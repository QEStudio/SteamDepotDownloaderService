import { Badge, Button, Card, Code, Group, Loader, Progress, ScrollArea, Stack, Switch, Text, TextInput } from '@mantine/core'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

import type { ServiceEvent, ServiceJobDetail } from '../lib/types'

type ProgressPayload = { phase?: unknown; percent?: unknown; detail?: unknown }

function badgeColor(state: ServiceJobDetail['state']) {
  switch (state) {
    case 'Queued':
      return 'gray'
    case 'Starting':
      return 'cyan'
    case 'Running':
      return 'blue'
    case 'Finalizing':
      return 'grape'
    case 'Succeeded':
      return 'green'
    case 'Failed':
      return 'red'
    case 'Canceled':
      return 'yellow'
    default:
      return 'gray'
  }
}

function clamp01(v: number) {
  return Math.max(0, Math.min(1, v))
}

export function JobDetail(props: {
  jobId: string | null
  wsConnected: boolean
  lastEvent: ServiceEvent | null
  jobSnapshot: ServiceJobDetail | null
  sendRpc: (command: Record<string, unknown>) => Promise<unknown>
  onSelectJob: (jobId: string) => void
  onUnauthorized?: () => void
}) {
  const { jobId, wsConnected, lastEvent, jobSnapshot, sendRpc, onSelectJob, onUnauthorized } = props
  const [job, setJob] = useState<ServiceJobDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [live, setLive] = useState(true)
  const [canceling, setCanceling] = useState(false)
  const [retrying, setRetrying] = useState(false)
  const [progressDisplay, setProgressDisplay] = useState<number | null>(null)
  const logsEndRef = useRef<HTMLDivElement | null>(null)
  const logsViewportRef = useRef<HTMLDivElement | null>(null)
  const shouldAutoScrollRef = useRef(true)
  const scrollCleanupRef = useRef<(() => void) | null>(null)
  const progressTargetRef = useRef<number | null>(null)
  const progressTickRef = useRef<number | null>(null)
  const progressLastTickRef = useRef<number>(0)
  const lastGetJobRef = useRef<{ id: string; at: number } | null>(null)

  useEffect(() => {
    if (!jobId) return
    if (!wsConnected) return
    if (jobSnapshot && jobSnapshot.id === jobId) {
      setJob(jobSnapshot)
      setError(null)
      return
    }

    const now = performance.now()
    const last = lastGetJobRef.current
    if (last && last.id === jobId && now - last.at < 400) {
      return
    }
    lastGetJobRef.current = { id: jobId, at: now }

    setJob(null)
    setError(null)

    let canceled = false
    void sendRpc({ type: 'getJob', jobId }).catch((e) => {
      if (canceled) return
      onUnauthorized?.()
      setError(e instanceof Error ? e.message : String(e))
    })
    return () => {
      canceled = true
    }
  }, [jobId, jobSnapshot, onUnauthorized, sendRpc, wsConnected])

  useEffect(() => {
    if (!jobSnapshot) return
    if (jobSnapshot.id !== jobId) return
    setJob(jobSnapshot)
    setError(null)
  }, [jobId, jobSnapshot])

  useEffect(() => {
    setProgressDisplay(null)
    progressTargetRef.current = null

    if (progressTickRef.current != null) {
      window.clearInterval(progressTickRef.current)
      progressTickRef.current = null
    }

    if (!jobId) return

    progressLastTickRef.current = performance.now()
    progressTickRef.current = window.setInterval(() => {
      const now = performance.now()
      const dt = Math.max(0.016, (now - progressLastTickRef.current) / 1000)
      progressLastTickRef.current = now

      const target = progressTargetRef.current
      if (typeof target !== 'number') return

      setProgressDisplay((prev) => {
        const speed = 28
        const snap = 0.2

        const current = typeof prev === 'number' ? prev : target
        const diff = target - current
        if (Math.abs(diff) <= snap) return target

        const step = speed * dt
        return diff > 0 ? Math.min(target, current + step) : Math.max(target, current - step)
      })
    }, 120)

    return () => {
      if (progressTickRef.current != null) {
        window.clearInterval(progressTickRef.current)
        progressTickRef.current = null
      }
    }
  }, [jobId])

  useEffect(() => {
    const raw = typeof job?.progress?.percent === 'number' ? clamp01(job.progress.percent) * 100 : null
    const target = job?.state === 'Succeeded' ? 100 : raw
    progressTargetRef.current = typeof target === 'number' ? Math.max(0, Math.min(100, target)) : null

    if (typeof target !== 'number') {
      setProgressDisplay(null)
      return
    }

    setProgressDisplay((prev) => (typeof prev === 'number' ? prev : target))
  }, [job?.progress?.percent, job?.state])

  useEffect(() => {
    if (!jobId) return
    if (!lastEvent) return
    if (lastEvent.type === 'rpc') return
    if (lastEvent.jobId !== jobId) return

    if (lastEvent.type === 'job') {
      try {
        const data = JSON.parse(lastEvent.message) as ServiceJobDetail
        setJob(data)
        setError(null)
      } catch {
        void 0
      }
      return
    }

    setJob((prev) => {
      if (!prev) return prev
      if (prev.id !== jobId) return prev

      if (lastEvent.type === 'log') {
        if (!live) return prev
        const logs = [...prev.logs, lastEvent.message]
        const tail = logs.length > 600 ? logs.slice(logs.length - 600) : logs
        return { ...prev, logs: tail }
      }

      if (lastEvent.type === 'state') {
        const nextState = lastEvent.message as ServiceJobDetail['state']
        return { ...prev, state: nextState }
      }

      if (lastEvent.type === 'error') {
        return { ...prev, error: lastEvent.message }
      }

      if (lastEvent.type === 'progress') {
        let parsed: ProgressPayload = {}
        try {
          parsed = JSON.parse(lastEvent.message) as ProgressPayload
        } catch {
          parsed = {}
        }
        const phase = typeof parsed.phase === 'string' ? parsed.phase : null
        const percent = typeof parsed.percent === 'number' ? parsed.percent : null
        const detail = typeof parsed.detail === 'string' ? parsed.detail : null
        const progress = { ...(prev.progress ?? { phase: null, percent: null, detail: null, updatedAt: null }), phase, percent, detail, updatedAt: lastEvent.timestamp }
        return { ...prev, progress }
      }

      return prev
    })
  }, [jobId, lastEvent, live])

  useEffect(() => {
    shouldAutoScrollRef.current = true
    scrollCleanupRef.current?.()
    scrollCleanupRef.current = null
    return () => {
      scrollCleanupRef.current?.()
      scrollCleanupRef.current = null
    }
  }, [jobId])

  useEffect(() => {
    const viewport = logsViewportRef.current
    if (!job || !viewport) return
    if (scrollCleanupRef.current) return

    const update = () => {
      const threshold = 24
      const nearBottom = viewport.scrollTop + viewport.clientHeight >= viewport.scrollHeight - threshold
      shouldAutoScrollRef.current = nearBottom
    }

    update()
    viewport.addEventListener('scroll', update, { passive: true })
    scrollCleanupRef.current = () => {
      viewport.removeEventListener('scroll', update)
    }
  }, [jobId, job])

  useEffect(() => {
    if (!job) return
    const viewport = logsViewportRef.current
    if (!viewport) return
    if (!shouldAutoScrollRef.current) return
    viewport.scrollTo({ top: viewport.scrollHeight, behavior: 'auto' })
  }, [job])

  const handleCancel = useCallback(async () => {
    if (!jobId) return
    try {
      setCanceling(true)
      await sendRpc({ type: 'cancelJob', jobId })
      await sendRpc({ type: 'getJob', jobId })
    } catch (e) {
      onUnauthorized?.()
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setCanceling(false)
    }
  }, [jobId, onUnauthorized, sendRpc])

  const handleRetry = useCallback(async () => {
    if (!jobId) return
    try {
      setRetrying(true)
      const payload = await sendRpc({ type: 'retryJob', jobId })
      const parsed = payload as { data?: unknown }
      const data = (parsed?.data ?? null) as { jobId?: unknown } | null
      const nextJobId = typeof data?.jobId === 'string' ? data.jobId : null
      if (nextJobId && nextJobId !== jobId) {
        onSelectJob(nextJobId)
        await sendRpc({ type: 'getJob', jobId: nextJobId })
      } else {
        await sendRpc({ type: 'getJob', jobId })
      }
    } catch (e) {
      onUnauthorized?.()
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setRetrying(false)
    }
  }, [jobId, onSelectJob, onUnauthorized, sendRpc])

  const handleExportLogs = useCallback(() => {
    if (!job) return
    const blob = new Blob([job.logs.join('\n') + '\n'], { type: 'text/plain;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `steamdds-job-${job.id}.log`
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(url)
  }, [job])

  const body = useMemo(() => {
    if (!jobId) {
      return <Text c="dimmed">Select a job to see details</Text>
    }

    if (error) {
      return <Text c="red">{error}</Text>
    }

    if (!job) {
      return (
        <Group justify="center" py="xl">
          <Loader size="sm" />
        </Group>
      )
    }

    return (
      <Stack gap="md">
        <Group justify="space-between">
          <Group gap="sm">
            <Text fw={700}>Job</Text>
            <Code>{job.id}</Code>
          </Group>
          <Group gap="sm">
            <Button variant="default" size="xs" onClick={handleExportLogs}>
              Export logs
            </Button>
            {job.state === 'Failed' || job.state === 'Canceled' ? (
              <Button variant="light" size="xs" onClick={handleRetry} loading={retrying}>
                Retry
              </Button>
            ) : null}
            {job.state === 'Queued' || job.state === 'Starting' || job.state === 'Running' || job.state === 'Finalizing' ? (
              <Button color="red" variant="light" size="xs" onClick={handleCancel} loading={canceling}>
                Cancel
              </Button>
            ) : null}
            <Badge color={badgeColor(job.state)} variant="light">
              {job.state}
            </Badge>
          </Group>
        </Group>

        {job.progress?.phase || typeof job.progress?.percent === 'number' ? (
          <Card withBorder radius="md" padding="sm">
            <Stack gap={6}>
              <Group justify="space-between">
                <Text size="sm" fw={600}>
                  {job.progress?.phase ?? job.state}
                </Text>
                {typeof progressDisplay === 'number' ? (
                  <Text size="sm" c="dimmed">
                    {Math.round(progressDisplay)}%
                  </Text>
                ) : null}
              </Group>
              {job.progress?.detail ? (
                <Text size="xs" c="dimmed" lineClamp={2}>
                  {job.progress.detail}
                </Text>
              ) : null}
              {typeof progressDisplay === 'number' ? (
                <Progress value={Math.max(0, Math.min(100, progressDisplay))} />
              ) : null}
            </Stack>
          </Card>
        ) : null}

        <Group grow>
          <TextInput label="AppID" value={String(job.request.appId)} readOnly />
          <TextInput label="DepotID" value={job.request.depotId ? String(job.request.depotId) : ''} readOnly />
          <TextInput label="Branch" value={job.request.branch ?? ''} readOnly />
        </Group>

        {job.error ? <Text c="red">{job.error}</Text> : null}

        <Group justify="space-between" align="center">
          <Switch
            label="Live logs (WebSocket)"
            checked={live}
            onChange={(e) => setLive(e.currentTarget.checked)}
          />
          <Text size="sm" c={wsConnected ? 'green' : 'dimmed'}>
            {wsConnected ? 'connected' : 'disconnected'}
          </Text>
        </Group>

        <Card withBorder radius="md" padding="sm">
          <ScrollArea h={420} viewportRef={logsViewportRef}>
            <Stack gap={4}>
              {job.logs.map((line, idx) => (
                <Text key={`${idx}-${line}`} size="xs" ff="monospace">
                  {line}
                </Text>
              ))}
              <div ref={logsEndRef} />
            </Stack>
          </ScrollArea>
        </Card>
      </Stack>
    )
  }, [canceling, error, handleCancel, handleExportLogs, handleRetry, job, jobId, live, progressDisplay, retrying, wsConnected])

  return (
    <Card withBorder radius="md" padding="md" h="100%">
      {body}
    </Card>
  )
}
