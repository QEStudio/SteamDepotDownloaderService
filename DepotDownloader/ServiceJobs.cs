// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

// Modified by QEStudio (2026-01-26).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DepotDownloader
{
    enum ServiceJobState
    {
        Queued = 0,
        Starting = 1,
        Running = 2,
        Finalizing = 3,
        Succeeded = 4,
        Failed = 5,
        Canceled = 6,
    }

    enum LeaseRenewalResult
    {
        Renewed = 0,
        Missing = 1,
        NotOwner = 2,
    }

    sealed class ServiceJob
    {
        public Guid Id { get; init; }
        public ServiceJobState State { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string Error { get; set; }
        public string ProgressPhase { get; set; }
        public double? ProgressPercent { get; set; }
        public string ProgressDetail { get; set; }
        public DateTimeOffset? ProgressAt { get; set; }
        public bool UsesByteProgress { get; set; }
        public List<string> LogTail { get; } = new(capacity: 200);
        public ServiceInstallRequest Request { get; init; }
        public CancellationTokenSource Cancellation { get; set; }
    }

    sealed record ServiceEvent(Guid JobId, DateTimeOffset Timestamp, string Type, string Message);

    sealed class ServiceJobManager
    {
        private readonly ConcurrentDictionary<Guid, ServiceJob> jobs = new();
        private readonly int maxConcurrent;
        private readonly SemaphoreSlim runGate;
        private readonly ConcurrentDictionary<Guid, SubscriptionState> subscribers = new();
        private readonly CancellationTokenSource schedulerCts = new();
        private readonly SemaphoreSlim schedulerSignal = new(0, int.MaxValue);
        private readonly ConcurrentDictionary<Guid, byte> runningJobs = new();
        private readonly Guid ownerId;
        private readonly JobStore store;
        private readonly TimeSpan leaseTtl;
        private readonly int heartbeatIntervalMs;
        private readonly int snapshotThrottleMs;
        private readonly TimeSpan progressStaleTtl;
        private readonly TimeSpan downloadStallTtl;

        public ServiceJobManager()
        {
            maxConcurrent = ParseMaxConcurrent();
            runGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            ownerId = Guid.NewGuid();
            store = new JobStore(ParseDbPath());
            leaseTtl = ParseLeaseTtl();
            heartbeatIntervalMs = ParseHeartbeatIntervalMs();
            snapshotThrottleMs = ParseSnapshotThrottleMs();
            progressStaleTtl = ComputeProgressStaleTtl(leaseTtl);
            downloadStallTtl = ParseDownloadStallTtl();
            LoadJobsFromStore();
            _ = Task.Run(() => SchedulerLoopAsync(schedulerCts.Token));
        }

        public ChannelReader<ServiceEvent> Subscribe(out Guid subscriptionId)
        {
            subscriptionId = Guid.NewGuid();
            var channel = Channel.CreateBounded<ServiceEvent>(new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            subscribers[subscriptionId] = new SubscriptionState(channel);
            return channel.Reader;
        }

        public void Unsubscribe(Guid subscriptionId)
        {
            if (subscribers.TryRemove(subscriptionId, out var subscription))
            {
                subscription.Channel.Writer.TryComplete();
            }
        }

        public void StopAllJobs()
        {
            try
            {
                schedulerCts.Cancel();
            }
            catch
            {
            }

            foreach (var job in jobs.Values)
            {
                var publishSnapshot = false;
                var wasRunning = false;
                lock (job)
                {
                    if (job.State is ServiceJobState.Succeeded or ServiceJobState.Failed or ServiceJobState.Canceled)
                    {
                        continue;
                    }

                    wasRunning = job.State is ServiceJobState.Running or ServiceJobState.Starting or ServiceJobState.Finalizing;
                    try
                    {
                        job.Cancellation.Cancel();
                    }
                    catch
                    {
                    }

                    if (TryTransition(job, ServiceJobState.Canceled))
                    {
                        job.FinishedAt = DateTimeOffset.UtcNow;
                        job.ProgressPhase = "Canceled";
                        job.ProgressDetail = null;
                        job.ProgressAt = DateTimeOffset.UtcNow;
                        Publish(job.Id, "state", job.State.ToString());
                        publishSnapshot = true;
                    }
                }

                if (wasRunning)
                {
                    try
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                    catch
                    {
                    }
                }

                if (publishSnapshot)
                {
                    PublishJobsSnapshotThrottled(snapshotThrottleMs);
                }
            }
            store.ClearOwnerLeases(ownerId);
            store.Save();
        }

        public IReadOnlyCollection<ServiceJob> GetAllJobs() => jobs.Values.ToArray();

        public bool TryGetJob(Guid id, out ServiceJob job)
        {
            return jobs.TryGetValue(id, out job);
        }

        public string BuildJobsSnapshotJson()
        {
            var snapshot = GetAllJobs()
                .OrderByDescending(j => j.CreatedAt)
                .Select(j =>
                {
                    string phase;
                    double? percent;
                    string detail;
                    DateTimeOffset? updatedAt;
                    ServiceJobState state;
                    DateTimeOffset createdAt;
                    DateTimeOffset? startedAt;
                    DateTimeOffset? finishedAt;
                    string error;
                    ServiceInstallRequest request;
                    lock (j)
                    {
                        state = j.State;
                        createdAt = j.CreatedAt;
                        startedAt = j.StartedAt;
                        finishedAt = j.FinishedAt;
                        error = j.Error;
                        phase = j.ProgressPhase;
                        percent = j.ProgressPercent;
                        detail = j.ProgressDetail;
                        updatedAt = j.ProgressAt;
                        request = j.Request;
                    }

                    return new
                    {
                        id = j.Id,
                        state = state.ToString(),
                        createdAt,
                        startedAt,
                        finishedAt,
                        error,
                        progress = new
                        {
                            phase,
                            percent,
                            detail,
                            updatedAt,
                        },
                        request = new
                        {
                            appId = request.AppId,
                            depotId = request.DepotId,
                            manifestId = request.ManifestId,
                            branch = request.Branch,
                            dir = request.Dir,
                        }
                    };
                })
                .ToArray();

            return JsonSerializer.Serialize(snapshot);
        }

        private static int ParseMaxConcurrent()
        {
            var raw = Environment.GetEnvironmentVariable("STEAMDDS_MAX_CONCURRENT_JOBS");
            if (!int.TryParse(raw, out var value))
            {
                value = 1;
            }

            if (value < 1) value = 1;
            if (value > 8) value = 8;
            return value;
        }

        private static string ParseDbPath()
        {
            var raw = Environment.GetEnvironmentVariable("STEAMDDS_DB_PATH");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Path.Combine(AppContext.BaseDirectory, "steamdds.db");
            }
            return raw.Trim();
        }

        private static TimeSpan ParseLeaseTtl()
        {
            var raw = Environment.GetEnvironmentVariable("STEAMDDS_LEASE_TTL_SECONDS");
            if (!int.TryParse(raw, out var seconds)) seconds = 60;
            if (seconds < 10) seconds = 10;
            if (seconds > 3600) seconds = 3600;
            return TimeSpan.FromSeconds(seconds);
        }

        private static int ParseHeartbeatIntervalMs()
        {
            var raw = Environment.GetEnvironmentVariable("STEAMDDS_HEARTBEAT_INTERVAL_MS");
            if (!int.TryParse(raw, out var ms)) ms = 2000;
            if (ms < 200) ms = 200;
            if (ms > 30000) ms = 30000;
            return ms;
        }

        private static int ParseSnapshotThrottleMs()
        {
            var raw = Environment.GetEnvironmentVariable("STEAMDDS_SNAPSHOT_THROTTLE_MS");
            if (!int.TryParse(raw, out var ms)) ms = 300;
            if (ms < 50) ms = 50;
            if (ms > 5000) ms = 5000;
            return ms;
        }

        private static TimeSpan ParseDownloadStallTtl()
        {
            var raw = Environment.GetEnvironmentVariable("STEAMDDS_DOWNLOAD_STALL_SECONDS");
            if (!int.TryParse(raw, out var seconds)) seconds = 180;
            if (seconds < 30) seconds = 30;
            if (seconds > 3600) seconds = 3600;
            return TimeSpan.FromSeconds(seconds);
        }

        private static TimeSpan ComputeProgressStaleTtl(TimeSpan leaseTtl)
        {
            var seconds = Math.Clamp(leaseTtl.TotalSeconds * 5, 120, 1800);
            return TimeSpan.FromSeconds(seconds);
        }

        private static bool TryTransition(ServiceJob job, ServiceJobState next)
        {
            if (job.State == next)
            {
                return false;
            }

            var from = job.State;
            var ok = from switch
            {
                ServiceJobState.Queued => next is ServiceJobState.Starting or ServiceJobState.Canceled,
                ServiceJobState.Starting => next is ServiceJobState.Running or ServiceJobState.Canceled or ServiceJobState.Failed or ServiceJobState.Queued,
                ServiceJobState.Running => next is ServiceJobState.Finalizing or ServiceJobState.Failed or ServiceJobState.Canceled or ServiceJobState.Queued,
                ServiceJobState.Finalizing => next is ServiceJobState.Succeeded or ServiceJobState.Failed or ServiceJobState.Canceled or ServiceJobState.Queued,
                ServiceJobState.Failed => next is ServiceJobState.Queued or ServiceJobState.Canceled,
                ServiceJobState.Canceled => next is ServiceJobState.Queued,
                ServiceJobState.Succeeded => false,
                _ => false,
            };

            if (!ok)
            {
                return false;
            }

            job.State = next;
            return true;
        }

        private void LoadJobsFromStore()
        {
            var now = DateTimeOffset.UtcNow;
            store.RequeueOrphanedJobs(now, now - progressStaleTtl);
            store.Save();

            foreach (var record in store.LoadJobs())
            {
                var job = CreateJobFromRecord(record);
                jobs[job.Id] = job;
            }
        }

        private ServiceJob CreateJobFromRecord(JobRecord record)
        {
            ServiceInstallRequest request = null;
            if (!string.IsNullOrWhiteSpace(record.RequestJson))
            {
                try
                {
                    request = JsonSerializer.Deserialize<ServiceInstallRequest>(record.RequestJson);
                }
                catch
                {
                    request = null;
                }
            }

            request ??= new ServiceInstallRequest { AppId = 0 };

            var job = new ServiceJob
            {
                Id = record.Id,
                State = record.State,
                CreatedAt = record.CreatedAt,
                StartedAt = record.StartedAt,
                FinishedAt = record.FinishedAt,
                Error = record.Error,
                ProgressPhase = null,
                ProgressPercent = null,
                ProgressDetail = null,
                ProgressAt = record.FinishedAt ?? record.StartedAt ?? record.CreatedAt,
                Request = request,
                Cancellation = new CancellationTokenSource(),
            };

            switch (record.State)
            {
                case ServiceJobState.Queued:
                    job.ProgressPhase = "Queued";
                    job.ProgressPercent = 0;
                    break;
                case ServiceJobState.Starting:
                    job.ProgressPhase = "Starting";
                    job.ProgressPercent = 0.02;
                    break;
                case ServiceJobState.Running:
                    job.ProgressPhase = "Running";
                    job.ProgressPercent = 0.1;
                    break;
                case ServiceJobState.Finalizing:
                    job.ProgressPhase = "Finalizing";
                    job.ProgressPercent = 0.95;
                    break;
                case ServiceJobState.Succeeded:
                    job.ProgressPhase = "Succeeded";
                    job.ProgressPercent = 1;
                    break;
                case ServiceJobState.Failed:
                    job.ProgressPhase = "Failed";
                    job.ProgressPercent = null;
                    break;
                case ServiceJobState.Canceled:
                    job.ProgressPhase = "Canceled";
                    job.ProgressPercent = null;
                    break;
            }

            return job;
        }

        private void PublishJobsSnapshot()
        {
            if (subscribers.IsEmpty)
            {
                return;
            }
            var snapshot = BuildJobsSnapshotJson();
            var ev = new ServiceEvent(Guid.Empty, DateTimeOffset.UtcNow, "jobs", snapshot);
            foreach (var sub in subscribers.Values)
            {
                sub.Channel.Writer.TryWrite(ev);
                Interlocked.Exchange(ref sub.LastJobsSnapshotAtMs, Environment.TickCount64);
            }
        }

        public Guid EnqueueInstall(ServiceInstallRequest request)
        {
            var jobId = Guid.NewGuid();
            var job = new ServiceJob
            {
                Id = jobId,
                State = ServiceJobState.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                ProgressPhase = "Queued",
                ProgressPercent = 0,
                ProgressDetail = null,
                ProgressAt = DateTimeOffset.UtcNow,
                Request = request,
                Cancellation = new CancellationTokenSource(),
            };

            jobs[jobId] = job;
            store.UpsertJob(jobId, job.State.ToString(), job.CreatedAt, job.StartedAt, job.FinishedAt, JsonSerializer.Serialize(request), job.Error);
            store.Save();
            Publish(jobId, "state", job.State.ToString());
            PublishProgress(job, job.ProgressPhase, job.ProgressPercent, job.ProgressDetail);
            PublishJobsSnapshotThrottled(snapshotThrottleMs);

            SignalScheduler();
            return jobId;
        }

        public bool TryCancel(Guid jobId)
        {
            if (!jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            var wasRunning = false;
            var publishSnapshot = false;
            lock (job)
            {
                if (job.State is ServiceJobState.Succeeded or ServiceJobState.Failed or ServiceJobState.Canceled)
                {
                    return false;
                }

                wasRunning = job.State is ServiceJobState.Running or ServiceJobState.Starting or ServiceJobState.Finalizing;
                job.Cancellation.Cancel();

                if (TryTransition(job, ServiceJobState.Canceled))
                {
                    job.FinishedAt = DateTimeOffset.UtcNow;
                    Publish(job.Id, "state", job.State.ToString());
                    publishSnapshot = true;
                }

            }

            if (wasRunning)
            {
                try
                {
                    ContentDownloader.ShutdownSteam3();
                }
                catch
                {
                }
            }

            if (publishSnapshot)
            {
                PublishJobsSnapshotThrottled(snapshotThrottleMs);
            }
            store.RemoveLease(jobId);
            store.UpdateJob(jobId, state: ServiceJobState.Canceled.ToString(), startedAt: job.StartedAt, finishedAt: job.FinishedAt, requestJson: null, error: job.Error);
            store.Save();
            SignalScheduler();
            return true;
        }

        public bool TryRetry(Guid jobId, out Guid newJobId)
        {
            newJobId = Guid.Empty;

            if (!jobs.TryGetValue(jobId, out var job))
            {
                return false;
            }

            lock (job)
            {
                if (job.State != ServiceJobState.Failed && job.State != ServiceJobState.Canceled)
                {
                    return false;
                }

                try
                {
                    job.Cancellation.Cancel();
                }
                catch
                {
                }

                job.Cancellation = new CancellationTokenSource();
                if (TryTransition(job, ServiceJobState.Queued))
                {
                    job.StartedAt = null;
                    job.FinishedAt = null;
                    job.Error = null;
                    job.ProgressPhase = "Queued";
                    job.ProgressPercent = 0;
                    job.ProgressDetail = null;
                    job.ProgressAt = DateTimeOffset.UtcNow;
                    job.UsesByteProgress = false;
                }

                lock (job.LogTail)
                {
                    job.LogTail.Clear();
                }
            }

            Publish(job.Id, "state", job.State.ToString());
            PublishProgress(job, job.ProgressPhase, job.ProgressPercent, job.ProgressDetail);
            PublishJobsSnapshotThrottled(snapshotThrottleMs);

            store.RemoveLease(job.Id);
            store.UpdateJob(job.Id, state: ServiceJobState.Queued.ToString(), startedAt: null, finishedAt: null, requestJson: JsonSerializer.Serialize(job.Request), error: null);
            store.Save();
            SignalScheduler();
            newJobId = job.Id;
            return true;
        }

        public int ResetAllJobs(bool force)
        {
            var now = DateTimeOffset.UtcNow;
            var canceled = 0;
            var hadRunning = false;

            foreach (var job in jobs.Values)
            {
                var shouldPublish = false;
                lock (job)
                {
                    if (job.State is ServiceJobState.Succeeded)
                    {
                        continue;
                    }
                    if (!force && job.State is ServiceJobState.Failed or ServiceJobState.Canceled)
                    {
                        continue;
                    }

                    hadRunning |= job.State is ServiceJobState.Running or ServiceJobState.Starting or ServiceJobState.Finalizing;

                    try
                    {
                        job.Cancellation.Cancel();
                    }
                    catch
                    {
                    }

                    if (TryTransition(job, ServiceJobState.Canceled))
                    {
                        job.FinishedAt = now;
                        job.Error = null;
                        job.ProgressPhase = "Canceled";
                        job.ProgressPercent = null;
                        job.ProgressDetail = null;
                        job.ProgressAt = now;
                        canceled++;
                        Publish(job.Id, "state", job.State.ToString());
                        shouldPublish = true;
                    }
                }

                if (shouldPublish)
                {
                    store.RemoveLease(job.Id);
                    store.UpdateJob(job.Id, state: ServiceJobState.Canceled.ToString(), startedAt: job.StartedAt, finishedAt: job.FinishedAt, requestJson: null, error: job.Error);
                }
            }

            if (hadRunning || force)
            {
                try
                {
                    ContentDownloader.ShutdownSteam3();
                }
                catch
                {
                }
            }

            store.ClearOwnerLeases(ownerId);
            store.Save();
            PublishJobsSnapshotThrottled(snapshotThrottleMs);
            SignalScheduler();

            return canceled;
        }

        private void SignalScheduler()
        {
            try
            {
                schedulerSignal.Release();
            }
            catch
            {
            }
        }

        private async Task SchedulerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await schedulerSignal.WaitAsync(heartbeatIntervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var now = DateTimeOffset.UtcNow;
                var requeued = store.RequeueOrphanedJobs(now, now - progressStaleTtl);
                store.Save();
                if (requeued.Count > 0)
                {
                    foreach (var jobId in requeued)
                    {
                        if (!jobs.TryGetValue(jobId, out var orphaned))
                        {
                            continue;
                        }

                        lock (orphaned)
                        {
                            if (!TryTransition(orphaned, ServiceJobState.Queued))
                            {
                                continue;
                            }
                            orphaned.StartedAt = null;
                            orphaned.FinishedAt = null;
                            orphaned.Error = null;
                            orphaned.ProgressPhase = "Queued";
                            orphaned.ProgressPercent = 0;
                            orphaned.ProgressDetail = null;
                            orphaned.ProgressAt = now;
                        }
                        Publish(orphaned.Id, "state", orphaned.State.ToString());
                    }
                    PublishJobsSnapshotThrottled(snapshotThrottleMs);
                }

                var capacity = Math.Max(0, maxConcurrent - runningJobs.Count);
                if (capacity == 0)
                {
                    continue;
                }

                var acquired = store.TryAcquireQueuedJobs(capacity, ownerId, now, leaseTtl);
                if (acquired.Count == 0)
                {
                    continue;
                }

                foreach (var jobId in acquired)
                {
                    if (!jobs.TryGetValue(jobId, out var job))
                    {
                        var record = store.GetJob(jobId);
                        if (record == null)
                        {
                            continue;
                        }
                        job = CreateJobFromRecord(record);
                        jobs[jobId] = job;
                    }

                    var shouldStart = false;
                    lock (job)
                    {
                        if (job.State == ServiceJobState.Starting)
                        {
                            shouldStart = true;
                        }
                        else if (TryTransition(job, ServiceJobState.Starting))
                        {
                            shouldStart = true;
                        }

                        if (job.State == ServiceJobState.Canceled || job.Cancellation.IsCancellationRequested)
                        {
                            shouldStart = false;
                        }

                        if (shouldStart)
                        {
                            job.StartedAt ??= now;
                            job.ProgressPhase = "Starting";
                            job.ProgressPercent = 0.02;
                            job.ProgressDetail = null;
                            job.ProgressAt = now;
                        }
                    }
                    if (!shouldStart)
                    {
                        continue;
                    }
                    Publish(job.Id, "state", job.State.ToString());
                    PublishProgress(job, "Starting", 0.02, null);
                    PublishJobsSnapshotThrottled(snapshotThrottleMs);

                    runningJobs.TryAdd(job.Id, 0);
                    _ = Task.Run(() => RunInstallAsync(job));
                }
            }
        }

        private async Task RunInstallAsync(ServiceJob job)
        {
            var gateAcquired = false;
            try
            {
                if (!store.HasValidLease(job.Id, ownerId, DateTimeOffset.UtcNow))
                {
                    lock (job)
                    {
                        if (job.State == ServiceJobState.Canceled || job.Cancellation.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                    RequeueJob(job);
                    return;
                }

                await runGate.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
                gateAcquired = true;
                if (job.Cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException(job.Cancellation.Token);
                }
                if (!store.HasValidLease(job.Id, ownerId, DateTimeOffset.UtcNow))
                {
                    lock (job)
                    {
                        if (job.State == ServiceJobState.Canceled || job.Cancellation.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                    RequeueJob(job);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                var publishSnapshot = false;
                lock (job)
                {
                    if (TryTransition(job, ServiceJobState.Canceled))
                    {
                        job.FinishedAt = DateTimeOffset.UtcNow;
                        Publish(job.Id, "state", job.State.ToString());
                        publishSnapshot = true;
                    }
                }
                if (publishSnapshot)
                {
                    PublishJobsSnapshotThrottled(snapshotThrottleMs);
                }
                return;
            }

            CancellationTokenSource linkedCts = null;
            var timeoutMessage = string.Empty;
            var heartbeatCts = new CancellationTokenSource();
            Task heartbeatTask = null;
            var leaseLostCts = new CancellationTokenSource();
            var shouldRequeue = false;
            var stopDueToLeaseMismatch = false;

            try
            {
                var publishSnapshotOnStart = false;
                lock (job)
                {
                    if (job.State == ServiceJobState.Canceled || job.Cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!TryTransition(job, ServiceJobState.Running))
                    {
                        return;
                    }
                    var now = DateTimeOffset.UtcNow;
                    job.StartedAt ??= now;
                    job.ProgressPhase = "Starting";
                    job.ProgressPercent = 0.02;
                    job.ProgressDetail = null;
                    job.ProgressAt = now;
                    Publish(job.Id, "state", job.State.ToString());
                    publishSnapshotOnStart = true;
                }
                if (publishSnapshotOnStart)
                {
                    PublishJobsSnapshotThrottled(snapshotThrottleMs);
                }
                PublishProgress(job, "Starting", 0.02, null);
                store.UpdateJob(job.Id, state: ServiceJobState.Running.ToString(), startedAt: job.StartedAt, finishedAt: null, requestJson: null, error: null);
                store.Save();

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, leaseLostCts.Token);

                var runToken = linkedCts?.Token ?? job.Cancellation.Token;

                var originalOut = Console.Out;
                var originalErr = Console.Error;

                var writer = new JobConsoleWriter(originalOut, line =>
                {
                    UpdateProgressFromLog(job, line);
                    lock (job.LogTail)
                    {
                        job.LogTail.Add(line);
                        if (job.LogTail.Count > 200)
                        {
                            job.LogTail.RemoveAt(0);
                        }
                    }
                    Publish(job.Id, "log", line);
                });

                try
                {
                    Console.SetOut(writer);
                    Console.SetError(writer);

                    heartbeatTask = Task.Run(async () =>
                    {
                        var token = heartbeatCts.Token;
                        while (!token.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(heartbeatIntervalMs, token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            var now = DateTimeOffset.UtcNow;
                            var renewResult = store.TryRenewLease(job.Id, ownerId, now, leaseTtl);
                            if (renewResult == LeaseRenewalResult.Renewed)
                            {
                                store.Save();
                                continue;
                            }

                            if (renewResult == LeaseRenewalResult.Missing)
                            {
                                shouldRequeue = true;
                            }
                            else
                            {
                                stopDueToLeaseMismatch = true;
                            }

                            try
                            {
                                leaseLostCts.Cancel();
                            }
                            catch
                            {
                            }
                            break;
                        }
                    }, heartbeatCts.Token);

                    var previousConfig = ContentDownloader.Config;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(job.Request.Username))
                        {
                            var activeUser = AccountSettingsStore.Instance.ActiveUser;
                            if (!string.IsNullOrWhiteSpace(activeUser) && AccountSettingsStore.Instance.LoginTokens.ContainsKey(activeUser))
                            {
                                job.Request.Username = activeUser;
                                job.Request.Password = null;
                                job.Request.RememberPassword ??= true;
                            }
                        }

                        ContentDownloader.Config = BuildDownloadConfig(job.Request);

                        var maxAttempts = 3;
                        Exception lastError = null;
                        for (var attempt = 1; attempt <= maxAttempts; attempt++)
                        {
                            if (runToken.IsCancellationRequested)
                            {
                                throw new OperationCanceledException(runToken);
                            }

                            Console.WriteLine($"Install attempt {attempt}/{maxAttempts}");

                            var stallTriggered = false;
                            var stallReason = string.Empty;

                            try
                            {
                                if (!ContentDownloader.InitializeSteam3(job.Request.Username, job.Request.Password))
                                {
                                    throw new ContentDownloaderException("InitializeSteam3 failed");
                                }

                                lock (job)
                                {
                                    job.UsesByteProgress = false;
                                }

                                var lastTicks = 0L;
                                var lastPermille = -1;
                                ContentDownloader.ProgressCallback = (downloaded, total) =>
                                {
                                    if (runToken.IsCancellationRequested) return;
                                    if (total == 0) return;

                                    var ratio = Math.Clamp(downloaded / (double)total, 0.0, 1.0);
                                    var permille = (int)Math.Round(ratio * 1000.0);

                                    var now = Environment.TickCount64;
                                    var prevTicks = Interlocked.Read(ref lastTicks);
                                    var prevPermille = Volatile.Read(ref lastPermille);

                                    if (permille == prevPermille && now - prevTicks < 1000) return;
                                    if (now - prevTicks < 250 && permille - prevPermille < 2) return;

                                    Interlocked.Exchange(ref lastTicks, now);
                                    Volatile.Write(ref lastPermille, permille);

                                    lock (job)
                                    {
                                        job.UsesByteProgress = true;
                                    }

                                    var mapped = 0.3 + ratio * 0.65;
                                    var detail = $"{ratio * 100.0:0.00}% · {FormatBytes(downloaded)}/{FormatBytes(total)}";
                                    SetProgress(job, "Downloading", mapped, detail);
                                };

                                var depotManifestIds = new List<(uint depotId, ulong manifestId)>();
                                if (job.Request.DepotId != null)
                                {
                                    depotManifestIds.Add((job.Request.DepotId.Value, job.Request.ManifestId ?? ContentDownloader.INVALID_MANIFEST_ID));
                                }

                                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                                var attemptToken = attemptCts.Token;
                                var stallTask = Task.Run(async () =>
                                {
                                    while (!attemptToken.IsCancellationRequested)
                                    {
                                        try
                                        {
                                            await Task.Delay(2000, attemptToken).ConfigureAwait(false);
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            break;
                                        }

                                        DateTimeOffset? progressAt;
                                        double? percent;
                                        lock (job)
                                        {
                                            progressAt = job.ProgressAt;
                                            percent = job.ProgressPercent;
                                        }

                                        if (!progressAt.HasValue || !percent.HasValue)
                                        {
                                            continue;
                                        }

                                        if (percent.Value < 0.30 || percent.Value >= 0.95)
                                        {
                                            continue;
                                        }

                                        var staleFor = DateTimeOffset.UtcNow - progressAt.Value;
                                        if (staleFor <= downloadStallTtl)
                                        {
                                            continue;
                                        }

                                        stallTriggered = true;
                                        stallReason = $"Download stalled: no progress for {(int)downloadStallTtl.TotalSeconds}s";
                                        Console.WriteLine(stallReason);
                                        try
                                        {
                                            attemptCts.Cancel();
                                        }
                                        catch
                                        {
                                        }
                                        break;
                                    }
                                }, attemptToken);

                                try
                                {
                                    await ContentDownloader.DownloadAppAsync(
                                        job.Request.AppId,
                                        depotManifestIds,
                                        job.Request.Branch ?? ContentDownloader.DEFAULT_BRANCH,
                                        job.Request.Os,
                                        job.Request.Arch,
                                        job.Request.Language,
                                        job.Request.LowViolence ?? false,
                                        isUgc: false,
                                        cancellationToken: attemptToken
                                    ).ConfigureAwait(false);
                                }
                                finally
                                {
                                    try
                                    {
                                        attemptCts.Cancel();
                                    }
                                    catch
                                    {
                                    }
                                    try
                                    {
                                        await stallTask.ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                    }
                                }

                                lastError = null;
                                break;
                            }
                            catch (OperationCanceledException ex) when (!job.Cancellation.IsCancellationRequested)
                            {
                                if (stallTriggered && !string.IsNullOrWhiteSpace(stallReason))
                                {
                                    lastError = new TimeoutException(stallReason, ex);
                                }
                                else
                                {
                                    lastError = ex;
                                }
                            }
                            catch (Exception ex)
                            {
                                lastError = ex;
                            }
                            finally
                            {
                                ContentDownloader.ProgressCallback = null;
                                try
                                {
                                    ContentDownloader.ShutdownSteam3();
                                }
                                catch
                                {
                                }
                            }

                            if (lastError == null)
                            {
                                break;
                            }

                            if (!ShouldRetry(lastError) || attempt >= maxAttempts)
                            {
                                throw lastError;
                            }

                            var delay = RetryDelayMs(attempt);
                            var msg = lastError.Message;
                            var innerMsg = lastError.InnerException?.Message;
                            var suffix = innerMsg != null && !string.Equals(innerMsg, msg, StringComparison.Ordinal) ? $" | inner: {innerMsg}" : "";
                            Console.WriteLine($"Retrying in {delay}ms: {lastError.GetType().Name}: {msg}{suffix}");
                            await Task.Delay(delay, runToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        ContentDownloader.Config = previousConfig;
                    }

                    if (job.State is ServiceJobState.Canceled or ServiceJobState.Failed || runToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(runToken);
                    }

                    var publishSnapshotOnFinalizing = false;
                    lock (job)
                    {
                        if (TryTransition(job, ServiceJobState.Finalizing))
                        {
                            job.ProgressPhase = "Finalizing";
                            job.ProgressPercent = 0.95;
                            job.ProgressDetail = null;
                            job.ProgressAt = DateTimeOffset.UtcNow;
                            Publish(job.Id, "state", job.State.ToString());
                            publishSnapshotOnFinalizing = true;
                        }
                    }
                    if (publishSnapshotOnFinalizing)
                    {
                        PublishJobsSnapshotThrottled(snapshotThrottleMs);
                    }
                    PublishProgress(job, "Finalizing", 0.95, null);

                    var publishSnapshotOnSuccess = false;
                    lock (job)
                    {
                        if (TryTransition(job, ServiceJobState.Succeeded))
                        {
                            job.FinishedAt = DateTimeOffset.UtcNow;
                            job.ProgressPhase = "Succeeded";
                            job.ProgressPercent = 1;
                            job.ProgressDetail = null;
                            job.ProgressAt = DateTimeOffset.UtcNow;
                            Publish(job.Id, "state", job.State.ToString());
                            publishSnapshotOnSuccess = true;
                        }
                    }
                    if (publishSnapshotOnSuccess)
                    {
                        PublishJobsSnapshotThrottled(snapshotThrottleMs);
                    }
                    PublishProgress(job, "Succeeded", 1, null);
                    store.RemoveLease(job.Id);
                    store.UpdateJob(job.Id, state: ServiceJobState.Succeeded.ToString(), startedAt: job.StartedAt, finishedAt: job.FinishedAt, requestJson: null, error: null);
                    store.Save();
                }
                catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
                {
                    var publishSnapshotOnCancel = false;
                    lock (job)
                    {
                        if (TryTransition(job, ServiceJobState.Canceled))
                        {
                            job.FinishedAt = DateTimeOffset.UtcNow;
                            job.ProgressPhase = "Canceled";
                            job.ProgressDetail = null;
                            job.ProgressAt = DateTimeOffset.UtcNow;
                            Publish(job.Id, "state", job.State.ToString());
                            publishSnapshotOnCancel = true;
                        }
                    }
                    if (publishSnapshotOnCancel)
                    {
                        PublishJobsSnapshotThrottled(snapshotThrottleMs);
                    }
                    PublishProgress(job, "Canceled", null, null);
                    store.RemoveLease(job.Id);
                    store.UpdateJob(job.Id, state: ServiceJobState.Canceled.ToString(), startedAt: job.StartedAt, finishedAt: job.FinishedAt, requestJson: null, error: job.Error);
                    store.Save();
                }
                catch (OperationCanceledException ex)
                {
                    if (shouldRequeue)
                    {
                        RequeueJob(job);
                        return;
                    }
                    if (stopDueToLeaseMismatch)
                    {
                        return;
                    }
                    var publishSnapshotOnCanceledException = false;
                    var errorMessage = ex.Message;
                    if (!string.IsNullOrWhiteSpace(timeoutMessage))
                    {
                        errorMessage = timeoutMessage;
                    }
                    lock (job)
                    {
                        if (job.State == ServiceJobState.Canceled || job.Cancellation.IsCancellationRequested)
                        {
                            if (TryTransition(job, ServiceJobState.Canceled))
                            {
                                job.FinishedAt = DateTimeOffset.UtcNow;
                                Publish(job.Id, "state", job.State.ToString());
                                publishSnapshotOnCanceledException = true;
                            }
                        }
                        else if (TryTransition(job, ServiceJobState.Failed))
                        {
                            job.FinishedAt = DateTimeOffset.UtcNow;
                            job.Error = errorMessage;
                            job.ProgressPhase = "Failed";
                            job.ProgressDetail = errorMessage;
                            job.ProgressAt = DateTimeOffset.UtcNow;
                            Publish(job.Id, "state", job.State.ToString());
                            Publish(job.Id, "error", errorMessage);
                            publishSnapshotOnCanceledException = true;
                        }
                    }
                    if (publishSnapshotOnCanceledException)
                    {
                        PublishJobsSnapshotThrottled(snapshotThrottleMs);
                    }
                    PublishProgress(job, "Failed", null, errorMessage);
                    store.RemoveLease(job.Id);
                    store.UpdateJob(job.Id, state: ServiceJobState.Failed.ToString(), startedAt: job.StartedAt, finishedAt: job.FinishedAt, requestJson: null, error: errorMessage);
                    store.Save();
                }
                catch (Exception ex)
                {
                    var publishSnapshotOnException = false;
                    lock (job)
                    {
                        if (job.State == ServiceJobState.Canceled || job.Cancellation.IsCancellationRequested)
                        {
                            if (TryTransition(job, ServiceJobState.Canceled))
                            {
                                job.FinishedAt = DateTimeOffset.UtcNow;
                                Publish(job.Id, "state", job.State.ToString());
                                publishSnapshotOnException = true;
                            }
                        }
                        else if (TryTransition(job, ServiceJobState.Failed))
                        {
                            job.FinishedAt = DateTimeOffset.UtcNow;
                            job.Error = ex.Message;
                            job.ProgressPhase = "Failed";
                            job.ProgressDetail = ex.Message;
                            job.ProgressAt = DateTimeOffset.UtcNow;
                            Publish(job.Id, "state", job.State.ToString());
                            Publish(job.Id, "error", ex.Message);
                            publishSnapshotOnException = true;
                        }
                    }
                    if (publishSnapshotOnException)
                    {
                        PublishJobsSnapshotThrottled(snapshotThrottleMs);
                    }
                    PublishProgress(job, "Failed", null, ex.Message);
                    store.RemoveLease(job.Id);
                    store.UpdateJob(job.Id, state: ServiceJobState.Failed.ToString(), startedAt: job.StartedAt, finishedAt: job.FinishedAt, requestJson: null, error: ex.Message);
                    store.Save();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                    linkedCts?.Dispose();
                    try
                    {
                        heartbeatCts.Cancel();
                    }
                    catch
                    {
                    }
                    if (heartbeatTask != null)
                    {
                        try
                        {
                            await heartbeatTask.ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                    heartbeatCts.Dispose();
                }
            }
            finally
            {
                if (gateAcquired)
                {
                    try
                    {
                        runGate.Release();
                    }
                    catch
                    {
                    }
                }
                runningJobs.TryRemove(job.Id, out _);
                SignalScheduler();
            }
        }

        private static bool ShouldRetry(Exception ex)
        {
            if (ex is OperationCanceledException) return true;

            var msg = ex.Message ?? string.Empty;
            if (msg.Contains("not available from this account", StringComparison.OrdinalIgnoreCase)) return false;
            if (msg.Contains("appId is required", StringComparison.OrdinalIgnoreCase)) return false;
            if (msg.Contains("Couldn't find any depots", StringComparison.OrdinalIgnoreCase)) return false;
            if (msg.Contains("Depot ", StringComparison.OrdinalIgnoreCase) && msg.Contains("not listed", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }

        private static int RetryDelayMs(int attempt)
        {
            var baseMs = 800;
            var maxMs = 8000;
            var exp = Math.Min(maxMs, baseMs * (int)Math.Pow(2, Math.Max(0, attempt - 1)));
            var jitter = Random.Shared.Next(0, 200);
            return exp + jitter;
        }

        private static DownloadConfig BuildDownloadConfig(ServiceInstallRequest request)
        {
            var dnsServer = string.IsNullOrWhiteSpace(request.DnsServer) ? Environment.GetEnvironmentVariable("STEAMDDS_DNS_SERVER") : request.DnsServer;
            var httpProxy = string.IsNullOrWhiteSpace(request.HttpProxy) ? Environment.GetEnvironmentVariable("STEAMDDS_HTTP_PROXY") : request.HttpProxy;
            var rememberPassword = request.RememberPassword ?? false;
            if (!rememberPassword && !string.IsNullOrWhiteSpace(request.Username) && AccountSettingsStore.Instance.LoginTokens.ContainsKey(request.Username))
            {
                rememberPassword = true;
            }

            return new DownloadConfig
            {
                InstallDirectory = request.Dir,
                BetaPassword = request.BranchPassword,
                VerifyAll = request.Validate ?? false,
                MaxDownloads = request.MaxDownloads ?? 8,
                RememberPassword = rememberPassword,
                UseQrCode = false,
                SkipAppConfirmation = request.SkipAppConfirmation ?? false,
                DnsServer = dnsServer,
                HttpProxy = httpProxy,
            };
        }

        private void Publish(Guid jobId, string type, string message)
        {
            var ev = new ServiceEvent(jobId, DateTimeOffset.UtcNow, type, message);
            if (jobId != Guid.Empty)
            {
                store.AppendEvent(jobId, ev.Timestamp, type, message);
            }
            foreach (var sub in subscribers.Values)
            {
                sub.Channel.Writer.TryWrite(ev);
            }
        }

        private void PublishJobsSnapshotThrottled(int minIntervalMs)
        {
            if (subscribers.IsEmpty)
            {
                return;
            }
            var nowTicks = Environment.TickCount64;
            List<SubscriptionState> targets = null;
            foreach (var sub in subscribers.Values)
            {
                var last = Interlocked.Read(ref sub.LastJobsSnapshotAtMs);
                if (nowTicks - last < minIntervalMs)
                {
                    continue;
                }
                targets ??= new List<SubscriptionState>();
                targets.Add(sub);
            }
            if (targets == null || targets.Count == 0)
            {
                return;
            }
            var snapshot = BuildJobsSnapshotJson();
            var ev = new ServiceEvent(Guid.Empty, DateTimeOffset.UtcNow, "jobs", snapshot);
            foreach (var sub in targets)
            {
                sub.Channel.Writer.TryWrite(ev);
                Interlocked.Exchange(ref sub.LastJobsSnapshotAtMs, nowTicks);
            }
        }

        private void PublishProgress(ServiceJob job, string phase, double? percent, string detail)
        {
            var payload = JsonSerializer.Serialize(new { phase, percent, detail });
            Publish(job.Id, "progress", payload);
            PublishJobsSnapshotThrottled(snapshotThrottleMs);
        }

        private void SetProgress(ServiceJob job, string phase, double? percent, string detail)
        {
            var changed = false;
            lock (job)
            {
                if (!string.Equals(job.ProgressPhase, phase, StringComparison.Ordinal))
                {
                    job.ProgressPhase = phase;
                    changed = true;
                }

                if (job.ProgressPercent != percent)
                {
                    job.ProgressPercent = percent;
                    changed = true;
                }

                if (!string.Equals(job.ProgressDetail, detail, StringComparison.Ordinal))
                {
                    job.ProgressDetail = detail;
                    changed = true;
                }

                if (changed)
                {
                    job.ProgressAt = DateTimeOffset.UtcNow;
                }
            }

            if (changed)
            {
                PublishProgress(job, phase, percent, detail);
            }
        }

        private void UpdateProgressFromLog(ServiceJob job, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var t = line.Trim();

            var percentEndIdx = t.IndexOf('%');
            if (percentEndIdx > 0 && percentEndIdx <= 8)
            {
                lock (job)
                {
                    if (job.UsesByteProgress)
                    {
                        return;
                    }
                }
                var candidate = t[..percentEndIdx].Trim();
                if (double.TryParse(candidate, out var p) && p >= 0 && p <= 100)
                {
                    var mapped = 0.3 + (p / 100.0) * 0.65;
                    SetProgress(job, "Downloading Files", mapped, t);
                    return;
                }
            }

            if (t.StartsWith("Connecting to Steam3", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Connecting", 0.05, null);
                return;
            }

            if (t.StartsWith("Logging anonymously into Steam3", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Logging '", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Authenticating", 0.12, null);
                return;
            }

            if (t.StartsWith("Using Steam3 suggested CellID", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Preparing", 0.16, null);
                return;
            }

            if (t.StartsWith("Got AppInfo for ", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Fetching AppInfo", 0.22, t);
                return;
            }

            if (t.StartsWith("Using app branch:", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Selecting Branch", 0.26, t);
                return;
            }

            if (t.StartsWith("Got depot key for ", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Fetching Depot Keys", 0.34, t);
                return;
            }

            if (t.StartsWith("Processing depot ", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Processing Depot", 0.22, t);
                return;
            }

            if (t.StartsWith("Downloading depot ", StringComparison.OrdinalIgnoreCase) && t.EndsWith(" manifest", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Downloading Manifest", 0.26, t);
                return;
            }

            if (t.StartsWith("Total downloaded:", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Finalizing", 0.95, t);
                return;
            }

            if (t.Contains("was not completely downloaded", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Incomplete", null, t);
                return;
            }

            if (t.StartsWith("Disconnected from Steam", StringComparison.OrdinalIgnoreCase))
            {
                SetProgress(job, "Disconnecting", null, null);
            }
        }

        private static string FormatBytes(ulong bytes)
        {
            const double k = 1024.0;
            if (bytes < k) return $"{bytes} B";
            if (bytes < k * k) return $"{bytes / k:0.0} KiB";
            if (bytes < k * k * k) return $"{bytes / (k * k):0.0} MiB";
            return $"{bytes / (k * k * k):0.0} GiB";
        }

        private sealed class JobConsoleWriter : TextWriter
        {
            private readonly TextWriter inner;
            private readonly Action<string> onLine;
            private readonly StringBuilder buffer = new();

            public JobConsoleWriter(TextWriter inner, Action<string> onLine)
            {
                this.inner = inner;
                this.onLine = onLine;
            }

            public override Encoding Encoding => inner.Encoding;

            public override void Write(char value)
            {
                inner.Write(value);
                Append(value);
            }

            public override void Write(string value)
            {
                inner.Write(value);
                if (value != null)
                {
                    foreach (var ch in value)
                    {
                        Append(ch);
                    }
                }
            }

            public override void WriteLine(string value)
            {
                inner.WriteLine(value);
                if (value != null)
                {
                    foreach (var ch in value)
                    {
                        Append(ch);
                    }
                }
                Append('\n');
            }

            private void Append(char ch)
            {
                lock (buffer)
                {
                    if (ch == '\r')
                    {
                        return;
                    }

                    if (ch == '\n')
                    {
                        var line = buffer.ToString();
                        buffer.Clear();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            onLine(line);
                        }
                        return;
                    }

                    buffer.Append(ch);
                    if (buffer.Length > 4096)
                    {
                        var line = buffer.ToString();
                        buffer.Clear();
                        onLine(line);
                    }
                }
            }
        }

        private void RequeueJob(ServiceJob job)
        {
            var now = DateTimeOffset.UtcNow;
            var shouldPublish = false;
            lock (job)
            {
                if (!TryTransition(job, ServiceJobState.Queued))
                {
                    return;
                }
                job.StartedAt = null;
                job.FinishedAt = null;
                job.Error = null;
                job.ProgressPhase = "Queued";
                job.ProgressPercent = 0;
                job.ProgressDetail = null;
                job.ProgressAt = now;
                job.UsesByteProgress = false;
                Publish(job.Id, "state", job.State.ToString());
                shouldPublish = true;
            }
            if (shouldPublish)
            {
                PublishProgress(job, "Queued", 0, null);
            }
            store.RemoveLease(job.Id);
            store.UpdateJob(job.Id, state: ServiceJobState.Queued.ToString(), startedAt: null, finishedAt: null, requestJson: JsonSerializer.Serialize(job.Request), error: null);
            store.Save();
            SignalScheduler();
        }

        private sealed class SubscriptionState
        {
            public SubscriptionState(Channel<ServiceEvent> channel)
            {
                Channel = channel;
            }

            public Channel<ServiceEvent> Channel { get; }
            public long LastJobsSnapshotAtMs;
        }
    }

    sealed record JobRecord(
        Guid Id,
        ServiceJobState State,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? FinishedAt,
        string RequestJson,
        string Error
    );

    sealed class JobStore
    {
        private readonly string dbPath;
        private readonly object dbLock = new();

        public JobStore(string dbPath)
        {
            this.dbPath = dbPath;
            Initialize();
        }

        public void Save()
        {
        }

        public void UpsertJob(Guid id, string state, DateTimeOffset createdAt, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string requestJson, string error)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Jobs (id, state, createdAt, startedAt, finishedAt, requestJson, error)
                    VALUES ($id, $state, $createdAt, $startedAt, $finishedAt, $requestJson, $error)
                    ON CONFLICT(id) DO UPDATE SET
                        state = excluded.state,
                        createdAt = excluded.createdAt,
                        startedAt = excluded.startedAt,
                        finishedAt = excluded.finishedAt,
                        requestJson = excluded.requestJson,
                        error = excluded.error
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$state", state ?? ServiceJobState.Queued.ToString());
                cmd.Parameters.AddWithValue("$createdAt", ToDb(createdAt));
                cmd.Parameters.AddWithValue("$startedAt", ToDb(startedAt));
                cmd.Parameters.AddWithValue("$finishedAt", ToDb(finishedAt));
                cmd.Parameters.AddWithValue("$requestJson", requestJson == null ? DBNull.Value : requestJson);
                cmd.Parameters.AddWithValue("$error", error == null ? DBNull.Value : error);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateJob(Guid id, string state, DateTimeOffset? startedAt, DateTimeOffset? finishedAt, string requestJson, string error)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                var sql = "UPDATE Jobs SET state = $state, startedAt = $startedAt, finishedAt = $finishedAt, error = $error";
                if (requestJson != null)
                {
                    sql += ", requestJson = $requestJson";
                }
                sql += " WHERE id = $id";
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$state", state ?? ServiceJobState.Queued.ToString());
                cmd.Parameters.AddWithValue("$startedAt", ToDb(startedAt));
                cmd.Parameters.AddWithValue("$finishedAt", ToDb(finishedAt));
                cmd.Parameters.AddWithValue("$error", error == null ? DBNull.Value : error);
                if (requestJson != null)
                {
                    cmd.Parameters.AddWithValue("$requestJson", requestJson);
                }
                cmd.ExecuteNonQuery();
            }
        }

        public LeaseRenewalResult TryRenewLease(Guid jobId, Guid ownerId, DateTimeOffset now, TimeSpan leaseTtl)
        {
            var expiresAt = now.Add(leaseTtl);
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE Leases SET expiresAt = $expiresAt, renewedAt = $renewedAt
                    WHERE jobId = $jobId AND ownerId = $ownerId
                    """;
                cmd.Parameters.AddWithValue("$jobId", jobId.ToString());
                cmd.Parameters.AddWithValue("$ownerId", ownerId.ToString());
                cmd.Parameters.AddWithValue("$expiresAt", ToDb(expiresAt));
                cmd.Parameters.AddWithValue("$renewedAt", ToDb(now));
                if (cmd.ExecuteNonQuery() > 0)
                {
                    return LeaseRenewalResult.Renewed;
                }

                using var check = conn.CreateCommand();
                check.CommandText = "SELECT ownerId FROM Leases WHERE jobId = $jobId";
                check.Parameters.AddWithValue("$jobId", jobId.ToString());
                var result = check.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                {
                    return LeaseRenewalResult.Missing;
                }
                return LeaseRenewalResult.NotOwner;
            }
        }

        public bool HasValidLease(Guid jobId, Guid ownerId, DateTimeOffset now)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT expiresAt FROM Leases WHERE jobId = $jobId AND ownerId = $ownerId";
                cmd.Parameters.AddWithValue("$jobId", jobId.ToString());
                cmd.Parameters.AddWithValue("$ownerId", ownerId.ToString());
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                {
                    return false;
                }
                if (DateTimeOffset.TryParse(result.ToString(), out var expiresAt))
                {
                    return expiresAt > now;
                }
                return false;
            }
        }

        public void RemoveLease(Guid jobId)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Leases WHERE jobId = $jobId";
                cmd.Parameters.AddWithValue("$jobId", jobId.ToString());
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearOwnerLeases(Guid ownerId)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM Leases WHERE ownerId = $ownerId";
                cmd.Parameters.AddWithValue("$ownerId", ownerId.ToString());
                cmd.ExecuteNonQuery();
            }
        }

        public List<Guid> TryAcquireQueuedJobs(int capacity, Guid ownerId, DateTimeOffset now, TimeSpan leaseTtl)
        {
            var results = new List<Guid>();
            if (capacity <= 0)
            {
                return results;
            }

            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var tx = conn.BeginTransaction();

                var candidates = new List<Guid>();
                using (var select = conn.CreateCommand())
                {
                    select.Transaction = tx;
                    select.CommandText = """
                        SELECT j.id
                        FROM Jobs j
                        WHERE j.state = $state
                        ORDER BY j.createdAt
                        LIMIT $limit
                        """;
                    select.Parameters.AddWithValue("$state", ServiceJobState.Queued.ToString());
                    select.Parameters.AddWithValue("$limit", capacity);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        if (Guid.TryParse(reader.GetString(0), out var id))
                        {
                            candidates.Add(id);
                        }
                    }
                }

                var expiresAt = now.Add(leaseTtl);
                foreach (var id in candidates)
                {
                    using var update = conn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = "UPDATE Jobs SET state = $state, startedAt = $startedAt WHERE id = $id AND state = $queued";
                    update.Parameters.AddWithValue("$state", ServiceJobState.Starting.ToString());
                    update.Parameters.AddWithValue("$startedAt", ToDb(now));
                    update.Parameters.AddWithValue("$id", id.ToString());
                    update.Parameters.AddWithValue("$queued", ServiceJobState.Queued.ToString());
                    if (update.ExecuteNonQuery() == 0)
                    {
                        continue;
                    }

                    using var lease = conn.CreateCommand();
                    lease.Transaction = tx;
                    lease.CommandText = """
                        INSERT INTO Leases (jobId, ownerId, expiresAt, renewedAt)
                        VALUES ($jobId, $ownerId, $expiresAt, $renewedAt)
                        ON CONFLICT(jobId) DO UPDATE SET
                            ownerId = excluded.ownerId,
                            expiresAt = excluded.expiresAt,
                            renewedAt = excluded.renewedAt
                        """;
                    lease.Parameters.AddWithValue("$jobId", id.ToString());
                    lease.Parameters.AddWithValue("$ownerId", ownerId.ToString());
                    lease.Parameters.AddWithValue("$expiresAt", ToDb(expiresAt));
                    lease.Parameters.AddWithValue("$renewedAt", ToDb(now));
                    lease.ExecuteNonQuery();

                    results.Add(id);
                }

                tx.Commit();
            }

            return results;
        }

        public List<Guid> RequeueOrphanedJobs(DateTimeOffset now, DateTimeOffset staleBefore)
        {
            var results = new List<Guid>();
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using (var select = conn.CreateCommand())
                {
                    select.Transaction = tx;
                    select.CommandText = """
                        SELECT j.id
                        FROM Jobs j
                        LEFT JOIN Leases l ON l.jobId = j.id
                        LEFT JOIN (
                            SELECT jobId, MAX(ts) AS lastEventAt
                            FROM Events
                            WHERE type IN ('progress', 'log', 'state')
                            GROUP BY jobId
                        ) e ON e.jobId = j.id
                        WHERE j.state IN ($starting, $running, $finalizing)
                          AND (
                              l.jobId IS NULL
                              OR l.expiresAt <= $now
                              OR COALESCE(e.lastEventAt, j.startedAt, j.createdAt) <= $staleBefore
                          )
                        """;
                    select.Parameters.AddWithValue("$starting", ServiceJobState.Starting.ToString());
                    select.Parameters.AddWithValue("$running", ServiceJobState.Running.ToString());
                    select.Parameters.AddWithValue("$finalizing", ServiceJobState.Finalizing.ToString());
                    select.Parameters.AddWithValue("$now", ToDb(now));
                    select.Parameters.AddWithValue("$staleBefore", ToDb(staleBefore));
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        if (Guid.TryParse(reader.GetString(0), out var id))
                        {
                            results.Add(id);
                        }
                    }
                }

                if (results.Count == 0)
                {
                    tx.Commit();
                    return results;
                }

                var placeholders = string.Join(", ", results.Select((_, idx) => $"$id{idx}"));
                using (var update = conn.CreateCommand())
                {
                    update.Transaction = tx;
                    update.CommandText = $"UPDATE Jobs SET state = $queued, startedAt = NULL, finishedAt = NULL, error = NULL WHERE id IN ({placeholders})";
                    update.Parameters.AddWithValue("$queued", ServiceJobState.Queued.ToString());
                    for (var i = 0; i < results.Count; i++)
                    {
                        update.Parameters.AddWithValue($"$id{i}", results[i].ToString());
                    }
                    update.ExecuteNonQuery();
                }

                using (var delete = conn.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText = $"DELETE FROM Leases WHERE jobId IN ({placeholders})";
                    for (var i = 0; i < results.Count; i++)
                    {
                        delete.Parameters.AddWithValue($"$id{i}", results[i].ToString());
                    }
                    delete.ExecuteNonQuery();
                }

                tx.Commit();
            }

            return results;
        }

        public JobRecord GetJob(Guid id)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, state, createdAt, startedAt, finishedAt, requestJson, error FROM Jobs WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id.ToString());
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }
                return ReadJobRecord(reader);
            }
        }

        public IEnumerable<JobRecord> LoadJobs()
        {
            var list = new List<JobRecord>();
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, state, createdAt, startedAt, finishedAt, requestJson, error FROM Jobs ORDER BY createdAt DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(ReadJobRecord(reader));
                }
            }
            return list;
        }

        private JobRecord ReadJobRecord(SqliteDataReader reader)
        {
            var id = Guid.TryParse(reader.GetString(0), out var parsed) ? parsed : Guid.Empty;
            var stateRaw = reader.IsDBNull(1) ? ServiceJobState.Queued.ToString() : reader.GetString(1);
            if (!Enum.TryParse<ServiceJobState>(stateRaw, out var state))
            {
                state = ServiceJobState.Queued;
            }

            var createdAt = ParseDate(reader, 2) ?? DateTimeOffset.UtcNow;
            var startedAt = ParseDate(reader, 3);
            var finishedAt = ParseDate(reader, 4);
            var requestJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            var error = reader.IsDBNull(6) ? null : reader.GetString(6);

            return new JobRecord(id, state, createdAt, startedAt, finishedAt, requestJson, error);
        }

        private void Initialize()
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS Jobs (
                        id TEXT PRIMARY KEY,
                        state TEXT NOT NULL,
                        createdAt TEXT NOT NULL,
                        startedAt TEXT NULL,
                        finishedAt TEXT NULL,
                        requestJson TEXT NULL,
                        error TEXT NULL
                    );
                    CREATE TABLE IF NOT EXISTS Leases (
                        jobId TEXT PRIMARY KEY,
                        ownerId TEXT NOT NULL,
                        expiresAt TEXT NOT NULL,
                        renewedAt TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS Events (
                        jobId TEXT NOT NULL,
                        ts TEXT NOT NULL,
                        type TEXT NOT NULL,
                        payload TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_events_job_ts ON Events (jobId, ts);
                    """;
                cmd.ExecuteNonQuery();
            }
        }

        public void AppendEvent(Guid jobId, DateTimeOffset ts, string type, string payload)
        {
            lock (dbLock)
            {
                using var conn = Open();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO Events (jobId, ts, type, payload)
                    VALUES ($jobId, $ts, $type, $payload)
                    """;
                cmd.Parameters.AddWithValue("$jobId", jobId.ToString());
                cmd.Parameters.AddWithValue("$ts", ToDb(ts));
                cmd.Parameters.AddWithValue("$type", type ?? string.Empty);
                cmd.Parameters.AddWithValue("$payload", payload == null ? DBNull.Value : payload);
                cmd.ExecuteNonQuery();
            }
        }

        private SqliteConnection Open()
        {
            return new SqliteConnection($"Data Source={dbPath}");
        }

        private static string ToDb(DateTimeOffset value)
        {
            return value.ToString("O");
        }

        private static object ToDb(DateTimeOffset? value)
        {
            return value.HasValue ? value.Value.ToString("O") : DBNull.Value;
        }

        private static DateTimeOffset? ParseDate(SqliteDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
            {
                return null;
            }
            var raw = reader.GetString(index);
            if (DateTimeOffset.TryParse(raw, out var value))
            {
                return value;
            }
            return null;
        }
    }

    sealed class ServiceInstallRequest
    {
        public uint AppId { get; set; }
        public uint? DepotId { get; set; }
        public ulong? ManifestId { get; set; }
        public string Branch { get; set; }
        public string BranchPassword { get; set; }
        public string Os { get; set; }
        public string Arch { get; set; }
        public string Language { get; set; }
        public bool? LowViolence { get; set; }
        public string Dir { get; set; }
        public bool? Validate { get; set; }
        public int? MaxDownloads { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool? RememberPassword { get; set; }
        public bool? SkipAppConfirmation { get; set; }
        public string DnsServer { get; set; }
        public string HttpProxy { get; set; }
    }
}
