import type { E2EConfig } from 'e2e';

// The upstream built-in engines drive browsers and mobile devices, not WPF.
// Native steps use Scout's isolated real-WPF driver; no browser UI is substituted.
export default {
  projectId: 'scout-windows-e2e',
  targets: [{ name: 'windows-native', platform: 'windows' }],
  tests: ['tests/**/*.e2e.ts'],
  workers: 1,
  retries: 0,
  timeout: 600_000,
  trace: 'off',
  video: 'off',
  cache: 'off',
  output: process.env.SCOUT_E2E_OUTPUT ?? '.e2e',
  reporters: ['list', 'junit', 'markdown'],
} satisfies E2EConfig;
