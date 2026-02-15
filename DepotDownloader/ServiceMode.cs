// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

// Modified by QEStudio (2026-01-26).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;

namespace DepotDownloader
{
    static class ServiceMode
    {
        private static readonly JsonSerializerOptions WebSocketJsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly object AccountLock = new();
        private static AccountStateSnapshot AccountState = new()
        {
            Status = "none",
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        public static bool ShouldRun(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var env = Environment.GetEnvironmentVariable("STEAMDDS_SERVICE");
            return string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool PersistLogin;

        public static async Task<int> RunAsync()
        {
            Ansi.Init();
            DebugLog.Enabled = false;
            var persistEnv = (Environment.GetEnvironmentVariable("STEAMDDS_LOGIN_PERSIST") ?? string.Empty).Trim();
            PersistLogin = string.Equals(persistEnv, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(persistEnv, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(persistEnv, "yes", StringComparison.OrdinalIgnoreCase);
            if (PersistLogin)
            {
                AccountSettingsStore.LoadFromFile("account.config");
            }
            else
            {
                AccountSettingsStore.InitEmpty();
            }

            var apiKey = Environment.GetEnvironmentVariable("STEAMDDS_API_KEY");
            var listenMode = (Environment.GetEnvironmentVariable("STEAMDDS_LISTEN_MODE") ?? "tcp").Trim();
            var listenUrl = (Environment.GetEnvironmentVariable("STEAMDDS_LISTEN_URL") ?? "http://127.0.0.1:8080").Trim();
            var unixSocketPath = (Environment.GetEnvironmentVariable("STEAMDDS_UNIX_SOCKET_PATH") ?? "/tmp/steamdds.sock").Trim();
            var corsOriginsRaw = (Environment.GetEnvironmentVariable("STEAMDDS_CORS_ORIGINS") ?? "http://localhost:5173,http://127.0.0.1:5173").Trim();

            var manager = new ServiceJobManager();

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    if (string.Equals(corsOriginsRaw, "*", StringComparison.Ordinal))
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                        policy.SetPreflightMaxAge(TimeSpan.FromMinutes(10));
                        return;
                    }

                    var origins = corsOriginsRaw
                        .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                    if (origins.Length == 0)
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                        policy.SetPreflightMaxAge(TimeSpan.FromMinutes(10));
                        return;
                    }

                    policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
                    policy.SetPreflightMaxAge(TimeSpan.FromMinutes(10));
                });
            });
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (string.Equals(listenMode, "unix", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.Exists(unixSocketPath))
                        {
                            File.Delete(unixSocketPath);
                        }
                    }
                    catch
                    {
                    }

