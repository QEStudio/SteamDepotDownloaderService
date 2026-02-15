import { ActionIcon, Button, Card, Group, NumberInput, PasswordInput, Stack, Switch, Text, TextInput } from '@mantine/core'
import { useForm } from '@mantine/form'
import { notifications } from '@mantine/notifications'
import { IconPlayerPlay } from '@tabler/icons-react'

import type { ServiceInstallRequest } from '../lib/types'

type InstallFormValues = {
  appId: number | ''
  depotId: number | ''
  manifestId: string
  branch: string
  dir: string
  validate: boolean
  maxDownloads: number
  username: string
  password: string
  rememberPassword: boolean
}

function toRequest(values: InstallFormValues, network?: { dnsServer?: string; httpProxy?: string }): ServiceInstallRequest {
  const depotId = values.depotId === '' ? undefined : values.depotId
  const manifestId = values.manifestId.trim() ? Number(values.manifestId.trim()) : undefined

  return {
    appId: values.appId === '' ? 0 : values.appId,
    depotId,
    manifestId: depotId ? manifestId : undefined,
    branch: values.branch.trim() || undefined,
    dir: values.dir.trim() || undefined,
    validate: values.validate,
    maxDownloads: values.maxDownloads,
    username: values.username.trim() || undefined,
    password: values.password || undefined,
    rememberPassword: values.rememberPassword,
    dnsServer: network?.dnsServer?.trim() || undefined,
    httpProxy: network?.httpProxy?.trim() || undefined,
  }
}

export function InstallForm(props: {
  sendRpc: (command: Record<string, unknown>) => Promise<unknown>
  network?: { dnsServer?: string; httpProxy?: string }
  onCreated: (jobId: string) => void
  onUnauthorized?: () => void
}) {
  const form = useForm<InstallFormValues>({
    initialValues: {
      appId: '',
      depotId: '',
      manifestId: '',
      branch: 'public',
      dir: '',
      validate: false,
      maxDownloads: 8,
      username: '',
      password: '',
      rememberPassword: false,
    },
    validate: {
      appId: (v) => (v === '' || v <= 0 ? 'AppID is required' : null),
      maxDownloads: (v) => (v < 1 ? 'Must be >= 1' : null),
      manifestId: (v, values) => {
        if (values.depotId === '') return null
        if (!v.trim()) return null
        return Number.isFinite(Number(v.trim())) ? null : 'ManifestId must be a number'
      },
    },
  })

  const submit = form.onSubmit(async (values) => {
    const req = toRequest(values, props.network)
    try {
      const payload = await props.sendRpc({ type: 'install', request: req })
      const parsed = payload as { data?: unknown }
      const data = (parsed?.data ?? null) as { jobId?: unknown } | null
      const jobId = typeof data?.jobId === 'string' ? data.jobId : null
      if (!jobId) {
        throw new Error('invalid server response')
      }
      notifications.show({ title: 'Job created', message: jobId })
      props.onCreated(jobId)
    } catch (e) {
      props.onUnauthorized?.()
      notifications.show({ color: 'red', title: 'Create job failed', message: e instanceof Error ? e.message : String(e) })
    }
  })

  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" mb="sm">
        <Text fw={600}>Install / Download</Text>
        <ActionIcon variant="subtle" onClick={() => submit()}>
          <IconPlayerPlay size={18} />
        </ActionIcon>
      </Group>
      <form onSubmit={submit}>
        <Stack gap="sm">
          <NumberInput label="AppID" placeholder="730" hideControls {...form.getInputProps('appId')} />
          <Group grow align="end">
            <NumberInput label="DepotID (optional)" placeholder="731" hideControls {...form.getInputProps('depotId')} />
            <TextInput label="ManifestId (optional)" placeholder="7617088375292372759" {...form.getInputProps('manifestId')} />
          </Group>
          <Group grow>
            <TextInput label="Branch" placeholder="public" {...form.getInputProps('branch')} />
            <NumberInput label="Max downloads" hideControls {...form.getInputProps('maxDownloads')} />
          </Group>
          <TextInput label="Install dir (optional)" placeholder="/data/steamdds" {...form.getInputProps('dir')} />
          <Group grow>
            <TextInput label="Username (optional)" {...form.getInputProps('username')} />
            <PasswordInput label="Password (optional)" {...form.getInputProps('password')} />
          </Group>
          <Group justify="space-between">
            <Switch label="Validate" {...form.getInputProps('validate', { type: 'checkbox' })} />
            <Switch label="Remember password" {...form.getInputProps('rememberPassword', { type: 'checkbox' })} />
          </Group>
          <Button type="submit">Create Job</Button>
        </Stack>
      </form>
    </Card>
  )
}
