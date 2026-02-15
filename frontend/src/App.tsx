import { ActionIcon, AppShell, Badge, Container, Group, Select, Stack, Text, Title } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconSettings } from '@tabler/icons-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

import { InstallForm } from './components/InstallForm'
import { JobDetail } from './components/JobDetail'
import { JobsList } from './components/JobsList'
import { SettingsModal } from './components/SettingsModal'
import { buildWsUrl } from './lib/api'
import { getActiveService, loadSettings, saveSettings, type AppSettings } from './lib/settings'
import type { ServiceEvent, ServiceJobDetail, ServiceJobSummary } from './lib/types'

export default function App() {
  const [settings, setSettings] = useState<AppSettings>(() => loadSettings())
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [focusApiKey, setFocusApiKey] = useState(false)
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null)
  const [serviceStatus, setServiceStatus] = useState<'unknown' | 'ok' | 'error'>('unknown')
  const [wsConnected, setWsConnected] = useState(false)
  const [jobs, setJobs] = useState<ServiceJobSummary[] | null>(null)
  const [jobsError, setJobsError] = useState<string | null>(null)
  const [lastEvent, setLastEvent] = useState<ServiceEvent | null>(null)
  const [jobSnapshots, setJobSnapshots] = useState<Record<string, ServiceJobDetail>>({})
  const [jobsReady, setJobsReady] = useState(false)
  const [pendingAutoSelectId, setPendingAutoSelectId] = useState<string | null>(null)
  const unauthorizedLock = useRef(false)
  const wsRef = useRef<WebSocket | null>(null)
  const retryTimerRef = useRef<number | null>(null)
  const wsUrlRef = useRef<string | null>(null)
  const wsConnectIdRef = useRef(0)
  const pendingRef = useRef(
    new Map<
      string,
      {
        resolve: (value: unknown) => void
        reject: (reason?: unknown) => void
        timer: number
      }
    >(),
  )

  const activeService = useMemo(() => getActiveService(settings), [settings])
  const api = useMemo(
    () => ({ baseUrl: activeService.apiBaseUrl, apiKey: activeService.apiKey }),
    [activeService.apiBaseUrl, activeService.apiKey],
  )

  const serviceOptions = useMemo(
    () => settings.services.map((s) => ({ value: s.id, label: s.name })),
    [settings.services],
  )

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setJobs(null)
      setJobsReady(false)
      setJobSnapshots({})
      setPendingAutoSelectId(null)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [api])

  const requestApiKey = useCallback(() => {
    if (unauthorizedLock.current) return
    unauthorizedLock.current = true
    setFocusApiKey(true)
    setSettingsOpen(true)
    notifications.show({ color: 'yellow', title: '未授权', message: '请填写 API Key 后重试' })
  }, [])

  const waitForWsOpen = useCallback(() => {
    return new Promise<WebSocket>((resolve, reject) => {
      const start = performance.now()
      const tick = () => {
        const ws = wsRef.current
        if (ws && ws.readyState === WebSocket.OPEN) {
          resolve(ws)
          return
        }
        if (performance.now() - start > 15_000) {
          reject(new Error('websocket not connected'))
          return
        }
        window.setTimeout(tick, 150)
      }
      tick()
    })
  }, [])

  const sendRpc = useCallback(async (command: Record<string, unknown>) => {
    const ws = await waitForWsOpen()

    const requestId = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : `${Date.now()}-${Math.random().toString(16).slice(2)}`

    const payload = JSON.stringify({ ...command, requestId })
    const res = await new Promise<unknown>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        pendingRef.current.delete(requestId)
        reject(new Error('request timeout'))
      }, 12_000)
      pendingRef.current.set(requestId, { resolve, reject, timer })
      try {
        ws.send(payload)
      } catch (e) {
        window.clearTimeout(timer)
        pendingRef.current.delete(requestId)
        reject(e)
      }
    })
    return res
  }, [waitForWsOpen])

  useEffect(() => {
    if (!wsConnected || !jobsReady) return
    const list = jobs ?? []
    let nextId: string | null = selectedJobId
    if (list.length === 0) {
      nextId = null
    } else if (selectedJobId && list.some((j) => j.id === selectedJobId)) {
      nextId = selectedJobId
    } else {
      const running =
        list.find((j) => j.state === 'Running' || j.state === 'Starting' || j.state === 'Finalizing') ??
        list.find((j) => j.state === 'Queued') ??
        list[0]
      nextId = running?.id ?? null
    }

    const timers: number[] = []
    const defer = (fn: () => void) => {
      const t = window.setTimeout(fn, 0)
      timers.push(t)
    }

    if (!nextId) {
      defer(() => {
        setSelectedJobId(null)
        setPendingAutoSelectId(null)
      })
      return () => timers.forEach((t) => window.clearTimeout(t))
    }

    if (nextId === selectedJobId && list.some((j) => j.id === selectedJobId)) {
      defer(() => setPendingAutoSelectId(null))
      return () => timers.forEach((t) => window.clearTimeout(t))
    }

    if (jobSnapshots[nextId]) {
      defer(() => {
        setSelectedJobId(nextId)
        setPendingAutoSelectId(null)
      })
      return () => timers.forEach((t) => window.clearTimeout(t))
    }

    defer(() => setPendingAutoSelectId(nextId))
    void sendRpc({ type: 'getJob', jobId: nextId }).catch(() => {
      void 0
    })

    return () => timers.forEach((t) => window.clearTimeout(t))
  }, [jobs, jobsReady, jobSnapshots, selectedJobId, sendRpc, wsConnected])

  useEffect(() => {
    if (!pendingAutoSelectId) return
    const snapshot = jobSnapshots[pendingAutoSelectId]
    if (!snapshot) return
    const timer = window.setTimeout(() => {
      setSelectedJobId(pendingAutoSelectId)
      setPendingAutoSelectId(null)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [jobSnapshots, pendingAutoSelectId])

  const requestJobsSnapshot = useCallback(async () => {
    const payload = await sendRpc({ type: 'listJobs' })
    return payload
  }, [sendRpc])

  useEffect(() => {
    let closed = false
    let retryCount = 0

    const rejectAllPending = (reason: unknown) => {
      for (const [, p] of pendingRef.current.entries()) {
        window.clearTimeout(p.timer)
        p.reject(reason)
      }
      pendingRef.current.clear()
    }

    const connect = () => {
      if (closed) return

      const url = buildWsUrl(api)
      const existing = wsRef.current
      if (existing && (existing.readyState === WebSocket.OPEN || existing.readyState === WebSocket.CONNECTING) && wsUrlRef.current === url) {
        return
      }

      wsConnectIdRef.current += 1
      const connectId = wsConnectIdRef.current

      try {
        existing?.close()
      } catch {
        void 0
      }
      wsRef.current = null
      wsUrlRef.current = url

      const ws = new WebSocket(url)
      wsRef.current = ws

      ws.onopen = () => {
        if (wsRef.current !== ws || wsConnectIdRef.current !== connectId) return
        retryCount = 0
        setWsConnected(true)
        setServiceStatus('ok')
        setJobsError(null)
        setJobs(null)
        setJobsReady(false)
        setJobSnapshots({})
        void requestJobsSnapshot().catch(() => {
          void 0
        })
      }

      ws.onclose = () => {
        if (wsRef.current !== ws || wsConnectIdRef.current !== connectId) return
        wsRef.current = null
        wsUrlRef.current = null
        setWsConnected(false)
        setServiceStatus('error')
        setJobsError('WebSocket disconnected')
        setJobsReady(false)
        rejectAllPending(new Error('websocket disconnected'))
        if (closed) return
        retryCount += 1
        const delay = Math.min(5_000, 800 * 2 ** Math.min(10, retryCount))
        retryTimerRef.current = window.setTimeout(connect, delay)
      }

      ws.onerror = () => {
        if (wsRef.current !== ws || wsConnectIdRef.current !== connectId) return
        setWsConnected(false)
        setServiceStatus('error')
        setJobsError('WebSocket error')
        setJobsReady(false)
        try {
          ws.close()
        } catch {
          void 0
        }
      }

      ws.onmessage = (msg) => {
        if (wsRef.current !== ws || wsConnectIdRef.current !== connectId) return
        try {
          const ev = JSON.parse(String(msg.data)) as ServiceEvent

          if (ev.type === 'rpc') {
            const payload = JSON.parse(ev.message) as { requestId?: unknown; ok?: unknown; data?: unknown; error?: unknown }
            const requestId = typeof payload.requestId === 'string' ? payload.requestId : null
            if (!requestId) return
            const pending = pendingRef.current.get(requestId)
            if (!pending) return
            pendingRef.current.delete(requestId)
            window.clearTimeout(pending.timer)
            if (payload.ok === true) {
              pending.resolve(payload)
            } else {
              pending.reject(new Error(typeof payload.error === 'string' ? payload.error : 'request failed'))
            }
            return
          }

          if (ev.type !== 'jobs') {
            setLastEvent(ev)
          }

          if (ev.type === 'jobs') {
            const data = JSON.parse(ev.message) as ServiceJobSummary[]
            setJobs(data)
            setJobsError(null)
            setJobsReady(true)
          }

          if (ev.type === 'job') {
            try {
              const data = JSON.parse(ev.message) as ServiceJobDetail
              if (data?.id) {
                setJobSnapshots((prev) => (prev[data.id] === data ? prev : { ...prev, [data.id]: data }))
              }
            } catch {
              void 0
            }
          }
        } catch {
          void 0
        }
      }
    }

    connect()

    return () => {
      closed = true
      if (retryTimerRef.current != null) {
        window.clearTimeout(retryTimerRef.current)
        retryTimerRef.current = null
      }
      rejectAllPending(new Error('websocket closed'))
      try {
        wsRef.current?.close()
      } catch {
        void 0
      }
      wsRef.current = null
      wsUrlRef.current = null
    }
  }, [api, requestJobsSnapshot])

  return (
    <>
      <SettingsModal
        opened={settingsOpen}
        focusApiKey={focusApiKey}
        onClose={() => {
          unauthorizedLock.current = false
          setFocusApiKey(false)
          setSettingsOpen(false)
        }}
        initial={settings}
        onSave={(next) => {
          unauthorizedLock.current = false
          setFocusApiKey(false)
          setSettings(next)
          saveSettings(next)
          notifications.show({ title: 'Saved', message: 'Settings updated' })
        }}
      />

      <AppShell
        header={{ height: 56 }}
        padding="md"
      >
        <AppShell.Header>
          <Container size="xl" h="100%">
            <Group justify="space-between" h="100%">
              <Group gap="sm">
                <Title order={4}>SteamDepotDownloaderService</Title>
                <Badge color={serviceStatus === 'ok' ? 'green' : serviceStatus === 'error' ? 'red' : 'gray'} variant="light">
                  {serviceStatus}
                </Badge>
                <Select
                  value={settings.activeServiceId}
                  data={serviceOptions}
                  onChange={(v) => {
                    if (!v) return
                    const next = { ...settings, activeServiceId: v }
                    setSettings(next)
                    saveSettings(next)
                    setSelectedJobId(null)
                  }}
                  allowDeselect={false}
                  size="xs"
                  w={180}
                />
                <Text c="dimmed" size="sm">
                  {activeService.apiBaseUrl}
                </Text>
              </Group>
              <Group gap="sm">
                <ActionIcon
                  variant="default"
                  onClick={() => {
                    setFocusApiKey(false)
                    setSettingsOpen(true)
                  }}
                >
                  <IconSettings size={18} />
                </ActionIcon>
              </Group>
            </Group>
          </Container>
        </AppShell.Header>

        <AppShell.Main>
          <Container size="xl">
            <Group align="flex-start" grow>
              <Stack w={420} maw={480}>
                <InstallForm
                  sendRpc={sendRpc}
                  network={{ dnsServer: activeService.dnsServer, httpProxy: activeService.httpProxy }}
                  onUnauthorized={requestApiKey}
                  onCreated={(jobId) => {
                    setSelectedJobId(jobId)
                  }}
                />
                <JobsList
                  selectedJobId={selectedJobId}
                  onSelect={setSelectedJobId}
                  jobs={jobs}
                  lastEvent={lastEvent}
                  error={jobsError}
                />
              </Stack>

              <Stack style={{ flex: 1 }}>
                <JobDetail
                  key={selectedJobId ?? 'none'}
                  jobId={selectedJobId}
                  wsConnected={wsConnected}
                  lastEvent={lastEvent}
                  jobSnapshot={selectedJobId ? jobSnapshots[selectedJobId] ?? null : null}
                  sendRpc={sendRpc}
                  onSelectJob={setSelectedJobId}
                  onUnauthorized={requestApiKey}
                />
              </Stack>
            </Group>
          </Container>
        </AppShell.Main>
      </AppShell>
    </>
  )
}
