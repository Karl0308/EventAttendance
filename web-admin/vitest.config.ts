// The test runner for `web-admin`, owed before D2 (MDVault #206).
//
// A config of its own rather than a `test` block bolted onto `vite.config.ts`, and Vitest reads this
// one *instead of* that one. Nothing the app build does is wanted here: `base: "/EventAttendance/"`
// is a GitHub Pages concern, and `@vitejs/plugin-react` exists for Fast Refresh and the JSX
// transform, neither of which a suite with no JSX in it needs. Keeping them apart means a change to
// how the SPA is bundled cannot alter what the tests run against, and a change here cannot reach the
// artefact that gets deployed.
//
// `environment: "node"` is the default for every file on purpose. Three of the four subjects
// (`eventStatus`, `eventDraft`, `apiGuidance`) are pure and React-free, so a DOM would be startup
// cost buying nothing. The one file that renders a hook opts in with a `@vitest-environment` docblock
// of its own — see `test/useApiMutation.test.ts`.

import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    environment: "node",

    // Sets the baseline time zone before any test file is imported. It is a setup file rather than
    // `test.env` because it *asserts* the zone took effect instead of assuming it did — see
    // `test/timeZone.ts` for why that assertion is the point rather than belt-and-braces.
    setupFiles: ["./test/setup.ts"],

    // No `globals`. Every test imports `describe`/`it`/`expect` from "vitest" explicitly, which is
    // what lets the suite be type-checked by an ordinary tsconfig with no ambient test types.
    globals: false,

    restoreMocks: true,
  },
});
