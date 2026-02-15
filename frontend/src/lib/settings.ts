export type ServiceProfile = {
  id: string
  name: string
  apiBaseUrl: string
  apiKey: string
  dnsServer: string
  httpProxy: string
}

export type AppSettings = {
  services: ServiceProfile[]
  activeServiceId: string
}

const SETTINGS_KEY_V2 = 'steamdds.settings.v2'
const SETTINGS_KEY_V1 = 'steamdds.settings.v1'

function newId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

export function getDefaultSettings(): AppSettings {
  const apiBaseUrlFromEnv = import.meta.env.VITE_API_BASE_URL
  const proxyTarget = import.meta.env.VITE_PROXY_TARGET
  const apiBaseUrl =
    apiBaseUrlFromEnv ??
    (import.meta.env.DEV && proxyTarget ? window.location.origin : 'http://127.0.0.1:18080')
  const apiKey = import.meta.env.VITE_API_KEY ?? ''
  const services: ServiceProfile[] = [{ id: 'default', name: 'default', apiBaseUrl, apiKey, dnsServer: '', httpProxy: '' }]
  return { services, activeServiceId: services[0].id }
}

export function loadSettings(): AppSettings {
  const defaults = getDefaultSettings()
  try {
    const rawV2 = localStorage.getItem(SETTINGS_KEY_V2)
    if (rawV2) {
      const parsed = JSON.parse(rawV2) as { services?: unknown; activeServiceId?: unknown }
      const rawServices = Array.isArray(parsed.services) ? parsed.services : []
      const services: ServiceProfile[] = rawServices
        .filter((s): s is Record<string, unknown> => typeof s === 'object' && s != null)
        .map((s) => ({
          id: typeof s.id === 'string' && s.id.trim() ? s.id.trim() : newId(),
          name: typeof s.name === 'string' && s.name.trim() ? s.name.trim() : 'service',
          apiBaseUrl:
            typeof s.apiBaseUrl === 'string' && s.apiBaseUrl.trim() ? s.apiBaseUrl.trim() : defaults.services[0].apiBaseUrl,
          apiKey: typeof s.apiKey === 'string' ? s.apiKey : defaults.services[0].apiKey,
          dnsServer: typeof s.dnsServer === 'string' ? s.dnsServer : defaults.services[0].dnsServer,
          httpProxy: typeof s.httpProxy === 'string' ? s.httpProxy : defaults.services[0].httpProxy,
        }))

      const normalizedServices = services.length > 0 ? services : defaults.services
      const activeServiceIdRaw = typeof parsed.activeServiceId === 'string' ? parsed.activeServiceId : defaults.activeServiceId
      const activeServiceId = normalizedServices.some((s) => s.id === activeServiceIdRaw) ? activeServiceIdRaw : normalizedServices[0].id

      return { services: normalizedServices, activeServiceId }
    }

    const rawV1 = localStorage.getItem(SETTINGS_KEY_V1)
    if (rawV1) {
      const parsed = JSON.parse(rawV1) as { apiBaseUrl?: unknown; apiKey?: unknown }
      const apiBaseUrl =
        typeof parsed.apiBaseUrl === 'string' && parsed.apiBaseUrl.trim() ? parsed.apiBaseUrl.trim() : defaults.services[0].apiBaseUrl
      const apiKey = typeof parsed.apiKey === 'string' ? parsed.apiKey : defaults.services[0].apiKey
      const services: ServiceProfile[] = [{ id: 'default', name: 'default', apiBaseUrl, apiKey, dnsServer: '', httpProxy: '' }]
      return { services, activeServiceId: services[0].id }
    }

    return defaults
  } catch {
    return defaults
  }
}

export function saveSettings(settings: AppSettings) {
  localStorage.setItem(SETTINGS_KEY_V2, JSON.stringify(settings))
}

export function getActiveService(settings: AppSettings): ServiceProfile {
  return settings.services.find((s) => s.id === settings.activeServiceId) ?? settings.services[0]
}
