// `GET /events/{id}/untapped` — the tap picker's input, read one searched page at a time.
//
// It replaced a client-side composition (every student minus every attendance row) that stopped working
// at 21,493 students. These pin the request the picker makes and the fail-loud parse of what comes back:
// field names are the contract's (`UntappedPersonDto`), and a server that drops one must fail at the
// seam rather than reach the picker as `undefined`.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const STUDENT_ID = "22222222-2222-4222-8222-222222222222";
const PERSONNEL_ID = "33333333-3333-4333-8333-333333333333";

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const studentPerson = (over: Record<string, unknown> = {}) => ({
  personType: "Student",
  studentId: STUDENT_ID,
  personnelId: null,
  number: "2026-0001",
  name: "Maria Santos",
  cardUid: "25-01",
  ...over,
});

const personnelPerson = (over: Record<string, unknown> = {}) => ({
  personType: "Personnel",
  studentId: null,
  personnelId: PERSONNEL_ID,
  number: "E-0042",
  name: "Rosa Villanueva",
  cardUid: null,
  ...over,
});

const envelope = (items: unknown[], over: Record<string, unknown> = {}) => ({
  items,
  page: 1,
  pageSize: 50,
  total: items.length,
  hasMore: false,
  ...over,
});

let asked: URL[] = [];

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL) => {
    asked.push(new URL(String(input), "http://localhost"));
    return Promise.resolve(reply());
  });
}

afterEach(() => {
  asked = [];
  vi.unstubAllGlobals();
});

describe("api.listUntappedPage", () => {
  it("asks the event's untapped route for one searched page", async () => {
    serve(() => json(200, envelope([studentPerson()])));

    await api.listUntappedPage(EVENT_ID, { search: "  Santos " }, 1, 50);

    const [request] = asked;
    expect(request.pathname).toBe(`/api/v1/events/${EVENT_ID}/untapped`);
    // Trimmed on the way out: the server trims too, but a padded fragment is not what was meant.
    expect(request.searchParams.get("search")).toBe("Santos");
    expect(request.searchParams.get("page")).toBe("1");
    expect(request.searchParams.get("pageSize")).toBe("50");
    expect(request.searchParams.has("personType")).toBe(false);
  });

  it("sends no search at all when there is none, and passes personType through", async () => {
    serve(() => json(200, envelope([])));

    await api.listUntappedPage(EVENT_ID, { personType: "Personnel" }, 1, 50);

    expect(asked[0].searchParams.has("search")).toBe(false);
    expect(asked[0].searchParams.get("personType")).toBe("Personnel");
  });

  it("parses a student and a personnel row, nulls becoming absent, and keeps the server's total", async () => {
    serve(() => json(200, envelope([studentPerson(), personnelPerson()], { total: 120 })));

    const page = await api.listUntappedPage(EVENT_ID, {}, 1, 50);

    expect(page.total).toBe(120);
    expect(page.items).toEqual([
      {
        personType: "Student",
        studentId: STUDENT_ID,
        personnelId: undefined,
        number: "2026-0001",
        name: "Maria Santos",
        cardUid: "25-01",
      },
      {
        personType: "Personnel",
        studentId: undefined,
        personnelId: PERSONNEL_ID,
        number: "E-0042",
        name: "Rosa Villanueva",
        // A person with no active card parses as having none — not as an empty UID to offer.
        cardUid: undefined,
      },
    ]);
    expect(page).not.toHaveProperty("hasMore");
  });

  it.each([
    ["a missing name", () => studentPerson({ name: undefined }), "name"],
    ["a missing number", () => studentPerson({ number: undefined }), "number"],
    ["an unknown personType", () => studentPerson({ personType: "Visitor" }), "personType"],
    ["a missing personType", () => studentPerson({ personType: undefined }), "personType"],
    ["a cardUid of the wrong type", () => studentPerson({ cardUid: 2501 }), "cardUid"],
  ])("fails loud on %s", async (_label, row, field) => {
    serve(() => json(200, envelope([row()])));

    const error = await api.listUntappedPage(EVENT_ID, {}, 1, 50).then(
      () => undefined,
      (thrown: unknown) => thrown,
    );

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).kind).toBe("malformed");
    expect((error as ApiError).message).toContain(field);
  });

  it("fails loud on an envelope without a total", async () => {
    serve(() => json(200, { items: [], page: 1, pageSize: 50, hasMore: false }));

    await expect(api.listUntappedPage(EVENT_ID, {}, 1, 50)).rejects.toBeInstanceOf(ApiError);
  });
});
