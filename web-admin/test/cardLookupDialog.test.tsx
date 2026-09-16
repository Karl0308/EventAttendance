/** @vitest-environment happy-dom */

// `CardLookupDialog` — Task 2, "whose card is this?" (QA Q5/Q6).
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The dialog's whole reason to exist is answering questions a student-search box cannot, and the
// four cases below are exactly those questions, each pinned against the failure it would silently
// become if the branch it needs were ever removed:
//
// 1. **A withdrawn card is an ordinary result, not an error.** `isActive: false` must render as
//    "Deactivated" beside the holder, not be dropped or treated as a miss.
// 2. **A fragment matches inside a longer serial**, and the request carries the fragment exactly as
//    typed — no reformatting on the way to the wire.
// 3. **A multi-match renders every row.** One withdrawn serial can legitimately have had several
//    holders (ADR-001 D-3), and nothing here may collapse that into a single answer.
// 4. **The 400 (`FragmentUnusable`) is a message about the input, never "no results".** This is the
//    one most likely to be silently merged with the empty-results branch by a future edit, because
//    both branches render when `result.data` looks empty-ish — the test below is written so that
//    swapping the `fragmentUnusable` check for `result.data?.cards.length === 0` fails it.
//
// Leading zeros matter to the fixture in test 2 as well: `0012503326` and `12503326` are different
// cards, so the request built from what was typed must not have quietly dropped the padding.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

import CardLookupDialog from "../src/components/CardLookupDialog";
import { beginSession, resetSessionForTests } from "../src/authSession";
import type { AuthUser } from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: ["students.read"],
};

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

/** One `CardMatchDto` row, filled in from a minimal override so each test states only what it needs. */
const cardMatchJson = (over: Partial<Record<string, unknown>>) => ({
  cardId: "c-0000",
  cardUid: "0012503326",
  label: null,
  isActive: true,
  issuedAt: "2025-06-01T00:00:00Z",
  deactivatedAt: null,
  studentId: "s-0000",
  studentNumber: "2026000001",
  fullName: "Ana Cruz",
  studentStatus: "Active",
  ...over,
});

/** Every URL asked for, so a test can assert on the request rather than guess from the render. */
let asked: string[] = [];

/**
 * Serves `GET /cards` and refuses anything else, the same discipline `studentsPaging.test.tsx`
 * uses — a request this dialog was never supposed to make should fail the test loudly rather than
 * be quietly ignored by a stub that answers everything.
 */
function serve(reply: (query: URLSearchParams) => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    const url = String(input);
    asked.push(url);
    if (!url.includes("/cards")) {
      throw new Error(`the card lookup dialog asked for something unexpected: ${url}`);
    }
    const query = new URL(url, "http://localhost").searchParams;
    return Promise.resolve(reply(query));
  });
}

const lastQuery = () => new URL(asked[asked.length - 1], "http://localhost").searchParams;

async function show() {
  const rendered = render(<CardLookupDialog onClose={() => {}} onViewStudent={() => {}} />);
  await act(async () => {});
  return rendered;
}

/** Types into the fragment box and waits out the debounce, the same budget `Students.tsx` uses. */
async function type(fragment: string) {
  await act(async () => {
    fireEvent.change(screen.getByLabelText(/card serial or fragment/i), {
      target: { value: fragment },
    });
    await new Promise((resolve) => setTimeout(resolve, 400));
  });
}

const pageText = () => document.body.textContent ?? "";

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

describe("a withdrawn card (QA Q5)", () => {
  it("still finds its holder, and shows it as Deactivated rather than as an error or a miss", async () => {
    serve(() =>
      json(200, {
        items: [
          cardMatchJson({
            isActive: false,
            deactivatedAt: "2026-01-15T00:00:00Z",
            fullName: "Withdrawn Holder",
            studentNumber: "2026000099",
          }),
        ],
        page: 1,
        pageSize: 50,
        total: 1,
        hasMore: false,
      }),
    );

    await show();
    await type("0012503326");

    expect(screen.getByText("Deactivated")).toBeTruthy();
    expect(screen.getByText(/Withdrawn Holder/)).toBeTruthy();
    expect(screen.queryByText("Active")).toBeNull();
    // This is an ordinary result, not a failure: neither error surface is on screen.
    expect(pageText()).not.toMatch(/Could not search cards/);
  });
});

