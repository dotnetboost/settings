// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  modules: [
    '@nuxt/eslint',
    '@nuxt/ui',
    '@vueuse/nuxt'
  ],

  // Client-rendered single-page app. Nitro is still used, but only to proxy the
  // settings API (see server/api/settings/[...path].ts) — never to render pages.
  ssr: false,

  devtools: {
    enabled: true
  },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    // Base address of the DotNetBoost.Settings API. Read by the proxy handler only, so the
    // browser never sees it. Override with NUXT_SETTINGS_API_URL.
    settingsApiUrl: 'http://localhost:5199',

    public: {
      // Linked from the sidebar — the API's own Scalar reference.
      // Override with NUXT_PUBLIC_API_REFERENCE_URL.
      apiReferenceUrl: 'http://localhost:5199/scalar'
    }
  },

  compatibilityDate: '2026-06-30',

  eslint: {
    config: {
      stylistic: {
        commaDangle: 'never',
        braceStyle: '1tbs'
      }
    }
  }
})
