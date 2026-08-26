<script setup lang="ts">
import type { NavigationMenuItem } from '@nuxt/ui'

const { apiReferenceUrl } = useRuntimeConfig().public

// One top-menu entry per settings group. Adding a group to `settingsGroups` (and a page
// under `pages/settings/`) is all it takes to get another tab.
const links = [
  settingsGroups.map(group => ({
    label: group.label,
    icon: group.icon,
    to: group.to,
    exact: group.to === '/settings'
  })),
  [{
    label: 'API reference',
    icon: 'i-lucide-book-open',
    to: apiReferenceUrl,
    target: '_blank'
  }]
] satisfies NavigationMenuItem[][]
</script>

<template>
  <UDashboardPanel id="settings" :ui="{ body: 'lg:py-12' }">
    <template #header>
      <UDashboardNavbar title="Settings">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>
      </UDashboardNavbar>

      <UDashboardToolbar>
        <!-- NOTE: The `-mx-1` class is used to align with the `DashboardSidebarCollapse` button here. -->
        <UNavigationMenu :items="links" highlight class="-mx-1 flex-1" />
      </UDashboardToolbar>
    </template>

    <template #body>
      <div class="flex flex-col gap-4 sm:gap-6 lg:gap-12 w-full lg:max-w-2xl mx-auto">
        <NuxtPage />
      </div>
    </template>
  </UDashboardPanel>
</template>
