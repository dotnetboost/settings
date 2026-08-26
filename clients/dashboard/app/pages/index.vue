<script setup lang="ts">
import type { SettingValues } from '~/types'

const { apiReferenceUrl } = useRuntimeConfig().public

interface GroupStatus {
  online: boolean
  summary: string
}

const status = reactive<Record<string, GroupStatus>>({})
const loading = ref(true)

async function probe() {
  loading.value = true

  await Promise.all(settingsGroups.map(async (group) => {
    try {
      const values = await $fetch<SettingValues>(`/api/settings/${group.route}`)
      const count = Object.keys(values).length

      status[group.route] = {
        online: true,
        summary: `${count} ${count === 1 ? 'property' : 'properties'} loaded`
      }
    } catch (error) {
      status[group.route] = { online: false, summary: (error as Error).message }
    }
  }))

  loading.value = false
}

const reachable = computed(() => settingsGroups.every(group => status[group.route]?.online))

onMounted(probe)
</script>

<template>
  <UDashboardPanel id="home">
    <template #header>
      <UDashboardNavbar title="Overview" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UBadge
            :color="loading ? 'neutral' : reachable ? 'success' : 'error'"
            variant="subtle"
            :icon="loading ? 'i-lucide-loader-circle' : reachable ? 'i-lucide-plug' : 'i-lucide-plug-zap'"
            :label="loading ? 'Connecting…' : reachable ? 'API connected' : 'API unreachable'"
          />

          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="loading"
            aria-label="Refresh"
            @click="probe"
          />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        title="Runtime settings, without a redeploy"
        description="This dashboard reads and writes settings through the REST endpoints that DotNetBoost.Settings.API generates for every [SettingGroup] class. Pick a group to edit its values."
        variant="naked"
        class="mb-2"
      >
        <UButton
          label="Open API reference"
          icon="i-lucide-book-open"
          color="neutral"
          variant="outline"
          :to="apiReferenceUrl"
          target="_blank"
          class="w-fit lg:ms-auto"
        />
      </UPageCard>

      <UPageGrid class="lg:grid-cols-2">
        <UPageCard
          v-for="group in settingsGroups"
          :key="group.route"
          :title="group.label"
          :description="group.description"
          :icon="group.icon"
          :to="group.to"
          variant="subtle"
          spotlight
        >
          <template #footer>
            <div class="flex flex-wrap items-center gap-2">
              <UBadge
                color="neutral"
                variant="subtle"
                class="font-mono"
                :label="`api/settings/${group.route}`"
              />

              <USkeleton v-if="loading" class="h-5 w-32" />
              <UBadge
                v-else
                :color="status[group.route]?.online ? 'success' : 'error'"
                variant="subtle"
                :label="status[group.route]?.summary"
              />
            </div>
          </template>
        </UPageCard>
      </UPageGrid>
    </template>
  </UDashboardPanel>
</template>