                    options.ListenUnixSocket(unixSocketPath);
                }
            });

            if (!string.Equals(listenMode, "unix", StringComparison.OrdinalIgnoreCase))
            {
                builder.WebHost.UseUrls(listenUrl);
            }

            var app = builder.Build();

            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
            });

            app.UseCors();
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                manager.StopAllJobs();
                _ = Task.Run(() =>
                {
                    Thread.Sleep(300);
                    Environment.Exit(0);
                });
            });

            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path == "/health")
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                if (HttpMethods.IsOptions(ctx.Request.Method))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                if (TryGetApiKey(ctx.Request, out var providedKey) && string.Equals(providedKey, apiKey, StringComparison.Ordinal))
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("Unauthorized").ConfigureAwait(false);
            });

            app.MapGet("/health", () =>
            {
                return Results.Json(new
                {
                    name = "SteamDepotDownloaderService",
                    status = "ok",
                });
            });

            app.MapGet("/api/account/state", () =>
            {
                return Results.Json(GetAccountStateSnapshot());
            });

            app.MapPost("/api/account/qr", async () =>
            {
                try
                {
                    var snapshot = await StartQrLoginAsync().ConfigureAwait(false);
                    return Results.Json(snapshot);
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message);
                }
            });

            app.MapPost("/api/install", (ServiceInstallRequest req) =>
            {
                if (req == null || req.AppId == 0)
                {
                    return Results.BadRequest(new { error = "appId is required" });
                }

                var jobId = manager.EnqueueInstall(req);
                return Results.Json(new { jobId });
            });

            app.MapGet("/api/jobs", () =>
            {
                var jobs = manager.GetAllJobs()
                    .OrderByDescending(j => j.CreatedAt)
                    .Select(j =>
                    {
                        lock (j)
                        {
                            return new
                            {
                                id = j.Id,
                                state = j.State.ToString(),
                                createdAt = j.CreatedAt,
                                startedAt = j.StartedAt,
                                finishedAt = j.FinishedAt,
                                error = j.Error,
                                progress = new
                                {
                                    phase = j.ProgressPhase,
                                    percent = j.ProgressPercent,
                                    detail = j.ProgressDetail,
                                    updatedAt = j.ProgressAt,
                                },
                                request = new
                                {
                                    appId = j.Request.AppId,
                                    depotId = j.Request.DepotId,
                                    manifestId = j.Request.ManifestId,
                                    branch = j.Request.Branch,
                                    dir = j.Request.Dir,
                                }
                            };
                        }
                    });

                return Results.Json(jobs);
            });

            app.MapGet("/api/jobs/{id:guid}", (Guid id) =>
            {
                if (!manager.TryGetJob(id, out var job))
                {
                    return Results.NotFound();
                }

                List<string> tail;
                lock (job.LogTail)
                {
                    tail = job.LogTail.ToList();
                }

                string phase;
                double? percent;
                string detail;
                DateTimeOffset? updatedAt;
                lock (job)
                {
                    phase = job.ProgressPhase;
                    percent = job.ProgressPercent;
                    detail = job.ProgressDetail;
                    updatedAt = job.ProgressAt;
                }

                return Results.Json(new
                {
                    id = job.Id,
                    state = job.State.ToString(),
                    createdAt = job.CreatedAt,
                    startedAt = job.StartedAt,
                    finishedAt = job.FinishedAt,
                    error = job.Error,
                    progress = new
                    {
                        phase,
                        percent,
                        detail,
                        updatedAt,
                    },
                    logs = tail,
                    request = job.Request,
                });
            });

            app.MapDelete("/api/jobs/{id:guid}", (HttpContext ctx, Guid id) =>
            {
                var force = false;
                if (ctx.Request.Query.TryGetValue("force", out var forceRaw))
                {
                    force = IsTruthy(forceRaw.ToString());
                }

                if (!manager.TryGetJob(id, out _))
                {
                    return Results.NotFound();
                }

                if (!manager.TryCancel(id))
                {
                    return Results.Conflict(new { error = "job is not cancelable" });
                }

                if (force)
                {
                    try
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                    catch
                    {
                    }
                }

                return Results.Json(new { ok = true });
            });

            app.MapPost("/api/jobs/{id:guid}/retry", (Guid id) =>
            {
                if (!manager.TryGetJob(id, out _))
                {
                    return Results.NotFound();
                }

                if (!manager.TryRetry(id, out var jobId))
                {
                    return Results.Conflict(new { error = "job is not retryable" });
                }

                return Results.Json(new { jobId });
            });

            app.MapPost("/api/jobs/reset", (HttpContext ctx, ResetJobsRequest req) =>
            {
                var force = req?.Force ?? false;
                if (!force && ctx.Request.Query.TryGetValue("force", out var forceRaw))
                {
                    force = IsTruthy(forceRaw.ToString());
                }

                var canceled = manager.ResetAllJobs(force);
                return Results.Json(new { ok = true, canceled });
            });

            app.MapGet("/ws", async (HttpContext ctx) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsync("Expected WebSocket request").ConfigureAwait(false);
                    return;
                }

                using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

                await SendJsonAsync(socket, new ServiceEvent(Guid.Empty, DateTimeOffset.UtcNow, "jobs", manager.BuildJobsSnapshotJson()), ctx.RequestAborted).ConfigureAwait(false);

                var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                var subscriptionToken = cts.Token;

                var inbound = Task.CompletedTask;
                try
                {
                    inbound = Task.Run(async () =>
                    {
                        var buffer = new byte[16 * 1024];
                        using var ms = new MemoryStream();
                        while (!subscriptionToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                        {
                            ms.SetLength(0);
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await socket.ReceiveAsync(buffer, subscriptionToken).ConfigureAwait(false);
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    cts.Cancel();
                                    return;
                                }
                                if (result.Count > 0)
                                {
                                    ms.Write(buffer, 0, result.Count);
                                }
                            } while (!result.EndOfMessage);

                            if (result.MessageType != WebSocketMessageType.Text)
                            {
                                continue;
                            }

                            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                            try
                            {
                                using var doc = JsonDocument.Parse(text);
                                var root = doc.RootElement;
                                if (root.ValueKind != JsonValueKind.Object) continue;
                                if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
                                var type = typeEl.GetString() ?? string.Empty;

                                string requestId = null;
                                if (root.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
                                {
                                    requestId = ridEl.GetString();
                                }

                                if (string.Equals(type, "listJobs", StringComparison.OrdinalIgnoreCase))
                                {
                                    await SendJsonAsync(socket, new ServiceEvent(Guid.Empty, DateTimeOffset.UtcNow, "jobs", manager.BuildJobsSnapshotJson()), subscriptionToken).ConfigureAwait(false);
                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, Guid.Empty, requestId, ok: true, data: new { type = "listJobs" }, subscriptionToken).ConfigureAwait(false);
                                    }
                                    continue;
                                }

                                if (string.Equals(type, "getAccountState", StringComparison.OrdinalIgnoreCase))
                                {
                                    var snapshot = GetAccountStateSnapshot();
                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, Guid.Empty, requestId, ok: true, data: new { type = "getAccountState", state = snapshot }, subscriptionToken).ConfigureAwait(false);
                                    }
                                    continue;
                                }

                                if (string.Equals(type, "startQrLogin", StringComparison.OrdinalIgnoreCase))
                                {
                                    AccountStateSnapshot snapshot;
                                    try
                                    {
                                        snapshot = await StartQrLoginAsync().ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        snapshot = new AccountStateSnapshot
                                        {
                                            Status = "error",
                                            Message = ex.Message,
                                            UpdatedAt = DateTimeOffset.UtcNow,
                                        };
                                        SetAccountState(snapshot);
                                    }

                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, Guid.Empty, requestId, ok: snapshot.Status != "error", data: new { type = "startQrLogin", state = snapshot }, subscriptionToken, error: snapshot.Status == "error" ? snapshot.Message : null).ConfigureAwait(false);
                                    }
                                    continue;
                                }

                                if (string.Equals(type, "getJob", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!TryParseGuid(root, "jobId", out var targetJobId) || !manager.TryGetJob(targetJobId, out var job))
                                    {
                                        if (requestId != null)
                                        {
                                            await SendRpcAsync(socket, Guid.Empty, requestId, ok: false, data: null, subscriptionToken, error: "not found").ConfigureAwait(false);
                                        }
                                        continue;
                                    }

                                    await SendJobSnapshotAsync(socket, job, subscriptionToken).ConfigureAwait(false);
                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, targetJobId, requestId, ok: true, data: new { type = "getJob", jobId = targetJobId }, subscriptionToken).ConfigureAwait(false);
                                    }
                                    continue;
                                }

                                if (string.Equals(type, "install", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!root.TryGetProperty("request", out var reqEl))
                                    {
                                        if (requestId != null)
                                        {
                                            await SendRpcAsync(socket, Guid.Empty, requestId, ok: false, data: null, subscriptionToken, error: "request is required").ConfigureAwait(false);
                                        }
                                        continue;
                                    }

                                    ServiceInstallRequest req;
                                    try
                                    {
                                        req = JsonSerializer.Deserialize<ServiceInstallRequest>(reqEl.GetRawText(), WebSocketJsonOptions);
                                    }
                                    catch
                                    {
                                        req = null;
                                    }

                                    if (req == null || req.AppId == 0)
                                    {
                                        if (requestId != null)
                                        {
                                            await SendRpcAsync(socket, Guid.Empty, requestId, ok: false, data: null, subscriptionToken, error: "appId is required").ConfigureAwait(false);
                                        }
                                        continue;
                                    }

                                    var newJobId = manager.EnqueueInstall(req);
                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, newJobId, requestId, ok: true, data: new { type = "install", jobId = newJobId }, subscriptionToken).ConfigureAwait(false);
                                    }
                                    continue;
                                }

                                if (string.Equals(type, "cancelJob", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!TryParseGuid(root, "jobId", out var targetJobId) || !manager.TryGetJob(targetJobId, out _))
                                    {
                                        if (requestId != null)
                                        {
                                            await SendRpcAsync(socket, Guid.Empty, requestId, ok: false, data: null, subscriptionToken, error: "not found").ConfigureAwait(false);
                                        }
                                        continue;
                                    }

                                    var ok = manager.TryCancel(targetJobId);
                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, targetJobId, requestId, ok, data: new { type = "cancelJob", jobId = targetJobId }, subscriptionToken, error: ok ? null : "job is not cancelable").ConfigureAwait(false);
                                    }
                                    continue;
                                }

                                if (string.Equals(type, "retryJob", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!TryParseGuid(root, "jobId", out var targetJobId) || !manager.TryGetJob(targetJobId, out _))
                                    {
                                        if (requestId != null)
                                        {
                                            await SendRpcAsync(socket, Guid.Empty, requestId, ok: false, data: null, subscriptionToken, error: "not found").ConfigureAwait(false);
                                        }
                                        continue;
                                    }

                                    var ok = manager.TryRetry(targetJobId, out var newJobId);
                                    if (requestId != null)
                                    {
                                        await SendRpcAsync(socket, newJobId, requestId, ok, data: new { type = "retryJob", jobId = newJobId }, subscriptionToken, error: ok ? null : "job is not retryable").ConfigureAwait(false);
                                    }
                                    continue;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }, subscriptionToken);

                    var reader = manager.Subscribe(out var subscriptionId);
                    try
                    {
                        await foreach (var ev in reader.ReadAllAsync(subscriptionToken).ConfigureAwait(false))
                        {
                            await SendJsonAsync(socket, ev, subscriptionToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (WebSocketException)
                    {
                    }
                    finally
                    {
                        manager.Unsubscribe(subscriptionId);
                    }
                }
                finally
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                    try
                    {
                        await inbound.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            });

            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }

        private sealed class ResetJobsRequest
        {
            public bool Force { get; set; }
        }

        private static bool IsTruthy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var v = raw.Trim();
            return string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetApiKey(HttpRequest request, out string apiKey)
        {
            apiKey = null;

            if (request.Headers.TryGetValue("X-Api-Key", out var xApiKey) && !string.IsNullOrWhiteSpace(xApiKey))
            {
                apiKey = xApiKey.ToString();
                return true;
            }

            if (request.Headers.TryGetValue("Authorization", out var authValues))
            {
                var auth = authValues.ToString();
                const string BearerPrefix = "Bearer ";
                if (auth.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    apiKey = auth[BearerPrefix.Length..].Trim();
                    return !string.IsNullOrWhiteSpace(apiKey);
                }
            }

            if (request.Path == "/ws" && request.Query.TryGetValue("apiKey", out var apiKeyValue) && !string.IsNullOrWhiteSpace(apiKeyValue))
            {
                apiKey = apiKeyValue.ToString().Trim();
                return !string.IsNullOrWhiteSpace(apiKey);
            }

            return false;
        }

        private static async Task SendJsonAsync(WebSocket socket, ServiceEvent ev, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(ev, WebSocketJsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }

        private static async Task SendRpcAsync(WebSocket socket, Guid jobId, string requestId, bool ok, object data, CancellationToken cancellationToken, string error = null)
        {
            var payload = JsonSerializer.Serialize(new { requestId, ok, data, error });
            await SendJsonAsync(socket, new ServiceEvent(jobId, DateTimeOffset.UtcNow, "rpc", payload), cancellationToken).ConfigureAwait(false);
        }

        private static AccountStateSnapshot GetAccountStateSnapshot()
        {
            lock (AccountLock)
            {
                if (AccountState.Status == "pending" || AccountState.Status == "starting" || AccountState.Status == "error")
                {
                    return CloneAccountState(AccountState);
                }
            }

            var activeUser = AccountSettingsStore.Instance.ActiveUser;
            if (!string.IsNullOrWhiteSpace(activeUser) && AccountSettingsStore.Instance.LoginTokens.ContainsKey(activeUser))
            {
                return new AccountStateSnapshot
                {
                    Status = "ready",
                    Username = activeUser,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            return new AccountStateSnapshot
            {
                Status = "none",
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        private static AccountStateSnapshot CloneAccountState(AccountStateSnapshot state)
        {
            return new AccountStateSnapshot
            {
                Status = state.Status,
                Username = state.Username,
                Message = state.Message,
                QrUrl = state.QrUrl,
                QrAscii = state.QrAscii,
                UpdatedAt = state.UpdatedAt,
            };
        }

        private static void SetAccountState(AccountStateSnapshot snapshot)
        {
            lock (AccountLock)
            {
                AccountState = snapshot;
            }
        }

        private static async Task<AccountStateSnapshot> StartQrLoginAsync()
        {
            lock (AccountLock)
            {
                if (AccountState.Status == "pending")
                {
                    return CloneAccountState(AccountState);
                }
                AccountState = new AccountStateSnapshot
                {
                    Status = "starting",
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            var clientConfiguration = SteamConfiguration.Create(config =>
                config.WithHttpClientFactory(static purpose => HttpClientFactory.CreateHttpClient())
            );
            var client = new SteamClient(clientConfiguration);
            var callbacks = new CallbackManager(client);
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            callbacks.Subscribe<SteamClient.ConnectedCallback>(_ =>
            {
                connected.TrySetResult(true);
            });
            callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            {
                if (!connected.Task.IsCompleted)
                {
                    connected.TrySetException(new InvalidOperationException("Steam connect failed"));
                }
            });

            client.Connect();

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var connectReg = connectCts.Token.Register(() =>
            {
                connected.TrySetException(new TimeoutException("Steam connect timeout"));
            });

            using var callbackCts = new CancellationTokenSource();
            var callbackTask = Task.Run(() =>
            {
                while (!callbackCts.IsCancellationRequested)
                {
                    callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                }
            });

            try
            {
                await connected.Task.ConfigureAwait(false);
            }
            catch
            {
                callbackCts.Cancel();
                try
                {
                    await callbackTask.ConfigureAwait(false);
                }
                catch
                {
                }
                try
                {
                    client.Disconnect();
                }
                catch
                {
                }
                throw;
            }

            var session = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
            {
                DeviceFriendlyName = nameof(DepotDownloader),
                IsPersistentSession = true,
            }).ConfigureAwait(false);

            session.ChallengeURLChanged = () =>
            {
                var next = BuildPendingAccountState(session.ChallengeURL);
                SetAccountState(next);
            };

            var pending = BuildPendingAccountState(session.ChallengeURL);
            SetAccountState(pending);

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await session.PollingWaitForResultAsync().ConfigureAwait(false);
                    if (result.NewGuardData != null)
                    {
                        AccountSettingsStore.Instance.GuardData[result.AccountName] = result.NewGuardData;
                    }
                    else
                    {
                        AccountSettingsStore.Instance.GuardData.Remove(result.AccountName);
                    }

                    AccountSettingsStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
                    AccountSettingsStore.Instance.ActiveUser = result.AccountName;
                    if (PersistLogin)
                    {
                        AccountSettingsStore.Save();
                    }

                    SetAccountState(new AccountStateSnapshot
                    {
                        Status = "ready",
                        Username = result.AccountName,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
                catch (Exception ex)
                {
                    SetAccountState(new AccountStateSnapshot
                    {
                        Status = "error",
                        Message = ex.Message,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                }
                finally
                {
                    callbackCts.Cancel();
                    try
                    {
                        await callbackTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                    try
                    {
                        client.Disconnect();
                    }
                    catch
                    {
                    }
                }
            });

            return pending;
        }

        private static AccountStateSnapshot BuildPendingAccountState(string challengeUrl)
        {
            return new AccountStateSnapshot
            {
                Status = "pending",
                QrUrl = challengeUrl,
                QrAscii = BuildQrAscii(challengeUrl),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        private static string BuildQrAscii(string challengeUrl)
        {
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var lines = qrCode.GetLineByLineGraphic(1, drawQuietZones: true);
            return string.Join('\n', lines);
        }

        private static bool TryParseGuid(JsonElement root, string name, out Guid id)
        {
            id = Guid.Empty;
            if (!root.TryGetProperty(name, out var el)) return false;
            if (el.ValueKind != JsonValueKind.String) return false;
            var s = el.GetString();
            return Guid.TryParse(s, out id);
        }

        private static async Task SendJobSnapshotAsync(WebSocket socket, ServiceJob job, CancellationToken cancellationToken)
        {
            List<string> tail;
            lock (job.LogTail)
            {
                tail = job.LogTail.ToList();
            }

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
            lock (job)
            {
                state = job.State;
                createdAt = job.CreatedAt;
                startedAt = job.StartedAt;
                finishedAt = job.FinishedAt;
                error = job.Error;
                phase = job.ProgressPhase;
                percent = job.ProgressPercent;
                detail = job.ProgressDetail;
                updatedAt = job.ProgressAt;
                request = job.Request;
            }

            var snapshot = new
            {
                id = job.Id,
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
                logs = tail,
                request,
            };

            await SendJsonAsync(socket, new ServiceEvent(job.Id, DateTimeOffset.UtcNow, "job", JsonSerializer.Serialize(snapshot, WebSocketJsonOptions)), cancellationToken).ConfigureAwait(false);
        }

        private sealed class AccountStateSnapshot
        {
            public string Status { get; set; }
            public string Username { get; set; }
            public string Message { get; set; }
            public string QrUrl { get; set; }
            public string QrAscii { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }
    }
}
