// End-to-end suite for the BACKOFFICE job orders (Tasks 1-5), run in a real Chromium against the real
// SPA served by `vite` (dev). NOT wired into CI; run it with `npm run test:e2e`.
//
// TRADE-OFF, stated once so nobody reads more into a green run than it says: every `/api/v1` request is
// answered by Playwright route interception from typed fixtures (`e2e/fixtures`). There is no API, no
// database and no secret. A pass therefore proves the SPA's BEHAVIOUR given those responses - what it
// renders, what it sends, what it refuses - and does NOT prove front-end <-> back-end wiring. Backend
// correctness (the workbook's 13 columns, the derived Department, scope rules on save) is EAMS.Tests'
// job, and the mocked response shapes here are only as true as the fixtures' typing against `src/types.ts`.
//
// An unmocked request fails the test, loudly, naming the URL (see `e2e/support/mockApi.ts`).

import { defineConfig, devices } from "@playwright/test";

const PORT = Number(process.env.E2E_PORT ?? 5188);
const ORIGIN = `http://localhost:${PORT}`;

export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.spec.ts",
  fullyParallel: true,
  // Two, not the CPU count: the dev server transforms MUI's modules on demand, and more workers than
  // that turns the cold first page loads into the timeouts this suite would then be blamed for.
  workers: 2,
  timeout: 60_000,
  forbidOnly: true,
  retries: 0,
  reporter: [["list"]],
  outputDir: "./test-results",
  use: {
    baseURL: ORIGIN,
    trace: "retain-on-failure",
    // Wide enough that the DataGrid does not virtualise away the later columns of the students grid.
    viewport: { width: 1920, height: 1080 },
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1920, height: 1080 } } }],
  webServer: {
    command: `npx vite --port ${PORT} --strictPort`,
    url: ORIGIN,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { VITE_API_BASE_URL: "/api/v1" },
  },
});
