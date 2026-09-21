/** @vitest-environment happy-dom */

// The "Download template" button on `/students/import` (Task 5, client QA #470 B1, #472 Q1/Q2) —
// `GET /sis/import/template` through the same seam as every other request, saved to disk via a
// temporary anchor.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// 1. **The download goes through `send`, not a bare `fetch`.** There is nothing in this file that
//    asserts the Bearer header directly — `apiSession.test.ts` already owns that — but the request is
//    made with the real `api.downloadRosterTemplate()`, on the real fetch stub, so a change that routed
//    this one call around `send` (skipping the 401-renewal-and-replay every other request gets) would
//    still show up as a request the stub can see and name.
// 2. **The filename comes from the server's `Content-Disposition`, not from a client-side guess** —
//    except when the header is missing, where a named fallback constant is used. Both are pinned, and
//    so is the *real* shape ASP.NET actually sends: a plain, unquoted `filename=` plus a trailing
//    `filename*=` (RFC 5987) this build deliberately ignores — not the quoted, `filename*`-less form a
//    hand-written example would guess at.
// 3. **The object URL is revoked, but not in the same tick as the click.** `click()` on an `<a
//    download>` only queues the save; Firefox and older Safari can fail it if the URL is revoked before
//    the browser has actually finished reading the blob, and the page would already be showing success
//    by then. The revoke is deferred behind a named constant instead — see `REVOKE_OBJECT_URL_DELAY_MS`.
// 4. **A second click while the first request is in flight starts no second request.** `useApiMutation`
//    is what buys this; this file exercises it end to end rather than trusting the hook's own suite.
// 5. **A failed body read is classified by what actually failed**, not by what `res.json()` would have
//    meant: `blob()` cannot be malformed the way JSON can, so a non-timeout failure is `network`/`read`
//    (safely retryable), and `malformed` is reserved for a 200 whose `Content-Type` is not the workbook
//    — the one case a retry provably cannot fix.
// 6. **A failure inside `saveBlob` itself is not silent.** It runs after the mutation has already
//    reported success, so it is a distinct failure the mutation's own state can never observe.
//
// happy-dom lacks `URL.createObjectURL`/`revokeObjectURL` (the same gap `useApiMutation.test.ts` and
// friends work around for other browser-only APIs), so both are stubbed. `HTMLAnchorElement.prototype
// .click` is stubbed too — not because happy-dom cannot dispatch it, but because a `blob:` href with no
// real backing object is not something a click should be allowed to attempt to navigate to; stubbing it
// is also what lets this file see and assert on the exact anchor `saveBlob` built.
//
// The transport is a `fetch` stub in the idiom `eventCertificates.test.tsx` and
// `studentsImportProgress.test.tsx` both use, rather than a mocked `api` module: `api.ts` is the single
// seam per `CLAUDE.md`, and mocking it out would test a component's wiring to a fake instead of the
// real request this feature depends on.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import StudentsImport from "../src/pages/StudentsImport";
import { ApiError, api } from "../src/api";
import { advise } from "../src/apiGuidance";
import { beginSession, resetSessionForTests } from "../src/authSession";
import { PERMISSIONS } from "../src/permissions";
import { REVOKE_OBJECT_URL_DELAY_MS } from "../src/sisImport";
import type { AuthUser } from "../src/types";

const OPERATOR: AuthUser = {
  id: "11111111-1111-1111-1111-111111111111",
  schoolId: "22222222-2222-2222-2222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions: [PERMISSIONS.sisImport],
};

/**
 * The one term the terms picker needs to render past its loading state. Its content is irrelevant to
 * every test here — the download button renders whether or not a term is chosen — but the terms read
 * still has to be served, or the stub throws on an unhandled route and every test fails on that instead
 * of on what it means to test.
 */
const TERM_JSON = {
  id: "44444444-4444-4444-4444-444444444444",
  code: "2026-1",
  schoolYear: "2026",
  semester: "1st",
  isCurrent: true,
  startsOn: null,
  endsOn: null,
};

