import { describe, expect, it } from "vitest";

import { AUDIENCE_TYPES } from "../src/eventAudienceDraft";

describe("the audience type hints", () => {
  it("says an Organization audience covers students as well as employees (ADR-011 D-82)", () => {
    const organization = AUDIENCE_TYPES.find((t) => t.value === "Organization");
    expect(organization?.hint).toBe("Students and employees in the chosen organizations.");
  });
});