describe("a partial fragment (QA Q6)", () => {
  it("matches inside a longer serial, and sends the fragment exactly as typed", async () => {
    serve(() =>
      json(200, {
        items: [cardMatchJson({ cardUid: "0012503326" })],
        page: 1,
        pageSize: 50,
        total: 1,
        hasMore: false,
      }),
    );

    await show();
    await type("2503");

    expect(screen.getByText("0012503326")).toBeTruthy();
    expect(lastQuery().get("cardUid")).toBe("2503");
  });

  it("does not drop a leading zero on the way to the request", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 50, total: 0, hasMore: false }));

    await show();
    await type("0012503326");

    // `0012503326` and `12503326` are different cards. If this ever normalized on the way out, the
    // request would be indistinguishable from a search for the un-padded serial.
    expect(lastQuery().get("cardUid")).toBe("0012503326");
  });
});

describe("a multi-match (ADR-001 D-3)", () => {
  it("renders every match, and nominates none of them as the answer", async () => {
    serve(() =>
      json(200, {
        items: [
          cardMatchJson({
            cardId: "c-1",
            studentId: "s-1",
            studentNumber: "2026000001",
            fullName: "First Holder",
            isActive: false,
          }),
          cardMatchJson({
            cardId: "c-2",
            studentId: "s-2",
            studentNumber: "2026000002",
            fullName: "Second Holder",
            isActive: false,
          }),
        ],
        page: 1,
        pageSize: 50,
        total: 2,
        hasMore: false,
      }),
    );

    await show();
    await type("04a7b8c9");

    expect(screen.getByText(/First Holder/)).toBeTruthy();
    expect(screen.getByText(/Second Holder/)).toBeTruthy();
    expect(
      screen.getAllByRole("button", { name: /View .* in the roster/ }),
    ).toHaveLength(2);
  });

  it("tells the user more matched than is shown, rather than silently truncating", async () => {
    serve(() =>
      json(200, {
        items: [cardMatchJson({ cardId: "c-1" })],
        page: 1,
        pageSize: 50,
        total: 3,
        hasMore: true,
      }),
    );

    await show();
    await type("04");

    expect(pageText()).toMatch(/Showing the first 1 of 3 matching cards/);
  });
});

describe("the 400 branch — a fragment that normalizes to empty", () => {
  it("renders a message about the input, and never the empty-results copy", async () => {
    serve(() =>
      json(400, {
        status: 400,
        title: "The request could not be processed.",
        detail: "The search fragment does not contain any letters or digits.",
        code: "FragmentUnusable",
      }),
    );

    await show();
    await type("-");

    expect(pageText()).toMatch(/does not contain any letters or digits/);
    // The distinction this test exists to protect: an empty-after-normalization fragment is a fact
    // about the request, not a fact about the roster, and must never be rendered as the ordinary
    // "no matches" state.
    expect(pageText()).not.toMatch(/No card matches/);
    // Nor as the generic transport-failure heading — it is styled as information about what was
    // typed, not as "something went wrong with the search".
    expect(pageText()).not.toMatch(/Could not search cards/);
  });

  it("renders the generic error surface for a failure that is not FragmentUnusable", async () => {
    serve(() =>
      json(500, { status: 500, title: "Service unavailable.", detail: "Try again." }),
    );

    await show();
    await type("04a7");

    expect(pageText()).toMatch(/Could not search cards/);
    expect(pageText()).not.toMatch(/does not contain any letters or digits/);
  });

  it("renders the ordinary empty state for a fragment that legitimately matched nothing", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 50, total: 0, hasMore: false }));

    await show();
    await type("zzzzzzzz");

    expect(pageText()).toMatch(/No card matches/);
    expect(pageText()).not.toMatch(/does not contain any letters or digits/);
  });
});