const TEMPLATE_PATH = "/api/v1/sis/import/template";
const TERMS_PATH = "/api/v1/academic/terms";

const jsonResponse = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const problemResponse = (status: number, detail: string) =>
  new Response(
    JSON.stringify({
      type: "about:blank",
      title: "The request could not be processed.",
      status,
      detail,
      traceId: "trace-1",
    }),
    { status, headers: { "Content-Type": "application/problem+json" } },
  );

const XLSX_CONTENT_TYPE = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

/** A `Response` shaped like the real `SisImportController.Template` answers, with a real body. */
const templateOk = (filename: string | undefined) =>
  new Response(new Blob([new Uint8Array([1, 2, 3])]), {
    status: 200,
    headers: {
      "Content-Type": XLSX_CONTENT_TYPE,
      ...(filename === undefined ? {} : { "Content-Disposition": `attachment; filename="${filename}"` }),
    },
  });

/** Every request this stub actually needs to answer, and nothing else. */
let templateReply: () => Response | Promise<Response>;
let fetchSpy: ReturnType<typeof vi.fn>;

function serve() {
  fetchSpy = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const path = new URL(url, "http://localhost").pathname;

    if (path === TERMS_PATH) {
      return Promise.resolve(
        jsonResponse(200, { items: [TERM_JSON], page: 1, pageSize: 200, total: 1, hasMore: false }),
      );
    }
    if (path === TEMPLATE_PATH) {
      expect(init?.method ?? "GET").toBe("GET");
      return Promise.resolve(templateReply());
    }
    throw new Error(`the import page asked for something this stub does not serve: ${url}`);
  });
  vi.stubGlobal("fetch", fetchSpy);
}

// ---------------------------------------------------------------------------------------------
// The browser-only APIs `saveBlob` needs and happy-dom does not provide
// ---------------------------------------------------------------------------------------------

let createObjectURL: ReturnType<typeof vi.fn>;
let revokeObjectURL: ReturnType<typeof vi.fn>;
let anchorClick: ReturnType<typeof vi.spyOn>;
/** `document.body.appendChild`, spied rather than replaced — it still runs, so `saveBlob`'s anchor
 * really lands in the DOM and can be inspected afterward. This is how the anchor `saveBlob` built is
 * captured, without aliasing `this` out of the stubbed `click()` below. */
let appendChildSpy: ReturnType<typeof vi.spyOn>;

/** The anchor `saveBlob` most recently appended, if any. */
const clickedAnchor = (): HTMLAnchorElement | undefined =>
  appendChildSpy.mock.calls.at(-1)?.[0] as HTMLAnchorElement | undefined;

const MOCK_OBJECT_URL = "blob:mock-url";

function stubDownloadPlumbing() {
  createObjectURL = vi.fn(() => MOCK_OBJECT_URL);
  revokeObjectURL = vi.fn();
  (URL as unknown as { createObjectURL: typeof createObjectURL }).createObjectURL = createObjectURL;
  (URL as unknown as { revokeObjectURL: typeof revokeObjectURL }).revokeObjectURL = revokeObjectURL;

  appendChildSpy = vi.spyOn(document.body, "appendChild");
  // Never actually dispatched: a `blob:` href with no real backing object is not something a click
  // should be allowed to attempt to navigate to.
  anchorClick = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/students/import"]}>
      <Routes>
        <Route path="/students/import" element={<StudentsImport />} />
      </Routes>
    </MemoryRouter>,
  );
}

/** Renders and flushes the terms read every test starts from. */
async function show() {
  const rendered = renderPage();
  await act(async () => {});
  return rendered;
}

const pageText = () => document.body.textContent ?? "";

const downloadButton = () => screen.getByRole("button", { name: /download template/i });

