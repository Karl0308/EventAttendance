/** @vitest-environment happy-dom */

// `StudentClassificationsDialog` — Task 3, one picker per axis.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The harness below (`Harness`) wires the dialog exactly the way `Students.tsx` does: the two
// writes live outside the dialog, and the dialog only ever sees the student prop the harness holds
// and the running/failure state of the two `useApiMutation`s. That is deliberate — it is what lets
// this file test the one thing that matters most and is easiest to get backwards: **a failed write
// must not be rendered as though it landed.** The harness only advances `student.classifications`
// from the *response*, in the `succeeded` branch, exactly as `submitAssignClassification` does — so
// a 409 leaves the picker showing what it showed before the click, and the alert is the only thing
// that changed.
//
// Four things beyond that:
//
// 1. **At most one per axis, several axes at once (QA Q2).** A fixture with two axes held is
//    asserted axis by axis, not by a total count, so a bug that put both on the same axis would
//    still pass a count-only assertion.
// 2. **Retired-but-held is shown and is not offered.** Both halves of that sentence are asserted:
//    the retired banner and Clear button render, and the Select's own option list does not repeat
//    the retired entry — it is excluded from the vocabulary the picker is built from, per contract.
// 3. **`SUPERVISORY/MANAGERIAL` survives untouched** — end to end through the fixture, the option
//    label and the assign call.
// 4. **An empty set (34 of the sampled roster) renders as ordinary**, not as a missing-data state.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";

import StudentClassificationsDialog from "../src/components/StudentClassificationsDialog";
import { api } from "../src/api";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type {
  AuthUser,
  Classification,
  Student,
  StudentClassification,
} from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.write"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const student = (over: Partial<Student> = {}): Student => ({
  id: "s-1",
  studentNumber: "2026000001",
  fullName: "Ana Cruz",
  firstName: "Ana",
  lastName: "Cruz",
  status: "Active",
  cards: [],
  classifications: [],
  ...over,
});

/** The eight seeded values across the four axes, exactly as QA described the population. */
const VOCAB: Classification[] = [
  { id: "v-student", name: "STUDENT", nameKey: "student", axis: "Student", isActive: true, studentCount: 10 },
  { id: "v-nap", name: "NAP", nameKey: "nap", axis: "Student", isActive: true, studentCount: 2 },
  { id: "v-acad", name: "ACAD", nameKey: "acad", axis: "Personnel", isActive: true, studentCount: 5 },
  { id: "v-ant", name: "ANT", nameKey: "ant", axis: "Personnel", isActive: true, studentCount: 1 },
  {
    id: "v-supervisory",
    name: "SUPERVISORY/MANAGERIAL",
    nameKey: "supervisory-managerial",
    axis: "Personnel",
    isActive: true,
    studentCount: 3,
  },
  { id: "v-friars", name: "USA FRIARS", nameKey: "usa-friars", axis: "Friars", isActive: true, studentCount: 4 },
  { id: "v-c2b2", name: "C2B2", nameKey: "c2b2", axis: "Special", isActive: true, studentCount: 2 },
  { id: "v-cfi", name: "CFI", nameKey: "cfi", axis: "Special", isActive: true, studentCount: 1 },
];

function held(over: Partial<StudentClassification>): StudentClassification {
  return {
    classificationId: "v-student",
    name: "STUDENT",
    axis: "Student",
    isActive: true,
    assignedAt: "2026-01-01T00:00:00Z",
    ...over,
  };
}

let asked: { method: string; url: string }[] = [];

/**
 * Serves `GET /classifications` from `vocabulary`, and any `PUT`/`DELETE` on the assignment route
 * from `writeReply` — a function so a test can answer 200 once and 409 the next time, or vice versa.
 */
function serve(
  vocabulary: Classification[],
  writeReply?: (method: string, url: string) => Response,
): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    asked.push({ method, url });

    if (url.includes("/classifications") && !url.includes("/students/")) {
      return Promise.resolve(
        json(200, { items: vocabulary, page: 1, pageSize: 200, total: vocabulary.length, hasMore: false }),
      );
    }
    if (url.includes("/students/") && url.includes("/classifications/")) {
      if (writeReply === undefined) {
        throw new Error(`the dialog wrote to ${method} ${url} but this test gave it no write reply`);
      }
      return Promise.resolve(writeReply(method, url));
    }
    throw new Error(`the classifications dialog asked for something unexpected: ${method} ${url}`);
  });
}

/**
 * The real wiring `Students.tsx` uses, trimmed to what this dialog needs: the writes live outside
 * the dialog, and `classifications` only ever advances from a *successful* response.
 */
