/**
 * Proxies every `/api/settings/**` call through to the DotNetBoost.Settings API.
 *
 * The browser therefore only ever talks to this app's own origin, which keeps the API free
 * of CORS configuration and lets the target move between environments by changing
 * `NUXT_SETTINGS_API_URL` alone.
 */
export default defineEventHandler(async (event) => {
  const { settingsApiUrl } = useRuntimeConfig(event)
  const path = getRouterParam(event, 'path') ?? ''
  const { search } = getRequestURL(event)

  const target = `${settingsApiUrl.replace(/\/$/, '')}/api/settings/${path}${search}`

  return proxyRequest(event, target)
})
