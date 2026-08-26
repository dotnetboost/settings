<script setup lang="ts">
import type { NavigationMenuItem } from '@nuxt/ui'

const { apiReferenceUrl } = useRuntimeConfig().public

// Registers the `g-h` / `g-s` navigation shortcuts.
useDashboard()

const open = ref(false)

const close = () => {
  open.value = false
}

const links = [[{
  label: 'Overview',
  icon: 'i-lucide-house',
  to: '/',
  onSelect: close
}, {
  label: 'Settings',
  icon: 'i-lucide-settings',
  to: '/settings',
  defaultOpen: true,
  type: 'trigger',
  // One child per settings group, mirroring the top menu on the Settings page.
  children: settingsGroups.map(group => ({
    label: group.label,
    to: group.to,
    exact: group.to === '/settings',
    onSelect: close
  }))
}], [{
  label: 'API reference',
  icon: 'i-lucide-book-open',
  to: apiReferenceUrl,
  target: '_blank'
}, {
  label: 'Documentation',
  icon: 'i-simple-icons-github',
  to: 'https://github.com/dotnetboost/settings',
  target: '_blank'
}]] satisfies NavigationMenuItem[][]

const groups = computed(() => [{
  id: 'links',
  label: 'Go to',
  items: links.flat()
}])
</script>

<template>
  <UDashboardGroup unit="rem">
    <UDashboardSidebar
      id="default"
      v-model:open="open"
      collapsible
      resizable
      class="bg-elevated/25"
      :ui="{ footer: 'lg:border-t lg:border-default' }"
    >
      <template #header="{ collapsed }">
        <AppBrand :collapsed="collapsed" />
      </template>

      <template #default="{ collapsed }">
        <UDashboardSearchButton :collapsed="collapsed" class="bg-transparent ring-default" />

        <UNavigationMenu
          :collapsed="collapsed"
          :items="links[0]"
          orientation="vertical"
          tooltip
          popover
        />

        <UNavigationMenu
          :collapsed="collapsed"
          :items="links[1]"
          orientation="vertical"
          tooltip
          class="mt-auto"
        />
      </template>

      <template #footer="{ collapsed }">
        <AppearanceMenu :collapsed="collapsed" />
      </template>
    </UDashboardSidebar>

    <UDashboardSearch :groups="groups" />

    <slot />
  </UDashboardGroup>
</template>
