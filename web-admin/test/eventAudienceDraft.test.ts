import { describe, expect, it } from "vitest";

import {
  AUDIENCE_SCOPES,
  AUDIENCE_TYPES,
  EMPTY_AUDIENCE_DRAFT,
  MIXED_AUDIENCE_TYPES,
  criteriaForType,
  draftFromAudience,
  draftWithAudienceType,
  validateAudience,
} from "../src/eventAudienceDraft";
import type { AudienceDraft } from "../src/eventAudienceDraft";
import type { AudienceCriteria, AudienceDefinition, AudienceScope, AudienceType } from "../src/types";

describe("the audience type hints", () => {
  it("says an Organization audience covers students as well as employees (ADR-011 D-82)", () => {
    const organization = AUDIENCE_TYPES.find((t) => t.value === "Organization");
    expect(organization?.hint).toBe("Students and employees in the chosen organizations.");
  });
});

// ---------------------------------------------------------------------------------------------
// Task 5: "Who it covers" on the mixed types (Department, Organization, Custom).
// ---------------------------------------------------------------------------------------------

const MIXED: AudienceType[] = ["Department", "Organization", "Custom"];
const SINGLE_POPULATION: AudienceType[] = [
  "Program",
  "YearLevel",
  "Section",
  "EmployeeClassification",
  "SpecificIndividuals",
];

const FULL_CRITERIA: AudienceCriteria = {
  scope: "Students",
  departments: ["CICSS"],
  programs: ["BSIT"],
  yearLevels: ["1"],
  sections: ["A"],
  classifications: ["Faculty"],
  organizations: ["CS Society"],
  studentIds: ["s-1"],
  personnelIds: ["p-1"],
};

const draftOf = (audienceType: AudienceType, criteria: AudienceCriteria): AudienceDraft => ({
  name: "Task 5",
  eventClassificationId: "c-1",
  audienceType,
  criteria,
});

describe("criteriaForType and scope", () => {
  it("lists exactly Department, Organization and Custom as the mixed types", () => {
    expect([...MIXED_AUDIENCE_TYPES].sort()).toEqual([...MIXED].sort());
  });

  it("keeps scope for every mixed type", () => {
    for (const type of MIXED) {
      expect(criteriaForType(type, FULL_CRITERIA).scope).toBe("Students");
    }
  });

  it("never sends scope for a single-population type, even when the draft still carries one", () => {
    for (const type of SINGLE_POPULATION) {
      expect("scope" in criteriaForType(type, FULL_CRITERIA)).toBe(false);
    }
  });

  it("still sends University-wide's scope, defaulting to Both", () => {
    expect(criteriaForType("UniversityWide", { scope: "Employees" })).toEqual({ scope: "Employees" });
    expect(criteriaForType("UniversityWide", {})).toEqual({ scope: "Both" });
  });

  it("omits an unchosen or invalid scope rather than sending it", () => {
    expect("scope" in criteriaForType("Department", { departments: ["CICSS"] })).toBe(false);
    expect("scope" in criteriaForType("Department", { scope: "Everyone" as AudienceScope })).toBe(false);
  });

  it("drops programs, year levels and sections from a Custom audience that covers Employees", () => {
    const sent = criteriaForType("Custom", { ...FULL_CRITERIA, scope: "Employees" });
    expect(sent.programs).toEqual([]);
    expect(sent.yearLevels).toEqual([]);
    expect(sent.sections).toEqual([]);
    expect(sent.classifications).toEqual(["Faculty"]);
    expect(sent.departments).toEqual(["CICSS"]);
    expect(sent.organizations).toEqual(["CS Society"]);
  });

  it("drops classifications from a Custom audience that covers Students", () => {
    const sent = criteriaForType("Custom", { ...FULL_CRITERIA, scope: "Students" });
    expect(sent.classifications).toEqual([]);
    expect(sent.programs).toEqual(["BSIT"]);
    expect(sent.yearLevels).toEqual(["1"]);
    expect(sent.sections).toEqual(["A"]);
  });

  it("keeps every dimension of a Custom audience that covers Both", () => {
    const sent = criteriaForType("Custom", { ...FULL_CRITERIA, scope: "Both" });
    expect(sent.programs).toEqual(["BSIT"]);
    expect(sent.classifications).toEqual(["Faculty"]);
  });
});