beforeEach(() => {
  resetSessionForTests();
  beginSession("access-token-1", OPERATOR);
  templateReply = () => templateOk("roster-template-2026.xlsx");
  stubDownloadPlumbing();
  serve();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  // Unconditional rather than only after the one test that enables fake timers: leaving them fake on a
  // test that forgot to restore would silently break every timer-driven thing (the deferred revoke
  // included) in whichever test happened to run next.
  vi.useRealTimers();
  resetSessionForTests();
});

describe("the Download template button", () => {
  it("is present on the import page", async () => {
    await show();
    expect(downloadButton()).toBeTruthy();
  });

  it("has an accessible name", async () => {
    await show();
    // getByRole with the accessible-name matcher IS the assertion — a button with no name, or one
    // whose name is carried only by adjacent text, would already have failed the query above.
    expect(downloadButton().textContent).toMatch(/download template/i);
  });

  it("shows a hint that the workbook has an Instructions sheet", async () => {
    await show();
    expect(pageText()).toMatch(/instructions sheet/i);
  });
});

describe("pressing it", () => {
  it("requests GET /sis/import/template and saves the file the server named", async () => {
    templateReply = () => templateOk("roster-template-2026.xlsx");
    await show();

    fireEvent.click(downloadButton());
    await act(async () => {});

    const templateCalls = fetchSpy.mock.calls.filter(
      ([input]) => new URL(String(input), "http://localhost").pathname === TEMPLATE_PATH,
    );
    expect(templateCalls).toHaveLength(1);

    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(clickedAnchor()).toBeDefined();
    expect(clickedAnchor()?.download).toBe("roster-template-2026.xlsx");
    expect(clickedAnchor()?.href).toContain(MOCK_OBJECT_URL);
  });

  it("falls back to the default filename when Content-Disposition is missing", async () => {
    templateReply = () => templateOk(undefined);
    await show();

    fireEvent.click(downloadButton());
    await act(async () => {});

    // Mirrors `ROSTER_TEMPLATE_FALLBACK_FILENAME` in `src/api.ts`, which mirrors
    // `SisImportController.TemplateFileName` in turn — hand-verified against the controller on
    // 2026-09-21, same tripwire idiom as `sisImport.test.ts`'s `SERVER_MAX_UPLOAD_BYTES`.
    expect(clickedAnchor()?.download).toBe("EAMS-roster-template.xlsx");
  });

  it("defers the object URL revoke — not in the same tick as the click, only after the delay", async () => {
    // W1: `click()` only queues the save; revoking synchronously can lose the download in Firefox and
    // older Safari, silently, with the page already showing success. Real timers would make "not yet
    // revoked" a race against however long the test runner takes to reach the assertion, so this pins
    // it deterministically instead: fake timers, and the assertion is made with the clock advanced by
    // exactly nothing before it and by `REVOKE_OBJECT_URL_DELAY_MS` after.
    vi.useFakeTimers();
    templateReply = () => templateOk("roster-template-2026.xlsx");
    await show();

    fireEvent.click(downloadButton());
    await act(async () => {});

    expect(anchorClick).toHaveBeenCalledTimes(1);
    expect(revokeObjectURL).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(REVOKE_OBJECT_URL_DELAY_MS);
    });

    expect(revokeObjectURL).toHaveBeenCalledWith(MOCK_OBJECT_URL);
  });

  it("starts only one download on a double click", async () => {
    let settle: ((response: Response) => void) | undefined;
    templateReply = () =>
      new Promise<Response>((resolve) => {
        settle = resolve;
      });
    await show();

    // Queried once and reused: the button's own accessible name changes to "Downloading…" the moment
    // the first click lands, so re-querying by the idle name for the second and third clicks would be
    // testing that `getByRole` fails rather than that a second request was refused.
    const button = downloadButton();
    fireEvent.click(button);
    fireEvent.click(button);
    fireEvent.click(button);

    settle?.(templateOk("roster-template-2026.xlsx"));
    await act(async () => {});

    const templateCalls = fetchSpy.mock.calls.filter(
      ([input]) => new URL(String(input), "http://localhost").pathname === TEMPLATE_PATH,
    );
    expect(templateCalls).toHaveLength(1);
    expect(createObjectURL).toHaveBeenCalledTimes(1);
  });

  it("shows a busy state while downloading, and reverts once it settles", async () => {
    let settle: ((response: Response) => void) | undefined;
    templateReply = () =>
      new Promise<Response>((resolve) => {
        settle = resolve;
      });
    await show();

    fireEvent.click(downloadButton());

    const busy = screen.getByRole("button", { name: /downloading/i }) as HTMLButtonElement;
    expect(busy.disabled).toBe(true);

    settle?.(templateOk("roster-template-2026.xlsx"));
    await act(async () => {});

    const idle = screen.getByRole("button", { name: /^download template$/i }) as HTMLButtonElement;
    expect(idle.disabled).toBe(false);
  });

  it("renders the server's message on a failure, and never swallows it", async () => {
    templateReply = () =>
      problemResponse(409, "No school could be resolved for this account.");
    await show();

    fireEvent.click(downloadButton());
    await act(async () => {});

    expect(pageText()).toMatch(/No school could be resolved for this account\./);
    // Not saved: a failed request must not reach `saveBlob` at all.
    expect(createObjectURL).not.toHaveBeenCalled();
  });

  it("a failure while saving the file shows an error", async () => {
    // N3: the request already succeeded by this point — `templateDownload.status` is `"succeeded"` —
    // so a throw out of `saveBlob` is a failure the mutation's own state never sees. Forced here by
    // making the very first thing `saveBlob` calls, `URL.createObjectURL`, throw; left uncaught, this
    // used to reject the `void`ed promise in `downloadTemplate` with nothing on screen saying why the
    // button just went idle.
    templateReply = () => templateOk("roster-template-2026.xlsx");
    createObjectURL.mockImplementation(() => {
      throw new Error("createObjectURL is blocked by the browser's storage settings");
    });
    await show();

    fireEvent.click(downloadButton());
    await act(async () => {});

    expect(pageText()).toMatch(/createObjectURL is blocked by the browser's storage settings/);
    // The request happened — this is not a network failure being mis-blamed on the save.
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(anchorClick).not.toHaveBeenCalled();
  });

  // Negative control for "starts only one download on a double click", confirmed by hand.
  //
  // Two guards cover the double click independently and either one alone still leaves the test green:
  // the button's own `disabled={downloadingTemplate}` (a disabled native button dispatches no click at
  // all, once React has committed the first click's state) and `useApiMutation`'s own `inFlight` ref.
  // Removing only one at a time proved that — the test stayed green both times. Only removing BOTH at
  // once (temporarily: `disabled={downloadingTemplate}` deleted from the button in
  // `src/pages/StudentsImport.tsx`, and the early `if (inFlight.current) return { outcome: "ignored" }`
  // deleted from `run` in `src/useApiMutation.ts`) made this test fail, with 3 template requests
  // recorded instead of 1 — for exactly the reason the test names: all three clicks landed and none of
  // them was refused. Both edits were reverted with the Edit tool immediately afterward and this file's
  // full suite was re-run green, which is the run this comment describes.
});

