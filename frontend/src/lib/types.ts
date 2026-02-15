export type ServiceJobState = 'Queued' | 'Starting' | 'Running' | 'Finalizing' | 'Succeeded' | 'Failed' | 'Canceled'

export type ServiceInstallRequest = {
  appId: number
  depotId?: number | null
  manifestId?: number | null
  branch?: string | null
  branchPassword?: string | null
  os?: string | null
  arch?: string | null
  language?: string | null
  lowViolence?: boolean | null
  dir?: string | null
  validate?: boolean | null
  maxDownloads?: number | null
  username?: string | null
  password?: string | null
  rememberPassword?: boolean | null
  skipAppConfirmation?: boolean | null
  dnsServer?: string | null
  httpProxy?: string | null
}

export type ServiceJobSummary = {
  id: string
  state: ServiceJobState
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  error: string | null
  progress?: {
    phase: string | null
    percent: number | null
    detail: string | null
    updatedAt: string | null
  }
  request: {
    appId: number
    depotId?: number | null
    manifestId?: number | null
    branch?: string | null
    dir?: string | null
  }
}

export type ServiceJobDetail = {
  id: string
  state: ServiceJobState
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  error: string | null
  progress?: {
    phase: string | null
    percent: number | null
    detail: string | null
    updatedAt: string | null
  }
  logs: string[]
  request: ServiceInstallRequest
}

export type ServiceEvent = {
  jobId: string
  timestamp: string
  type: 'log' | 'state' | 'error' | 'progress' | 'jobs' | 'job' | 'rpc'
  message: string
}

export type ServiceAccountState = {
  status: 'none' | 'starting' | 'pending' | 'ready' | 'error'
  username?: string | null
  message?: string | null
  qrUrl?: string | null
  qrAscii?: string | null
  updatedAt?: string | null
}