describe("validateAudience and scope", () => {
  it("blocks a mixed type with no scope, naming the missing answer", () => {
    for (const type of MIXED) {
      const result = validateAudience(draftOf(type, { departments: ["CICSS"], organizations: ["CS Society"] }));
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.errors.scope).toBe("Choose who it covers");
    }
  });

  it("accepts a mixed type once a scope is chosen, and sends it", () => {
    for (const type of MIXED) {
      const result = validateAudience(
        draftOf(type, { scope: "Both", departments: ["CICSS"], organizations: ["CS Society"] }),
      );
      expect(result.ok).toBe(true);
      if (result.ok) expect(result.request.criteria.scope).toBe("Both");
    }
  });

  it("reports the scope error beside, not instead of, the criterion error", () => {
    const result = validateAudience(draftOf("Department", {}));
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors.scope).toBe("Choose who it covers");
      expect(result.errors.criteria).toBe("Select at least one department.");
    }
  });

  it("does not ask a single-population type for a scope", () => {
    const result = validateAudience(draftOf("Program", { programs: ["BSIT"] }));
    expect(result.ok).toBe(true);
    if (result.ok) expect("scope" in result.request.criteria).toBe(false);
  });

  it("refuses a Custom audience whose scope leaves no side (Students with only a classification)", () => {
    const result = validateAudience(draftOf("Custom", { scope: "Students", classifications: ["Faculty"] }));
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors.criteria).toMatch(/Students need at least one/);
  });

  it("refuses a Custom audience whose scope leaves no side (Employees with only programs and sections)", () => {
    const result = validateAudience(draftOf("Custom", { scope: "Employees", programs: ["BSIT"], sections: ["A"] }));
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors.criteria).toMatch(/Employees need at least one/);
  });

  it("accepts Custom Employees with a classification, and Custom Students with a program", () => {
    expect(validateAudience(draftOf("Custom", { scope: "Employees", classifications: ["Faculty"] })).ok).toBe(true);
    expect(validateAudience(draftOf("Custom", { scope: "Students", programs: ["BSIT"] })).ok).toBe(true);
  });
});

describe("draftWithAudienceType", () => {
  it("clears University-wide's default Both when switching to a mixed type", () => {
    for (const type of MIXED) {
      const next = draftWithAudienceType(EMPTY_AUDIENCE_DRAFT, type);
      expect(next.audienceType).toBe(type);
      expect(next.criteria.scope).toBeUndefined();
    }
  });

  it("does not let a scope survive University-wide -> Program -> Department", () => {
    const viaProgram = draftWithAudienceType(EMPTY_AUDIENCE_DRAFT, "Program");
    expect(draftWithAudienceType(viaProgram, "Department").criteria.scope).toBeUndefined();
  });

  it("keeps a choice the admin already made when moving between mixed types", () => {
    const chosen = draftOf("Department", { scope: "Employees", departments: ["CICSS"] });
    expect(draftWithAudienceType(chosen, "Custom").criteria.scope).toBe("Employees");
  });

  it("leaves the other criteria alone, so switching back loses nothing", () => {
    const from = draftOf("UniversityWide", { scope: "Both", departments: ["CICSS"] });
    expect(draftWithAudienceType(from, "Department").criteria.departments).toEqual(["CICSS"]);
  });

  it("leaves a legacy row with no scope unselected, and unsaveable until chosen", () => {
    const legacy: AudienceDefinition = {
      id: "d-1",
      name: "Legacy",
      eventClassificationId: "c-1",
      eventClassificationName: "Institutional",
      audienceType: "Department",
      criteria: { departments: ["CICSS"] },
      isActive: true,
    };
    const draft = draftFromAudience(legacy);
    expect(draft.criteria.scope).toBeUndefined();
    const result = validateAudience(draft);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.errors.scope).toBe("Choose who it covers");
  });
});

describe("the shared scope labels", () => {
  it("offers Students, Employees and Both, with the wire values unchanged", () => {
    expect(AUDIENCE_SCOPES).toEqual([
      { value: "Students", label: "Students" },
      { value: "Employees", label: "Employees" },
      { value: "Both", label: "Both" },
    ]);
  });
});
