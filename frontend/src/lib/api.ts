import type { ServiceInstallRequest, ServiceJobDetail, ServiceJobSummary } from './types'

export type ApiConfig = {
  baseUrl: string
  apiKey: string
}

export class ApiError extends Error {
  status: number
  body: string

  constructor(status: number, body: string, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.body = body
  }
}

export function isUnauthorized(err: unknown) {
  return err instanceof ApiError && err.status === 401
}

function sleep(ms: number) {
  return new Promise<void>((resolve) => {
    globalThis.setTimeout(resolve, ms)
  })
}

function isRetryableStatus(status: number) {
  return status === 408 || status === 429 || status === 500 || status === 502 || status === 503 || status === 504
}

function isRetryableNetworkError(err: unknown) {
  if (err instanceof TypeError) return true
  if (err instanceof Error && err.name === 'TypeError') return true
  if (err instanceof Error && err.name === 'AbortError') return true
  return false
}

function backoffDelayMs(retryIndex: number) {
  const base = 300
  const max = 5000
  const exp = Math.min(max, base * 2 ** retryIndex)
  const jitter = Math.floor(Math.random() * 120)
  return exp + jitter
}

function buildUrl(baseUrl: string, path: string) {
  const normalizedBase = normalizeBaseUrl(baseUrl).replace(/\/+$/, '')
  const normalizedPath = path.startsWith('/') ? path : `/${path}`
  return `${normalizedBase}${normalizedPath}`
}

function normalizeBaseUrl(baseUrl: string) {
  const trimmed = baseUrl.trim()
  if (!trimmed) return ''
  if (trimmed.startsWith('http://') || trimmed.startsWith('https://')) return trimmed
  return `http://${trimmed}`
}

function buildHeaders(apiKey: string, method: string) {
  const headers: Record<string, string> = {}

  if (method !== 'GET' && method !== 'DELETE') {
    headers['Content-Type'] = 'application/json'
  }

  if (apiKey.trim()) {
    headers['X-Api-Key'] = apiKey.trim()
  }

  return headers
}

function timeoutMsFor(method: string) {
  if (method === 'GET') return 12_000
  if (method === 'DELETE') return 20_000
  return 60_000
}

async function fetchWithTimeout(url: string, init: RequestInit, timeoutMs: number) {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) {
    return fetch(url, init)
  }

  const controller = new AbortController()
  const onAbort = () => controller.abort()
  init.signal?.addEventListener('abort', onAbort, { once: true })

  const timer = globalThis.setTimeout(() => controller.abort(), timeoutMs)
  try {
    return await fetch(url, { ...init, signal: controller.signal })
  } finally {
    globalThis.clearTimeout(timer)
    init.signal?.removeEventListener('abort', onAbort)
  }
}

async function requestJson<T>(config: ApiConfig, path: string, init?: RequestInit): Promise<T> {
  const method = String(init?.method ?? 'GET').toUpperCase()
  const maxAttempts = method === 'GET' ? 3 : 1
  let attempt = 0

  while (true) {
    try {
      const url = buildUrl(config.baseUrl, path)
      const timeoutMs = timeoutMsFor(method)
      const res = await fetchWithTimeout(
        url,
        {
          ...init,
          headers: {
            ...buildHeaders(config.apiKey, method),
            ...(init?.headers ?? {}),
          },
        },
        timeoutMs
      )

      if (!res.ok) {
        const text = await res.text().catch(() => '')
        const message = text || `${res.status} ${res.statusText}`
        const err = new ApiError(res.status, text, message)

        if (attempt < maxAttempts - 1 && isRetryableStatus(res.status)) {
          attempt += 1
          await sleep(backoffDelayMs(attempt - 1))
          continue
        }

        throw err
      }

      return (await res.json()) as T
    } catch (err) {
      if (attempt < maxAttempts - 1 && isRetryableNetworkError(err)) {
        attempt += 1
        await sleep(backoffDelayMs(attempt - 1))
        continue
      }
      throw err
    }
  }
}

export async function health(config: ApiConfig) {
  return requestJson<{ name: string; status: string }>(config, '/health', { method: 'GET', headers: {} })
}

export async function listJobs(config: ApiConfig) {
  return requestJson<ServiceJobSummary[]>(config, '/api/jobs', { method: 'GET', headers: {} })
}

export async function getJob(config: ApiConfig, id: string) {
  return requestJson<ServiceJobDetail>(config, `/api/jobs/${encodeURIComponent(id)}`, { method: 'GET', headers: {} })
}

export async function createInstall(config: ApiConfig, req: ServiceInstallRequest) {
  return requestJson<{ jobId: string }>(config, '/api/install', { method: 'POST', body: JSON.stringify(req) })
}

export async function cancelJob(config: ApiConfig, id: string) {
  return requestJson<{ ok: boolean }>(config, `/api/jobs/${encodeURIComponent(id)}`, { method: 'DELETE', headers: {} })
}

export async function retryJob(config: ApiConfig, id: string) {
  return requestJson<{ jobId: string }>(config, `/api/jobs/${encodeURIComponent(id)}/retry`, { method: 'POST', body: '{}' })
}

export function buildWsUrl(config: ApiConfig, jobId?: string) {
  const base = normalizeBaseUrl(config.baseUrl).replace(/\/+$/, '')
  const wsBase = base.startsWith('https://') ? `wss://${base.slice('https://'.length)}` : `ws://${base.slice('http://'.length)}`
  const url = new URL(`${wsBase}/ws`)

  if (jobId) {
    url.searchParams.set('jobId', jobId)
  }

  if (config.apiKey.trim()) {
    url.searchParams.set('apiKey', config.apiKey.trim())
  }

  return url.toString()
}
