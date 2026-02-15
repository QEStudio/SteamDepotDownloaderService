import { Badge, Card, Group, Loader, Progress, ScrollArea, Stack, Text, UnstyledButton } from '@mantine/core'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

import type { ServiceEvent, ServiceJobSummary } from '../lib/types'

type ProgressPayload = { phase?: unknown; percent?: unknown; detail?: unknown }

function badgeColor(state: ServiceJobSummary['state']) {
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

export function JobsList(props: {
  selectedJobId: string | null
  onSelect: (jobId: string) => void
  jobs: ServiceJobSummary[] | null
  lastEvent: ServiceEvent | null
  error?: string | null
}) {
  const { selectedJobId, onSelect, jobs: jobsProp, lastEvent, error: errorProp } = props
  const jobs = jobsProp
  const error: string | null = errorProp ?? null
  const [progressDisplay, setProgressDisplay] = useState<Record<string, number>>({})
  const [progressMeta, setProgressMeta] = useState<Record<string, { phase: string | null; detail: string | null; updatedAt: number }>>({})
  const [liveState, setLiveState] = useState<Record<string, ServiceJobSummary['state']>>({})
  const progressTickRef = useRef<number | null>(null)
  const progressLastTickRef = useRef<number>(0)
  const jobIdsRef = useRef<string[]>([])
  const progressTargetRef = useRef<Record<string, number>>({})
  const currentJob = useMemo(() => {
    if (!jobs) return null
    return (
      jobs.find((j) => {
        const state = liveState[j.id] ?? j.state
        return state === 'Running' || state === 'Starting' || state === 'Finalizing'
      }) ?? null
    )
  }, [jobs, liveState])

  const applyJobsData = useCallback((data: ServiceJobSummary[]) => {
    const ids = data.map((j) => j.id)
    jobIdsRef.current = ids
    const idSet = new Set(ids)

    for (const k of Object.keys(progressTargetRef.current)) {
      if (!idSet.has(k)) {
        delete progressTargetRef.current[k]
      }
    }

    setLiveState((prev) => {
      let changed = false
      const next: Record<string, ServiceJobSummary['state']> = {}
      for (const id of ids) {
        const v = prev[id]
        if (v) next[id] = v
      }
      if (Object.keys(next).length !== Object.keys(prev).length) changed = true
      return changed ? next : prev
    })

    setProgressMeta((prev) => {
      let changed = false
      const next: Record<string, { phase: string | null; detail: string | null; updatedAt: number }> = { ...prev }

      for (const k of Object.keys(next)) {
        if (!idSet.has(k)) {
          delete next[k]
          changed = true
        }
      }

      for (const j of data) {
        const updatedAt = j.progress?.updatedAt ? Date.parse(j.progress.updatedAt) : 0
        const existing = prev[j.id]
        if (existing && updatedAt <= existing.updatedAt) {
          continue
        }

        const phase = j.progress?.phase ?? null
        const detail = j.progress?.detail ?? null
        next[j.id] = { phase, detail, updatedAt }

        if (typeof j.progress?.percent === 'number') {
          let t = clamp01(j.progress.percent) * 100
          if (j.state === 'Succeeded') t = 100
          progressTargetRef.current[j.id] = t
        }

        changed = true
      }

      return changed ? next : prev
    })
  }, [])

  useEffect(() => {
    if (!jobsProp) return
    const t = window.setTimeout(() => applyJobsData(jobsProp), 0)
    return () => window.clearTimeout(t)
  }, [applyJobsData, jobsProp])

  useEffect(() => {
    if (!lastEvent) return
    if (lastEvent.type === 'rpc') return
    const t = window.setTimeout(() => {
      if (lastEvent.type === 'state') {
        const nextState = lastEvent.message as ServiceJobSummary['state']
        setLiveState((prev) => (prev[lastEvent.jobId] === nextState ? prev : { ...prev, [lastEvent.jobId]: nextState }))
        return
      }

      if (lastEvent.type === 'progress') {
        let parsed: ProgressPayload = {}
        try {
          parsed = JSON.parse(lastEvent.message) as ProgressPayload
        } catch {
          parsed = {}
        }

        const phase = typeof parsed.phase === 'string' ? parsed.phase : null
        const detail = typeof parsed.detail === 'string' ? parsed.detail : null
        const percent = typeof parsed.percent === 'number' ? parsed.percent : null
        const updatedAt = Date.parse(lastEvent.timestamp)

        if (typeof percent === 'number') {
          const v = clamp01(percent) * 100
          progressTargetRef.current[lastEvent.jobId] = v >= 100 ? 100 : v
        }

        setProgressMeta((prev) => {
          const existing = prev[lastEvent.jobId]
          if (existing && updatedAt <= existing.updatedAt) return prev
          return { ...prev, [lastEvent.jobId]: { phase, detail, updatedAt } }
        })
      }
    }, 0)
    return () => window.clearTimeout(t)
  }, [lastEvent])

  useEffect(() => {
    progressLastTickRef.current = performance.now()
    progressTickRef.current = window.setInterval(() => {
      const now = performance.now()
      const dt = Math.max(0.016, (now - progressLastTickRef.current) / 1000)
      progressLastTickRef.current = now

      setProgressDisplay((prev) => {
        const ids = jobIdsRef.current
        if (ids.length === 0) {
          return Object.keys(prev).length === 0 ? prev : {}
        }

        let changed = false
        const next: Record<string, number> = { ...prev }

        const idSet = new Set(ids)
        for (const k of Object.keys(next)) {
          if (!idSet.has(k)) {
            delete next[k]
            changed = true
          }
        }

        const speed = 28
        const snap = 0.2

        for (const id of ids) {
          const target = progressTargetRef.current[id]
          if (typeof target !== 'number') continue

          const current = typeof next[id] === 'number' ? next[id] : target
          const diff = target - current
          if (Math.abs(diff) <= snap) {
            if (next[id] !== target) {
              next[id] = target
              changed = true
            }
            continue
          }

          const step = speed * dt
          const v = diff > 0 ? Math.min(target, current + step) : Math.max(target, current - step)
          if (next[id] !== v) {
            next[id] = v
            changed = true
          }
        }

        return changed ? next : prev
      })
    }, 120)

    return () => {
      if (progressTickRef.current != null) {
        window.clearInterval(progressTickRef.current)
        progressTickRef.current = null
      }
    }
  }, [])

  const content = useMemo(() => {
    if (error) {
      return <Text c="red">{error}</Text>
    }

    if (!jobs) {
      return (
        <Group justify="center" py="md">
          <Loader size="sm" />
        </Group>
      )
    }

    if (jobs.length === 0) {
      return <Text c="dimmed">No jobs yet</Text>
    }

    return (
      <Stack gap={8}>
        <Card withBorder padding="sm" radius="md">
          <Group justify="space-between" align="flex-start">
            <Stack gap={2}>
              <Text size="sm" fw={600}>
                当前执行任务
              </Text>
              {currentJob ? (
                <>
                  <Text size="xs" c="dimmed">
                    Job {currentJob.id.slice(0, 8)} · App {currentJob.request.appId}
                    {currentJob.request.depotId ? ` / Depot ${currentJob.request.depotId}` : ''}
                  </Text>
                  {(progressMeta[currentJob.id]?.phase ?? currentJob.progress?.phase) ? (
                    <Text size="xs" c="dimmed">
                      {progressMeta[currentJob.id]?.phase ?? currentJob.progress?.phase}
                      {(progressMeta[currentJob.id]?.detail ?? currentJob.progress?.detail)
                        ? ` · ${progressMeta[currentJob.id]?.detail ?? currentJob.progress?.detail}`
                        : ''}
                    </Text>
                  ) : null}
                </>
              ) : (
                <Text size="xs" c="dimmed">暂无运行任务</Text>
              )}
            </Stack>
            <Badge color={currentJob ? 'blue' : 'gray'} variant="light">
              {currentJob ? 'Running' : 'Idle'}
            </Badge>
          </Group>
          {currentJob && typeof progressDisplay[currentJob.id] === 'number' ? (
            <Progress value={Math.max(0, Math.min(100, progressDisplay[currentJob.id]))} size="xs" mt={8} />
          ) : null}
        </Card>
        {jobs.map((j) => {
          const state = liveState[j.id] ?? j.state
          const isRunning = state === 'Running' || state === 'Starting' || state === 'Finalizing'
          return (
            <UnstyledButton key={j.id} onClick={() => onSelect(j.id)}>
              <Card withBorder padding="sm" radius="md" bg={selectedJobId === j.id ? 'dark.6' : undefined}>
                <Group justify="space-between" align="flex-start">
                  <Stack gap={2}>
                    <Text size="sm" fw={600}>
                      Job {j.id.slice(0, 8)}
                    </Text>
                    <Text size="xs" c="dimmed">
                      App {j.request.appId}
                      {j.request.depotId ? ` / Depot ${j.request.depotId}` : ''}
                    </Text>
                    {(progressMeta[j.id]?.phase ?? j.progress?.phase) ? (
                      <Text size="xs" c="dimmed">
                        {progressMeta[j.id]?.phase ?? j.progress?.phase}
                        {(progressMeta[j.id]?.detail ?? j.progress?.detail) ? ` · ${progressMeta[j.id]?.detail ?? j.progress?.detail}` : ''}
                      </Text>
                    ) : null}
                  </Stack>
                  <Group gap="xs">
                    <Badge color={badgeColor(state)} variant="light">
                      {state}
                    </Badge>
                    {isRunning ? (
                      <Badge color="cyan" variant="light">
                        当前执行
                      </Badge>
                    ) : null}
                  </Group>
                </Group>
                {typeof progressDisplay[j.id] === 'number' ? (
                  <Progress value={Math.max(0, Math.min(100, progressDisplay[j.id]))} size="xs" mt={8} />
                ) : null}
                {j.error ? (
                  <Text size="xs" c="red" mt={6} lineClamp={2}>
                    {j.error}
                  </Text>
                ) : null}
              </Card>
            </UnstyledButton>
          )
        })}
      </Stack>
    )
  }, [currentJob, error, jobs, liveState, onSelect, progressDisplay, progressMeta, selectedJobId])

  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" mb="sm">
        <Text fw={600}>Jobs</Text>
      </Group>
      <ScrollArea h={380}>{content}</ScrollArea>
    </Card>
  )
}
