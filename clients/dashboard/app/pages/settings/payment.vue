<script setup lang="ts">
const group = findSettingsGroup('payment')!
</script>

<template>
  <SettingsGroupForm :group="group" />
</template>
