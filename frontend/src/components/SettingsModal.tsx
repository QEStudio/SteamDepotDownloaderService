import { Button, Group, Modal, Select, Stack, TextInput } from '@mantine/core'
import { useMemo, useState } from 'react'

import type { AppSettings } from '../lib/settings'

function newId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  return `${Date.now()}-${Math.random().toString(16).slice(2)}`
}

function SettingsForm(props: {
  initial: AppSettings
  onCancel: () => void
  onSave: (next: AppSettings) => void
  focusApiKey?: boolean
}) {
  const [activeServiceId, setActiveServiceId] = useState(props.initial.activeServiceId)
  const [services, setServices] = useState(props.initial.services)

  const serviceOptions = useMemo(() => services.map((s) => ({ value: s.id, label: s.name })), [services])
  const activeService = useMemo(() => services.find((s) => s.id === activeServiceId) ?? services[0], [activeServiceId, services])

  const updateActive = (patch: Partial<(typeof services)[number]>) => {
    setServices((prev) =>
      prev.map((s) => {
        if (s.id !== activeServiceId) return s
        return { ...s, ...patch }
      }),
    )
  }

  return (
    <Stack gap="md">
      <Select
        label="Service"
        value={activeServiceId}
        data={serviceOptions}
        allowDeselect={false}
        onChange={(v) => {
          if (!v) return
          setActiveServiceId(v)
        }}
      />
      <Group justify="space-between">
        <Button
          variant="default"
          onClick={() => {
            const id = newId()
            setServices((prev) => [...prev, { id, name: `service-${prev.length + 1}`, apiBaseUrl: '', apiKey: '', dnsServer: '', httpProxy: '' }])
            setActiveServiceId(id)
          }}
        >
          Add service
        </Button>
        <Button
          variant="default"
          disabled={services.length <= 1}
          onClick={() => {
            setServices((prev) => prev.filter((s) => s.id !== activeServiceId))
            const next = services.filter((s) => s.id !== activeServiceId)
            setActiveServiceId(next[0]?.id ?? '')
          }}
        >
          Remove
        </Button>
      </Group>
      <TextInput
        label="Name"
        value={activeService?.name ?? ''}
        onChange={(e) => updateActive({ name: e.currentTarget.value })}
      />
      <TextInput
        label="API Base URL"
        description="Example: http://127.0.0.1:18080"
        value={activeService?.apiBaseUrl ?? ''}
        onChange={(e) => updateActive({ apiBaseUrl: e.currentTarget.value })}
      />
      <TextInput
        label="API Key"
        description="Matches STEAMDDS_API_KEY on the service"
        value={activeService?.apiKey ?? ''}
        autoFocus={props.focusApiKey}
        onChange={(e) => updateActive({ apiKey: e.currentTarget.value })}
      />
      <TextInput
        label="DNS Server (optional)"
        description="Example: 8.8.8.8 or 114.114.114.114:53"
        value={activeService?.dnsServer ?? ''}
        onChange={(e) => updateActive({ dnsServer: e.currentTarget.value })}
      />
      <TextInput
        label="HTTP Proxy (optional)"
        description="Example: http://127.0.0.1:7890"
        value={activeService?.httpProxy ?? ''}
        onChange={(e) => updateActive({ httpProxy: e.currentTarget.value })}
      />
      <Group justify="flex-end">
        <Button variant="default" onClick={props.onCancel}>
          Cancel
        </Button>
        <Button
          onClick={() => {
            const normalizedServices = services
              .filter((s) => s && typeof s.id === 'string')
              .map((s) => ({
                ...s,
                name: s.name.trim() || s.id,
                apiBaseUrl: s.apiBaseUrl.trim(),
                dnsServer: typeof s.dnsServer === 'string' ? s.dnsServer.trim() : '',
                httpProxy: typeof s.httpProxy === 'string' ? s.httpProxy.trim() : '',
              }))
              .filter((s) => s.name && s.apiBaseUrl)

            const nextServices = normalizedServices.length > 0 ? normalizedServices : props.initial.services
            const nextActiveId = nextServices.some((s) => s.id === activeServiceId) ? activeServiceId : nextServices[0].id
            props.onSave({ services: nextServices, activeServiceId: nextActiveId })
            props.onCancel()
          }}
        >
          Save
        </Button>
      </Group>
    </Stack>
  )
}

export function SettingsModal(props: {
  opened: boolean
  focusApiKey?: boolean
  onClose: () => void
  initial: AppSettings
  onSave: (next: AppSettings) => void
}) {
  return (
    <Modal opened={props.opened} onClose={props.onClose} title="Settings" centered>
      {props.opened ? (
        <SettingsForm
          key={`${props.initial.activeServiceId}|${props.initial.services.length}`}
          initial={props.initial}
          onCancel={props.onClose}
          onSave={props.onSave}
          focusApiKey={props.focusApiKey}
        />
      ) : null}
    </Modal>
  )
}
