<script setup lang="ts">
const group = findSettingsGroup('mail-server')!
</script>

<template>
  <SettingsGroupForm :group="group" />
</template>