function Harness({ initial }: { initial: Student }) {
  const [current, setCurrent] = useState(initial);
  const [actingAxis, setActingAxis] = useState<string | undefined>(undefined);
  const [assignState, setAssignState] = useState<{ running: boolean; failure: { error: unknown } | undefined }>({
    running: false,
    failure: undefined,
  });
  const [clearState, setClearState] = useState<{ running: boolean; failure: { error: unknown } | undefined }>({
    running: false,
    failure: undefined,
  });

  const submitAssign = (target: Classification, _previous: StudentClassification | undefined) => {
    setActingAxis(target.axis);
    setAssignState({ running: true, failure: undefined });
    void api
      .assignClassification(current.id, target.id)
      .then((result) => {
        setAssignState({ running: false, failure: undefined });
        // Advances from the response, exactly as `Students.tsx` does — never optimistically.
        setCurrent((s) => ({ ...s, classifications: result.classifications }));
      })
      .catch((error: unknown) => {
        setAssignState({ running: false, failure: { error } });
      });
  };

  const submitClear = (previous: StudentClassification) => {
    setActingAxis(previous.axis);
    setClearState({ running: true, failure: undefined });
    void api
      .clearClassification(current.id, previous.classificationId)
      .then((result) => {
        setClearState({ running: false, failure: undefined });
        setCurrent((s) => ({ ...s, classifications: result.classifications }));
      })
      .catch((error: unknown) => {
        setClearState({ running: false, failure: { error } });
      });
  };

  return (
    <StudentClassificationsDialog
      student={current}
      onClose={() => {}}
      actingAxis={actingAxis}
      assign={{ running: assignState.running, failure: assignState.failure, submit: submitAssign }}
      clear={{ running: clearState.running, failure: clearState.failure, submit: submitClear }}
    />
  );
}

async function show(initial: Student) {
  const rendered = render(<Harness initial={initial} />);
  await act(async () => {});
  return rendered;
}

const pageText = () => document.body.textContent ?? "";

/** Opens the Select for one axis and returns its option listbox. */
function openAxis(axis: string) {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: axis }));
  return within(screen.getByRole("listbox"));
}

/**
 * The closed Select's own rendered value, whitespace stripped.
 *
 * An axis nobody holds reads as {@link NONE_TEXT}, and that is the property these assertions pin.
 * It depends on `displayEmpty` on the `<Select>`: MUI gates rendering a `MenuItem`'s content for the
 * CLOSED control on that prop, so without it this span is a zero-width space no matter what the
 * none-item's children say, and "— none —" appears only once the menu is open.
 *
 * Read what that does and does not mean, because an earlier version of this comment got it wrong and
 * the wrong version is what made the fix look free. Without the prop the *span* is empty; the
 * *control* is not, because the axis label sits in it exactly as it does in any untouched outlined
 * field. So this is a legibility gain, not the repair of a blank box — and `displayEmpty` has to be
 * paired with `shrink` on the `InputLabel`, or MUI cuts the notch open and draws the label straight
 * over the placeholder. Delete either prop and this file goes red — `displayEmpty` here, `shrink` in the label test below.
 */
const closedValueOf = (axis: string) =>
  (screen.getByRole("combobox", { name: axis }).textContent ?? "").replace(/[\s​]+/g, "");

/** {@link closedValueOf} applied to the "nothing held on this axis" placeholder. */
const NONE_TEXT = "—none—";

/** The `InputLabel` id `AxisPicker` derives for an axis, mirrored so the label can be read back. */
const labelIdFor = (axis: string) => `classification-axis-${axis}-label`;

beforeEach(() => {
  asked = [];
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  resetSessionForTests();
});

describe("the ordinary populations", () => {
  it("renders an empty set as ordinary — no selection on any axis, no error (34 of the roster)", async () => {
    serve(VOCAB);

    await show(student({ classifications: [] }));

    for (const axis of ["Student", "Personnel", "Friars", "Special"]) {
      expect(closedValueOf(axis)).toBe(NONE_TEXT);
    }
    expect(pageText()).not.toMatch(/Could not load/i);
  });

  it("keeps the axis label out of the placeholder's way when nothing is held", async () => {
    // Fences the OTHER half of the `displayEmpty` pairing, which no assertion above can reach.
    // `displayEmpty` force-notches the outlined border but does not shrink the label, and MUI
    // derives shrink from `filled` — false for an empty value. Unpaired, the full-size label is
    // drawn inside the box on top of "— none —", through a notch cut open for a label that never
    // moved into it. Neither `closedValueOf` nor the accessible-name lookup can see that, so
    // without this the pairing is held by a comment alone.
    serve(VOCAB);

    await show(student({ classifications: [] }));

    for (const axis of ["Student", "Personnel", "Friars", "Special"]) {
      const label = document.querySelector(`label[id="${labelIdFor(axis)}"]`);
      expect(label, `no label rendered for the ${axis} axis`).not.toBeNull();
      expect(label?.getAttribute("data-shrink"), `the ${axis} label is sitting over its placeholder`).toBe("true");
    }
  });

  it("holds several axes at once, each shown on its own axis and not on any other", async () => {
    serve(VOCAB);

    await show(
      student({
        classifications: [
          held({ classificationId: "v-nap", name: "NAP", axis: "Student" }),
          held({ classificationId: "v-acad", name: "ACAD", axis: "Personnel" }),
        ],
      }),
    );

    expect(screen.getByRole("combobox", { name: "Student" }).textContent).toBe("NAP");
    expect(screen.getByRole("combobox", { name: "Personnel" }).textContent).toBe("ACAD");
    expect(closedValueOf("Friars")).toBe(NONE_TEXT);
    expect(closedValueOf("Special")).toBe(NONE_TEXT);
  });
});