// ---------------------------------------------------------------------------------------------
// `api.downloadRosterTemplate()` directly — W2's failure classification and W3's header shape
// ---------------------------------------------------------------------------------------------
//
// These call the seam function itself rather than clicking through the page: what is being pinned is
// `ApiError.kind`/`shape` and the exact bytes of `Content-Disposition` this build reads, neither of
// which the rendered page exposes any more precisely than "an error alert appeared" — already covered
// above. Still goes through the same fetch stub and the same session as every other test in this file.

describe("downloadRosterTemplate — failure classification (W2)", () => {
  it("a dropped connection mid-download advises a retry", async () => {
    // `res.blob()` cannot fail on shape the way `res.json()` can — there is no structure to be
    // malformed. Simulated here by overriding a real `Response`'s own `blob()` to reject, which is the
    // one way this can actually fail apart from a timeout: the connection dropping mid-transfer.
    templateReply = () => {
      const res = new Response(new Blob([new Uint8Array([1, 2, 3])]), {
        status: 200,
        headers: { "Content-Type": XLSX_CONTENT_TYPE },
      });
      res.blob = () => Promise.reject(new Error("the connection was reset"));
      return res;
    };

    const error: unknown = await api.downloadRosterTemplate().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).kind).toBe("network");
    expect((error as ApiError).shape).toBe("read");
    // The point of classifying it this way rather than `malformed`: `advise()` reads `network` + a
    // read as freely retryable, where `malformed`'s advice ("this build and the API are different
    // versions, retrying will not help") would be actively wrong for a connection that merely dropped.
    expect(advise(error).retryable).toBe("safe");
  });

  it("a 200 that is not an xlsx is refused, not saved", async () => {
    // The shape a misconfigured proxy or a load balancer's own error page produces: a 200 that never
    // reached the controller at all, with an HTML body where the workbook should be.
    templateReply = () =>
      new Response("<html><body>Bad Gateway</body></html>", {
        status: 200,
        headers: { "Content-Type": "text/html; charset=utf-8" },
      });

    const error: unknown = await api.downloadRosterTemplate().catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).kind).toBe("malformed");
    expect((error as ApiError).shape).toBe("read");
    expect((error as ApiError).message).toContain("text/html");
  });

  it("accepts a 200 whose Content-Type carries extra parameters, as long as it is the xlsx type", async () => {
    // The negative control for the test above: refusing on `startsWith` rather than exact equality is
    // what lets a server that adds e.g. a charset parameter keep working, so this pins that a workbook
    // reply is not accidentally caught by the same guard.
    templateReply = () =>
      new Response(new Blob([new Uint8Array([1, 2, 3])]), {
        status: 200,
        headers: { "Content-Type": `${XLSX_CONTENT_TYPE}; charset=binary` },
      });

    await expect(api.downloadRosterTemplate()).resolves.toMatchObject({
      filename: "EAMS-roster-template.xlsx",
    });
  });
});

