import type { SettingGroup } from '~/types'

/**
 * The settings groups the dashboard exposes, one tab each.
 *
 * `route` must match the `[SettingGroup("…")]` attribute on the C# class — that is where
 * `MapSettingsEndpoints()` mounts the group. Everything else is presentation: the form
 * fields come from whatever the API returns, so a property added to the C# class appears
 * without an entry below.
 */
export const settingsGroups: SettingGroup[] = [
  {
    route: 'mail-server',
    name: 'MailSettings',
    label: 'Mail Settings',
    description: 'SMTP transport used for outgoing mail. Saved changes take effect immediately — no redeploy.',
    icon: 'i-lucide-mail',
    to: '/settings',
    fields: {
      host: { label: 'SMTP Host', description: 'Hostname of the mail server, e.g. smtp.example.com.' },
      port: { label: 'Port', description: '1–65535. Validated server-side by MailSettingsValidator.', min: 1, max: 65535, step: 1 },
      useSsl: { label: 'Use SSL', description: 'Negotiate TLS when connecting.' },
      password: { label: 'Password', description: 'Marked [Sensitive] — encrypted at rest with AES-256-GCM.', sensitive: true }
    }
  },
  {
    route: 'payment',
    name: 'PaymentSettings',
    label: 'Payment Settings',
    description: 'Payment gateway credentials and limits. Sandbox mode keeps transactions off the live gateway.',
    icon: 'i-lucide-credit-card',
    to: '/settings/payment',
    fields: {
      gatewayUrl: { label: 'Gateway URL', description: 'Base address of the payment provider.', type: 'url' },
      apiKey: { label: 'API Key', description: 'Marked [Sensitive] — encrypted at rest with AES-256-GCM.', sensitive: true },
      maxAmount: { label: 'Maximum Amount', description: 'Largest single transaction that will be accepted.', min: 0, step: 0.01 },
      sandboxMode: { label: 'Sandbox Mode', description: 'Send transactions to the sandbox environment instead of the live gateway.' }
    }
  }
]

export function findSettingsGroup(route: string): SettingGroup | undefined {
  return settingsGroups.find(group => group.route === route)
}

/** `useSsl` -> `Use Ssl`. Fallback label for properties with no metadata. */
export function humanizeKey(key: string): string {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  return spaced.charAt(0).toUpperCase() + spaced.slice(1)
}