describe("SUPERVISORY/MANAGERIAL — the slash", () => {
  it("renders verbatim in the picker and survives an assign end to end", async () => {
    let writes = 0;
    serve(VOCAB, (method, url) => {
      writes += 1;
      expect(method).toBe("PUT");
      expect(url).toContain("/classifications/v-supervisory");
      return json(200, {
        studentId: "s-1",
        classifications: [held({ classificationId: "v-supervisory", name: "SUPERVISORY/MANAGERIAL", axis: "Personnel" })],
        message: "assigned",
      });
    });

    await show(student());

    const listbox = openAxis("Personnel");
    // Exactly as authored — slash included — is what the option must read, not a mangled or
    // path-split rendering of it.
    expect(listbox.getByText("SUPERVISORY/MANAGERIAL")).toBeTruthy();

    await act(async () => {
      fireEvent.click(listbox.getByText("SUPERVISORY/MANAGERIAL"));
    });

    expect(writes).toBe(1);
    expect(screen.getByRole("combobox", { name: "Personnel" }).textContent).toBe("SUPERVISORY/MANAGERIAL");
  });
});

describe("a retired-but-held classification", () => {
  it("is shown with its Clear action, and is not offered as a selectable option", async () => {
    // The vocabulary read excludes retired entries (default includeRetired=false), per contract —
    // so the fixture omits "OLD AXIS" from VOCAB entirely, as the real endpoint would.
    serve(VOCAB);

    await show(
      student({
        classifications: [
          held({ classificationId: "v-old", name: "OLD AXIS", axis: "Student", isActive: false }),
        ],
      }),
    );

    expect(pageText()).toMatch(/Currently holds a.*retired.*classification: OLD AXIS/s);
    expect(screen.getByRole("button", { name: /Clear retired classification OLD AXIS/i })).toBeTruthy();

    // The Select itself shows no selection underneath the retired notice…
    expect(closedValueOf("Student")).toBe(NONE_TEXT);
    // …and its own option list does not repeat the retired value: it cannot be re-selected, only
    // cleared or replaced.
    const listbox = openAxis("Student");
    expect(listbox.queryByText("OLD AXIS")).toBeNull();
  });
});

describe("409 on assign or clear — nothing was written", () => {
  it("tells the user the assignment was not applied, and does not optimistically show it as assigned", async () => {
    serve(VOCAB, () =>
      json(409, {
        status: 409,
        title: "Conflict.",
        detail: "This classification has been retired since this page was opened.",
        code: "ClassificationRetired",
      }),
    );

    await show(student({ classifications: [] }));

    const listbox = openAxis("Student");
    await act(async () => {
      fireEvent.click(listbox.getByText("NAP"));
    });

    // Told, in the heading that names a refusal rather than an unknown outcome.
    expect(pageText()).toMatch(/The classification was not assigned/);
    expect(pageText()).toMatch(/This classification has been retired since this page was opened/);

    // Not optimistically updated: the picker still shows nothing selected on this axis, because the
    // student prop only ever advances from a successful response.
    expect(closedValueOf("Student")).toBe(NONE_TEXT);
  });

  it("tells the user a clear was not applied, and leaves the held classification in place", async () => {
    serve(VOCAB, () =>
      json(409, {
        status: 409,
        title: "Conflict.",
        detail: "Somebody else changed this axis a moment ago.",
        code: "ConcurrentClassificationChange",
      }),
    );

    await show(
      student({ classifications: [held({ classificationId: "v-nap", name: "NAP", axis: "Student" })] }),
    );

    expect(screen.getByRole("combobox", { name: "Student" }).textContent).toBe("NAP");

    const listbox = openAxis("Student");
    await act(async () => {
      fireEvent.click(listbox.getByText("— none —"));
    });

    expect(pageText()).toMatch(/The classification was not cleared/);
    expect(pageText()).toMatch(/Somebody else changed this axis a moment ago/);

    // Still there — a 409 must not be rendered as though the clear landed.
    expect(screen.getByRole("combobox", { name: "Student" }).textContent).toBe("NAP");
  });
});