describe("downloadRosterTemplate — the real Content-Disposition shape (W3)", () => {
  it("reads an unquoted filename alongside a trailing filename*", async () => {
    // The actual shape ASP.NET's `ContentDispositionHeaderValue` writes for a name that is already a
    // valid HTTP token — unquoted, with the RFC 5987 encoded form trailing it — not the quoted,
    // `filename*`-less form a hand-written test fixture would guess at.
    templateReply = () =>
      new Response(new Blob([new Uint8Array([1, 2, 3])]), {
        status: 200,
        headers: {
          "Content-Type": XLSX_CONTENT_TYPE,
          "Content-Disposition":
            "attachment; filename=EAMS-roster-template.xlsx; " +
            "filename*=UTF-8''EAMS-roster-template.xlsx",
        },
      });

    const file = await api.downloadRosterTemplate();
    expect(file.filename).toBe("EAMS-roster-template.xlsx");
  });

  it("reads the same name when filename* is placed first", async () => {
    // Ignoring `filename*` is deliberate (the server's name is a fixed ASCII constant, so the two can
    // never disagree) — but that only holds if the parse is not accidentally order-dependent.
    templateReply = () =>
      new Response(new Blob([new Uint8Array([1, 2, 3])]), {
        status: 200,
        headers: {
          "Content-Type": XLSX_CONTENT_TYPE,
          "Content-Disposition":
            "attachment; filename*=UTF-8''EAMS-roster-template.xlsx; " +
            "filename=EAMS-roster-template.xlsx",
        },
      });

    const file = await api.downloadRosterTemplate();
    expect(file.filename).toBe("EAMS-roster-template.xlsx");
  });
});
