/** A single setting value as it travels over the REST API. */
export type SettingValue = string | number | boolean

/** One settings group as returned by `GET api/settings/{route}`. */
export type SettingValues = Record<string, SettingValue>

/**
 * Presentation metadata for one property. Everything here is optional — the form is
 * generated from the values the API returns, so a property with no entry still renders.
 */
export interface SettingField {
  /** Overrides the label generated from the property name. */
  label?: string
  /** Helper text shown under the control. */
  description?: string
  /** Renders a masked input with a reveal toggle. Mirrors `[Sensitive]` on the C# property. */
  sensitive?: boolean
  /** Input type for string properties. */
  type?: 'text' | 'url' | 'email'
  min?: number
  max?: number
  step?: number
}

/** A `[SettingGroup]`-decorated C# class, as surfaced by the dashboard. */
export interface SettingGroup {
  /** Route segment from `[SettingGroup("…")]` — the API path this group lives on. */
  route: string
  /** C# class name, shown as the group identity. */
  name: string
  label: string
  description: string
  icon: string
  /** Dashboard route for this group's tab. */
  to: string
  fields: Record<string, SettingField>
}

/** ASP.NET Core `ValidationProblemDetails`. */
export interface ValidationProblem {
  title?: string
  status?: number
  detail?: string
  errors?: Record<string, string[]>
}
