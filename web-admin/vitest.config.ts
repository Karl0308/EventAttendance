// The test runner for `web-admin`, owed before D2 (MDVault #206).
//
// A config of its own rather than a `test` block bolted onto `vite.config.ts`, and Vitest reads this
// one *instead of* that one. Nothing the app build does is wanted here: `base: "/EventAttendance/"`
// is a GitHub Pages concern, and `@vitejs/plugin-react` exists for Fast Refresh and the JSX
// transform, neither of which a suite with no JSX in it needs. Keeping them apart means a change to
// how the SPA is bundled cannot alter what the tests run against, and a change here cannot reach the
// artefact that gets deployed.
//
// `environment: "node"` is the default for every file on purpose. The pure, React-free subjects
// (`eventStatus`, `eventDraft`, `apiGuidance`) would pay startup cost for a DOM that buys them
// nothing. Every file that renders anything — or that reaches `document.cookie`, `sessionStorage` or
// `fetch` — opts in with a `@vitest-environment happy-dom` docblock of its own; see
// `test/useApiMutation.test.ts` for the original of that pattern.

import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // `.tsx` as well as `.ts`, because two subjects are components now (`PermissionGuard`) and a
    // component test written in a `.ts` file has to spell its tree out in `createElement` calls —
    // which is a worse test of a thing whose whole job is what it renders. Vite transforms JSX with
    // esbuild using `tsconfig.test.json`'s `"jsx": "react-jsx"`; no plugin is involved, which is why
    // this config still deliberately loads none.
    include: ["test/**/*.test.ts", "test/**/*.test.tsx"],
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
