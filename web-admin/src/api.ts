// The single seam to the EAMS .NET API (`EAMS.Api`, base path `/api/v1`). Components import `api`
// and nothing reaches around it.
//
// Shapes are pinned to the committed contract at `docs/api/openapi.json` — camelCase JSON, RFC 7807
// `application/problem+json` on every error. Responses are narrowed field by field on the way in:
// this is a system boundary, so a payload that drifts from the contract fails loud here instead of
// arriving three components deep as `undefined`.

import type {
  Student,
  Card,
  EventItem,
  EventStatusName,
  EventWriteRequest,
  AttendanceRecord,
  EventSummary,
} from "./types";

// The URL `dotnet run --project backend/EAMS.Api --urls "http://localhost:5080"` serves. Overridden
// per-machine by VITE_API_BASE_URL (see `.env.example`) — the capture device's host is DHCP.
const DEFAULT_BASE_URL = "http://localhost:5080/api/v1";

const baseUrl = (import.meta.env.VITE_API_BASE_URL ?? DEFAULT_BASE_URL).replace(/\/+$/, "");

// ---------------------------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------------------------

/**
 * `network` — never reached the server. `http` — server refused. `malformed` — reply off-contract.
 * `too-large` — the list is real and well-formed but holds more rows than this seam will load; see
 * `MAX_LIST_ROWS`. It is a client-side ceiling, not a server fault, and retrying cannot clear it.
 */
export type ApiErrorKind = "network" | "http" | "malformed" | "too-large";

/**
 * Which half of the API a failed request came from — and the fact that lets `advise()` tell "press it
 * again" apart from "pressing it again may create a second one".
 *
 * A read that failed changed nothing, so retrying it is free. A write that failed **may already have
 * been applied**: a connection reset after the request bytes went out, a 500 raised after the row
 * committed, a gateway 429 in front of an origin that handled it. `POST /events` carries no
 * client-supplied idempotency key the way the tap flow does with `deviceTapId`, so sending it again
 * is how a user ends up with two events.
 *
 * It is recorded here, where `send` and `writeJson` already know it, rather than supplied by whoever
 * renders the error. A fact the call site must remember is the same defect class `useApiResource`'s
 * `deps` argument exists to close, and this one is worse: forgetting it produces a UI that
 * confidently invites the duplicate.
 */
export type ApiRequestShape = "read" | "write";

/** RFC 7807 body as this API emits it. `title`/`detail` are prose and are reworded freely. */
export interface ApiProblem {
  status: number;
  type?: string;
  title?: string;
  detail?: string;
  instance?: string;
  /** The correlation handle. On every error body this API produces. Quote it when reporting. */
  traceId?: string;
  /**
   * The stable machine-readable outcome token — **branch on this and on `status`, never on
   * `title`/`detail`**. Present on the attendance, students and devices surfaces and on auth /
   * rate-limit refusals; absent on the events write surface.
   */
  code?: string;
  /** Server UTC clock. Present on tap and manual-override failures. */
  serverTime?: string;
}

/**
 * The tail of `ApiError`'s constructor, as a named bag rather than four positional arguments nobody
 * can read at the call site.
 *
 * `shape` is **required and never defaulted**. A default would be the quiet failure: a throw site
 * added later would inherit "read", and the first thing anyone would notice is a Retry button on a
 * write that had already been applied.
 */
export interface ApiErrorInit {
  shape: ApiRequestShape;
  problem?: ApiProblem;
  cause?: unknown;
}

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  /** HTTP status, or 0 when the failure happened before/outside a response. */
  readonly status: number;
  /** Whether the request that failed could have changed server state. See `ApiRequestShape`. */
  readonly shape: ApiRequestShape;
  readonly code: string | undefined;
  readonly traceId: string | undefined;
  readonly problem: ApiProblem | undefined;

  constructor(kind: ApiErrorKind, status: number, message: string, init: ApiErrorInit) {
    super(message, { cause: init.cause });
    this.name = "ApiError";
    this.kind = kind;
    this.status = status;
    this.shape = init.shape;
    this.code = init.problem?.code;
    this.traceId = init.problem?.traceId;
    this.problem = init.problem;
  }
}

/** Turns anything thrown by this module into one truthful line a component can render. */
export function describeApiError(error: unknown): string {
  if (error instanceof ApiError) {
    return error.traceId ? `${error.message} (traceId ${error.traceId})` : error.message;
  }
  if (error instanceof Error) return error.message;
  return "Unexpected error.";
}

// ---------------------------------------------------------------------------------------------
// Narrowing helpers — the whole boundary check lives here, so no cast hides contract drift
// ---------------------------------------------------------------------------------------------

type Row = Record<string, unknown>;

const isRow = (value: unknown): value is Row =>
  typeof value === "object" && value !== null && !Array.isArray(value);

/**
 * `shape: "read"` because the narrowing helpers below run over a *reply body* and know nothing about
 * the request that fetched it. A mapper cannot know whether the server acted, and guessing "read"
 * here would be the one guess that produces a duplicate — so on a write it must never be the answer
 * that escapes.
 *
 * It cannot be, and that is now structural rather than remembered. **A write's reply is only ever
 * mapped inside `writeJson`**, which takes the mapper as a required parameter and runs it inside its
 * own catch, re-raising as `shape: "write"`. `writeJson` never hands a caller an unmapped body, so no
 * route this module offers reaches these helpers from a write except through that catch — the
 * guarantee is a signature rather than a convention, which is what a `PUT` added a slice later needs
 * it to be.
 *
 * Not unforgeable, and worth saying so rather than over-claiming: `send` is still callable directly
 * from inside this file with a body, and a future write that narrowed its own response by hand would
 * re-open the hole. The signature removes the way anyone would reach for by default; it does not
 * remove every way.
 */
const offContract = (what: string, detail: string) =>
  new ApiError("malformed", 0, `${what} is off-contract: ${detail}.`, { shape: "read" });

function asRow(value: unknown, what: string): Row {
  if (!isRow(value)) throw offContract(what, `expected an object, got ${describeType(value)}`);
  return value;
}

function asRows(value: unknown, what: string): Row[] {
  if (!Array.isArray(value)) throw offContract(what, `expected an array, got ${describeType(value)}`);
  return value.map((item, i) => asRow(item, `${what}[${i}]`));
}

const describeType = (value: unknown) => (value === null ? "null" : typeof value);

function reqStr(row: Row, key: string, what: string): string {
  const value = row[key];
  if (typeof value !== "string") {
    throw offContract(what, `\`${key}\` should be a string, got ${describeType(value)}`);
  }
  return value;
}

function reqNum(row: Row, key: string, what: string): number {
  const value = row[key];
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw offContract(what, `\`${key}\` should be a finite number, got ${describeType(value)}`);
  }
  return value;
}

function reqBool(row: Row, key: string, what: string): boolean {
  const value = row[key];
  if (typeof value !== "boolean") {
    throw offContract(what, `\`${key}\` should be a boolean, got ${describeType(value)}`);
  }
  return value;
}

/** Nullable-in-contract fields: JSON `null` and an absent key both collapse to `undefined`. */
const optStr = (value: unknown): string | undefined =>
  typeof value === "string" ? value : undefined;

// ---------------------------------------------------------------------------------------------
// Transport
// ---------------------------------------------------------------------------------------------

function buildUrl(path: string, query?: Record<string, string | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== "") params.set(key, value);
  }
  const qs = params.toString();
  return `${baseUrl}${path}${qs ? `?${qs}` : ""}`;
}

async function readProblem(res: Response): Promise<ApiProblem> {
  let body: unknown;
  try {
    body = await res.json();
  } catch {
    // A non-JSON error body (proxy HTML, an empty 502) is itself information, but it is not fatal
    // and it must not mask the status. Nothing is swallowed: the caller still gets an ApiError
    // built from `res.status` below.
    body = undefined;
  }
  const row = isRow(body) ? body : {};
  return {
    status: res.status,
    type: optStr(row.type),
    title: optStr(row.title),
    detail: optStr(row.detail),
    instance: optStr(row.instance),
    traceId: optStr(row.traceId),
    code: optStr(row.code),
    serverTime: optStr(row.serverTime),
  };
}

/**
 * The sentence a user reads — which is not the token a program branches on, and the two stop
 * competing for this one slot.
 *
 * `detail`, then `title`, then `code`. They are not three spellings of one thing. `code` is the
 * stable machine outcome token and it is already carried separately on `ApiError.code`, where
 * `advise()` and any future branch can reach it without going through prose; putting it first here as
 * well meant that on every surface that emits one, the token won and the sentence was thrown away.
 * `StudentsController.Problem` sets `Extensions["code"]` on *every* failure including validation
 * ones, and `/devices` does the same, so that was not an edge case — it was the students write
 * surface's ordinary 400, reaching the screen as `PUT /students/{id} failed (400: ValidationFailed).`
 * while the sentence naming the field and the limit sat unused in `detail`.
 *
 * `title` stays second: the events write surface emits no `code` at all and titles every §4.5 refusal
 * "The request could not be processed.", so `detail` has to outrank `title` there too.
 *
 * `code` last rather than dropped: where a server sends neither `detail` nor `title` the token still
 * beats a shrug.
 *
 * This string is display-only. Nothing in `src/` branches on it — `advise()` reads `kind`/`status`,
 * and the token stays on `ApiError.code` — so re-ordering it changes what is read, not what is done.
 */
function httpError(what: string, problem: ApiProblem, shape: ApiRequestShape): ApiError {
  const reason = problem.detail ?? problem.title ?? problem.code ?? "no detail supplied";
  return new ApiError("http", problem.status, `${what} failed (${problem.status}: ${reason}).`, {
    shape,
    problem,
  });
}

/**
 * How long one request may go unanswered before this seam calls it a failure.
 *
 * `fetch` has none of its own. A server that accepts the connection and then never answers — a
 * deadlocked connection pool is the realistic way that happens — leaves a promise that neither
 * resolves nor rejects, so `useApiResource` stays in `loading` for as long as the tab is open: the
 * page says "Loading the event…" forever, with no error state and no Retry. An unbounded wait is not
 * a safer default than a wrong one; it is the one failure the user cannot act on.
 *
 * Sized for the slowest read this SPA makes rather than for a single hop: `listAll` walks pages
 * sequentially and each page gets its own budget, so this bounds one request, not the whole walk.
 */
const REQUEST_TIMEOUT_MS = 15_000;

/**
 * `AbortSignal.timeout` rejects with `TimeoutError`; a caller-supplied `AbortController` rejects with
 * `AbortError`. Matching the name rather than abort-ness keeps those two apart — nothing in this SPA
 * cancels a request today, and if something ever does, "we gave up waiting" must not be the sentence
 * a user is shown for "you navigated away".
 */
const TIMEOUT_ERROR_NAME = "TimeoutError";

const isTimeout = (cause: unknown): boolean =>
  cause instanceof Error && cause.name === TIMEOUT_ERROR_NAME;

/**
 * `network` rather than a kind of its own: `advise()` in `apiGuidance` reads that kind together with
 * `shape`, and on a read a timeout is precisely the case where offering Retry is honest — the request
 * may well succeed on the next try, and nothing about the client needs to change first. On a write it
 * is the case where offering Retry is a duplicate, and `shape` is what carries that through.
 */
const timedOut = (what: string, cause: unknown, shape: ApiRequestShape) =>
  new ApiError(
    "network",
    0,
    `The EAMS API at ${baseUrl} did not finish answering ${what} within ${REQUEST_TIMEOUT_MS} ms.` +
      // Giving up waiting says nothing about what the server did with the request it already has. On
      // a read that is immaterial; on a write it is the only thing the user needs to know.
      (shape === "write"
        ? " It may still have been carried out — check the list before sending it again."
        : ""),
    { shape, cause },
  );

/** What this seam sends and accepts. RFC 7807 error bodies arrive as `application/problem+json`. */
const JSON_MEDIA_TYPE = "application/json";

/**
 * The write half of a request, absent on every read.
 *
 * A discrete argument rather than a spread `RequestInit`: the two headers and the serialisation are
 * the whole difference between a read and a write here, and letting callers hand `fetch` arbitrary
 * options would put the timeout signal — the thing that stops a stalled server hanging a screen
 * forever — one careless `signal:` away from being overwritten.
 *
 * A union rather than one shape with an optional `payload`, so **`DELETE` cannot carry a body and
 * `POST`/`PUT`/`PATCH` cannot omit one**. `DELETE /events/{id}` has nothing to send, and a request
 * that sets `Content-Type: application/json` with no body is a shape some proxies and gateways treat
 * as malformed — worth making unrepresentable rather than remembering not to write.
 */
type RequestBody =
  | { method: "POST" | "PUT" | "PATCH"; payload: unknown }
  | { method: "DELETE" };

/** The serialised body, or nothing — the one place that decides whether this request has one. */
const payloadOf = (write: RequestBody | undefined): string | undefined =>
  write === undefined || write.method === "DELETE" ? undefined : JSON.stringify(write.payload);

async function send(
  what: string,
  path: string,
  query?: Record<string, string | undefined>,
  write?: RequestBody,
) {
  // Derived once, here, from the method — which is what actually decides it. Every `ApiError` this
  // request can produce carries the answer, so no error-rendering surface has to work it out again.
  //
  // From the *method* and not from `write === undefined`, which is the same answer today and the
  // wrong rule. Body-presence is a proxy: it holds only while every write carries a payload, and
  // `DELETE /events/{id}` — the next verb this seam gains — carries none. A body-less write derived
  // that way would be labelled `"read"`, and a connection reset on it would tell the user it "was not
  // sent" over a row that may well be gone, with a live Retry beside it. That is exactly the defect
  // this field was added to close, so the rule has to be exact rather than currently-true.
  const method = write?.method ?? "GET";
  const shape: ApiRequestShape = method === "GET" ? "read" : "write";
  // From the body, because `Content-Type` describes a body — a bodyless `DELETE` declaring a JSON
  // content type is describing something it did not send. `shape` above is deliberately NOT derived
  // this way; see the comment there.
  const body = payloadOf(write);
  try {
    return await fetch(buildUrl(path, query), {
      method,
      headers:
        body === undefined
          ? { Accept: JSON_MEDIA_TYPE }
          : { Accept: JSON_MEDIA_TYPE, "Content-Type": JSON_MEDIA_TYPE },
      body,
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch (cause) {
    if (isTimeout(cause)) throw timedOut(what, cause, shape);
    throw new ApiError(
      "network",
      0,
      // "Was not sent" is a claim this seam can make about a read and cannot make about a write. A
      // `fetch` rejection covers a connection that never opened *and* one reset after the request
      // bytes went out, and for a non-idempotent POST those two are opposite facts: the second one
      // may have created the event. Saying so is the difference between a user pressing Create again
      // and a user checking the list first.
      shape === "read"
        ? `Cannot reach the EAMS API at ${baseUrl} — ${what} was not sent.`
        : `Cannot reach the EAMS API at ${baseUrl} — ${what} may or may not have been carried out. ` +
          `Check the list before sending it again.`,
      { shape, cause },
    );
  }
}

async function parseBody(res: Response, what: string, shape: ApiRequestShape): Promise<unknown> {
  try {
    return await res.json();
  } catch (cause) {
    // The timeout signal stays attached to the response body, so a server that writes its headers and
    // then stalls mid-stream lands here rather than in `send`. It is still a timeout, and calling it
    // `malformed` would tell the user their build and the API are on different versions and that
    // retrying will not help — both false, and the second one strands them.
    if (isTimeout(cause)) throw timedOut(what, cause, shape);
    // `shape` travels with it: on a write, a 2xx whose body will not parse is the server having
    // already acted and this build being unable to read what it did.
    throw new ApiError("malformed", res.status, `${what} returned a body that is not JSON.`, {
      shape,
      cause,
    });
  }
}

async function getJson(what: string, path: string, query?: Record<string, string | undefined>) {
  const res = await send(what, path, query);
  if (!res.ok) throw httpError(what, await readProblem(res), "read");
  return parseBody(res, what, "read");
}

/**
 * As `getJson`, but a 404 means "no such row" rather than a failure — the three lookup methods are
 * typed `T | undefined` and the pages already render a loading/empty state for it. Every other
 * non-2xx still throws.
 */
async function getJsonOrMissing(
  what: string,
  path: string,
): Promise<unknown | undefined> {
  const res = await send(what, path);
  if (res.status === 404) return undefined;
  if (!res.ok) throw httpError(what, await readProblem(res), "read");
  return parseBody(res, what, "read");
}

/**
 * A write, and its reply, **narrowed here rather than by the caller**.
 *
 * Every non-2xx becomes an `ApiError` through the same `readProblem`/`httpError` pair the reads use,
 * so the write surface's 400 and 409 land in the taxonomy `advise()` already reasons about rather
 * than in a parallel one of their own. The contract answers 201 for a create and 200 for an update;
 * any 2xx carrying a well-formed body is accepted, because refusing a 200 that contained exactly the
 * right DTO would fail the user over a number that changes nothing they can see or act on.
 *
 * **`map` is a required parameter, and that is the whole design.** It used to be the caller's job:
 * `postJson` returned `unknown`, `createEvent` narrowed it and caught the mapper's `offContract`
 * error to re-raise it with `shape: "write"`. That worked, and it worked by the author of the next
 * write remembering to do the same — with nothing to fail if they did not. The failure it invites is
 * specific: a `PUT` whose reply will not narrow raises `shape: "read"`, so `advise()` reports the
 * server as having changed nothing, the form heads its alert "was not saved" over a row that was, and
 * offers a live Retry beside it. Taking the mapper here means a write's reply is mapped inside this
 * function's catch or not at all, because this function is the only way to reach a write's body and
 * it never returns one unmapped.
 *
 * @param applied what the server did, in the user's words ("The event was created") — used only for
 *   the reply-unreadable message, where naming it is the difference between the user checking the
 *   list and the user sending it again.
 */
async function writeJson<T>(
  what: string,
  path: string,
  write: RequestBody,
  applied: string,
  map: (row: Row, what: string) => T,
): Promise<T> {
  const res = await send(what, path, undefined, write);
  if (!res.ok) throw httpError(what, await readProblem(res), "write");
  const body = await parseBody(res, what, "write");
  try {
    return map(asRow(body, what), what);
  } catch (cause) {
    // The reply is narrowed with the same mapper the list read uses, so a write cannot be the one
    // place a drifting contract slips through. What that costs is a failure mode a read does not
    // have — the server acted and this build cannot read back what it did — so it is caught and
    // renamed rather than reported as "it was not saved", which is the one reading that would have
    // the user send it a second time.
    //
    // `malformed` is the honest kind and also the useful one: `advise()` reads it as not retryable,
    // so no UI offers a Retry that could duplicate. `shape: "write"` is the other half — it is what
    // makes `advise()` report the server's effect as *unknown*, which is what a form's heading is
    // derived from.
    throw new ApiError(
      "malformed",
      0,
      `${applied}, but ${what} answered off-contract and this build cannot read it back: ` +
        `${describeApiError(cause)} Reload to confirm, and do not send it again.`,
      { shape: "write", cause },
    );
  }
}

/**
 * A write with nothing to read back — `DELETE /events/{id}`, which answers **204 No Content**.
 *
 * It deliberately never calls `parseBody`. A 204 has an empty body, `res.json()` on one rejects, and
 * routing that through the shared parser would report a perfectly successful delete as "returned a
 * body that is not JSON": a `malformed` failure invented out of the contract being honoured. Any 2xx
 * is accepted and whatever it carried is ignored, for the same reason `writeJson` accepts any 2xx —
 * the status number is not something the user can see or act on.
 *
 * Failures still go through `httpError` with `shape: "write"`, which is exactly why `send` derives
 * that from the method: a `DELETE` carries no body, so a body-presence rule would have labelled this
 * a read and told the user their delete "was not sent" over a row that may well be gone.
 */
async function writeNoContent(what: string, path: string, write: RequestBody): Promise<void> {
  const res = await send(what, path, undefined, write);
  if (!res.ok) throw httpError(what, await readProblem(res), "write");
}

// ---------------------------------------------------------------------------------------------
// Paging — the envelope stops here
// ---------------------------------------------------------------------------------------------
//
// Every §6.2/§6.3 admin list now answers with `<Dto>PagedResult` instead of a bare array:
// `{ items, page, pageSize, total, hasMore }`, `?page=` 1-based, `?pageSize=` defaulting to 50.
// This seam unwraps it and keeps handing components plain arrays (see the per-method notes), so a
// page of 50 must not be mistaken for the whole list: against the dev database `GET /students`
// answers `total: 61, hasMore: true` with 50 rows, and taking that first page as the answer would
// drop eleven students with nothing on screen saying so.
//
// `GET /attendance/live/{eventId}` is deliberately NOT in here. It pages by cursor
// (`since`/`cursor`/`hasMore`/`pollAfterSeconds`) as frozen published contract, its `hasMore` answers
// a different question, and routing it through `listAll` would be wrong twice over. It has no caller
// in this SPA today; if one is added it gets its own reader.

/** One page as it arrives. Internal: no component sees this type. */
interface PageOf<T> {
  items: T[];
  /** The page actually served — echoed back, so a clamp is visible rather than silent. */
  page: number;
  pageSize: number;
  total: number;
  hasMore: boolean;
}

/**
 * The ceiling that matters: how many rows this SPA will hold in memory and hand to a client-side
 * grid. Read from `total` on the first page, so one request settles it.
 *
 * It is expressed in rows because rows are what the limit is actually about. An earlier version
 * bounded *requests* instead and claimed that avoided restating the server's page size — it did not,
 * it hid it: a 40-request bound means 40 × whatever `Paging.DefaultPageSize` happens to be, so a
 * server-side change from 50 to 20 would have quietly cut this client's protection from 2000 rows to
 * 800 with no code change and no failing test. A row count means the same thing whatever the server
 * pages by. Crossing it is a design signal — that list has outgrown load-everything and needs
 * server-side paging — so it fails loud instead of truncating.
 */
const MAX_LIST_ROWS = 2000;

/**
 * Asked of every list read. This is a client saying "give me a big page", not a copy of the server's
 * cap: the contract guarantees an over-sized `?pageSize=` is **clamped rather than refused**, and the
 * reply echoes the size actually applied, so the server stays the authority on its own maximum. The
 * alternative — sending no `pageSize` and taking the default — cost four times the round trips to
 * dodge a coupling it did not actually dodge, and those trips are sequential: unnoticeable on a LAN,
 * seconds of blank dashboard over a real network.
 */
const REQUESTED_PAGE_SIZE = 200;

/**
 * Backstop, not the design. `MAX_LIST_ROWS` is what enforces the ceiling; this only stops an
 * unbounded loop if a server ever reports `hasMore` forever, which no correct one does.
 */
const MAX_LIST_REQUESTS = 40;

/** `?page=` is 1-based, per the contract. Named so the offset convention is stated, not remembered. */
const FIRST_PAGE = 1;

/**
 * The smallest page that still carries a `total` — for reads that want the count and not the rows.
 * One row comes back and is thrown away; `items` is still validated, so the shape check is not lost.
 */
const COUNT_ONLY_PAGE_SIZE = 1;

/** `shape: "read"` and provably so: this is only ever raised while walking a list. */
const tooLarge = (what: string, why: string) =>
  new ApiError(
    "too-large",
    0,
    `${what} is too large to show in full: ${why}. The list needs server-side paging.`,
    { shape: "read" },
  );

function asPage<T>(
  body: unknown,
  what: string,
  mapItem: (row: Row, what: string) => T,
): PageOf<T> {
  const row = asRow(body, what);
  return {
    items: asRows(row.items, `${what}.items`).map((item, i) =>
      mapItem(item, `${what}.items[${i}]`),
    ),
    page: reqNum(row, "page", what),
    pageSize: reqNum(row, "pageSize", what),
    total: reqNum(row, "total", what),
    hasMore: reqBool(row, "hasMore", what),
  };
}

/**
 * Walks every page of a list and returns the rows, so callers keep the whole-list semantics they had
 * before the envelope landed. Sequential by necessity: `hasMore` for page N only arrives with page N.
 *
 * `request` doubles as the page number rather than stepping from the `page` the server echoed. The
 * echo is the more trusting option in the one way that matters: a server that clamped a page number
 * from above would pin the echo and this loop would ask for the same page until the backstop fired,
 * where a counter of our own runs past the end and terminates on the empty page's `hasMore: false`.
 */
async function listAll<T>(
  what: string,
  path: string,
  query: Record<string, string | undefined>,
  mapItem: (row: Row, what: string) => T,
): Promise<T[]> {
  const rows: T[] = [];

  for (let request = FIRST_PAGE; request <= MAX_LIST_REQUESTS; request++) {
    const page = asPage(
      await getJson(what, path, {
        ...query,
        page: String(request),
        pageSize: String(REQUESTED_PAGE_SIZE),
      }),
      what,
      mapItem,
    );

    // Checked before the rows are kept, and on every page rather than only the first: `total` is the
    // server's own count against this filter, so the refusal costs one request, not forty.
    if (page.total > MAX_LIST_ROWS) {
      throw tooLarge(what, `it holds ${page.total} rows and this screen shows at most ${MAX_LIST_ROWS}`);
    }

    rows.push(...page.items);
    if (!page.hasMore) return rows;
  }

  throw tooLarge(what, `walking it did not reach the end within ${MAX_LIST_REQUESTS} requests`);
}

// ---------------------------------------------------------------------------------------------
// DTO mappers
// ---------------------------------------------------------------------------------------------

function toCard(row: Row, what: string): Card {
  return {
    id: reqStr(row, "id", what),
    cardUid: reqStr(row, "cardUid", what),
    label: optStr(row.label),
    isActive: reqBool(row, "isActive", what),
  };
}

function toStudent(row: Row, what: string): Student {
  // StudentDto also carries firstName / middleName / lastName / gender / photoUrl. Nothing in the
  // SPA reads them, so they are dropped here rather than widened into `Student`.
  return {
    id: reqStr(row, "id", what),
    studentNumber: reqStr(row, "studentNumber", what),
    fullName: reqStr(row, "fullName", what),
    email: optStr(row.email),
    course: optStr(row.course),
    yearLevel: optStr(row.yearLevel),
    section: optStr(row.section),
    status: reqStr(row, "status", what),
    cards: asRows(row.cards, `${what}.cards`).map((card, i) => toCard(card, `${what}.cards[${i}]`)),
  };
}

function toEvent(row: Row, what: string): EventItem {
  return {
    id: reqStr(row, "id", what),
    name: reqStr(row, "name", what),
    // Read, not dropped, since the edit form landed — and the contract says why in `EventDto`'s own
    // description: `PUT /events/{id}` is a full replacement, so a field this client cannot read back
    // is a field it cannot preserve. Dropping `description` here would have every event edited
    // through the UI come back with its description blanked, and every `requireRegistration` reset to
    // false, with nothing on screen having said so.
    description: optStr(row.description),
    location: optStr(row.location),
    requireRegistration: reqBool(row, "requireRegistration", what),
    startAt: reqStr(row, "startAt", what),
    endAt: reqStr(row, "endAt", what),
    attendanceMode: reqStr(row, "attendanceMode", what),
    graceMinutes: reqNum(row, "graceMinutes", what),
    status: reqStr(row, "status", what),
  };
}

function toAttendance(row: Row, what: string): AttendanceRecord {
  return {
    id: reqStr(row, "id", what),
    eventId: reqStr(row, "eventId", what),
    studentId: reqStr(row, "studentId", what),
    studentName: reqStr(row, "studentName", what),
    studentNumber: reqStr(row, "studentNumber", what),
    checkInAt: optStr(row.checkInAt),
    checkOutAt: optStr(row.checkOutAt),
    status: reqStr(row, "status", what),
    captureMethod: reqStr(row, "captureMethod", what),
  };
}

function toSummary(row: Row, what: string): EventSummary {
  return {
    eventId: reqStr(row, "eventId", what),
    eventName: reqStr(row, "eventName", what),
    expected: reqNum(row, "expected", what),
    present: reqNum(row, "present", what),
    late: reqNum(row, "late", what),
    absent: reqNum(row, "absent", what),
    excused: reqNum(row, "excused", what),
    unexpected: reqNum(row, "unexpected", what),
    attendanceRate: reqNum(row, "attendanceRate", what),
  };
}

// ---------------------------------------------------------------------------------------------
// Methods
// ---------------------------------------------------------------------------------------------

async function listStudents(filter?: {
  search?: string;
  course?: string;
  status?: string;
}): Promise<Student[]> {
  // WARNING — `course` is a LOSSY filter. It maps to `GET /students?course=`, which filters
  // `Students.Course`: the ADR-001 D-2 derived, read-only cache. That column is single-valued, so
  // for a student enrolled in more than one section it can only name one of them. The filter
  // therefore returns a plausible, non-empty, INCOMPLETE answer — 12 of 52 students in the real
  // roster (~23%) sit in 2+ sections. Wired because the signature has always had it; do not build
  // UI that treats the result as the full set for a course.
  //
  // Unwrapped at the seam: `Students.tsx` filters and pages its grid client-side over the rows it
  // holds, and `Dashboard.tsx` counts them, so both need the whole set rather than a window into it.
  const what = "GET /students";
  return listAll(
    what,
    "/students",
    { search: filter?.search, course: filter?.course, status: filter?.status },
    toStudent,
  );
}

/**
 * How many students there are, without the students.
 *
 * `total` is the server's own count against the same filter that would have produced the rows, so
 * this is one request and no DTO mapping — where `listStudents()` walks every page and maps every
 * row, each with its `cards` array, to produce the same integer. It returns a `number`, so the
 * Dashboard's KPI gets its value without paging following it into the component.
 */
async function countStudents(): Promise<number> {
  const what = "GET /students (count)";
  const body = await getJson(what, "/students", {
    page: String(FIRST_PAGE),
    pageSize: String(COUNT_ONLY_PAGE_SIZE),
  });
  return asPage(body, what, () => null).total;
}

async function getStudent(id: string): Promise<Student | undefined> {
  const what = "GET /students/{id}";
  const body = await getJsonOrMissing(what, `/students/${encodeURIComponent(id)}`);
  return body === undefined ? undefined : toStudent(asRow(body, what), what);
}

async function listEvents(status?: string): Promise<EventItem[]> {
  // Unwrapped at the seam. The sort below is the reason it has to be: ordering one page of a paged
  // list would put "newest" only within that page, which looks right and is not.
  const what = "GET /events";
  const events = await listAll(what, "/events", { status }, toEvent);
  // Newest first — the order the dashboard and the events list have always rendered. The contract
  // makes no ordering promise, so it stays a client concern rather than an assumption.
  return events.sort((a, b) => b.startAt.localeCompare(a.startAt));
}

/**
 * `POST /events` — §6.3. The first write this seam made.
 *
 * The event is always created `Draft`; `EventWriteRequest` carries no `status` because
 * `PATCH /events/{id}/status` is the only door into that column. See the type's own note.
 *
 * The open-coded narrow-and-re-raise this used to carry is gone: `writeJson` takes the mapper and
 * owns that catch, so every write gets it — including the ones written after this comment.
 */
async function createEvent(request: EventWriteRequest): Promise<EventItem> {
  return writeJson(
    "POST /events",
    "/events",
    { method: "POST", payload: request },
    "The event was created",
    toEvent,
  );
}

/**
 * `PUT /events/{id}` — §6.3. A **full replacement** of the event's own fields, which is why
 * `EventItem` reads `description` and `requireRegistration`: a field the caller cannot read back is a
 * field it cannot send back unchanged.
 *
 * The body is the same `EventWriteRequest` as create and carries no `status` for the same reason.
 * What is different is the refusals: `404` for an event that is not there or is soft-deleted, and
 * `409` — not 400 — when the event's status forbids the change. `Closed` refuses every edit, and
 * `Cancelled` refuses the five fields that decide what an attendance row *means*
 * (`startAt`, `endAt`, `graceMinutes`, `attendanceMode`, `requireRegistration`) while still allowing
 * name, description and location. The server's `detail` names which of them were moved and says to
 * re-send with the scheduling left alone; `httpError` ranks `detail` first, so that sentence is what
 * reaches the user rather than the fixed title.
 */
async function updateEvent(id: string, request: EventWriteRequest): Promise<EventItem> {
  return writeJson(
    "PUT /events/{id}",
    `/events/${encodeURIComponent(id)}`,
    { method: "PUT", payload: request },
    "The change was saved",
    toEvent,
  );
}

/**
 * `PATCH /events/{id}/status` — §6.3, and the **only** door into the status column. That is what makes
 * the roster freeze on close impossible to bypass: an event cannot reach `Closed` without passing
 * through the one server path that materialises its absentees.
 *
 * Which moves are legal is `eventStatus.ts`, mirrored from `EventStatusTransition`; this seam sends
 * whatever it is given and lets the server refuse. The refusals split deliberately:
 * **400** for a target not reachable from this status under any circumstances — the request is wrong
 * and re-sending it unchanged is always wrong, and the `detail` names what *is* reachable, or for a
 * terminal event points at `POST /attendance/manual` as the audited way to correct one student — and
 * **404** for an event that is not there or is soft-deleted.
 *
 * No 409, unlike `PUT /events/{id}` above. That code belongs to `EventLocked`, which only an *edit*
 * can raise; a status change has no well-formed-but-refused case, because the graph decides
 * reachability and an unreachable target is a wrong request rather than a conflicting one.
 *
 * **This is the one write in the app that is genuinely safe to re-send.** The server has an explicit
 * no-op arm: a request naming the status the event already holds succeeds without re-running the
 * freeze, which exists precisely so a `PATCH` retried after a timeout cannot mark a second cohort
 * Absent. `advise()` still classifies a network failure here as `may-duplicate`, because `shape` is a
 * proxy for idempotency and is exact only for POST — it errs safe. The confirmation's withheld-resend
 * sentence is where that is kept honest, and it is worded for this endpoint rather than in general.
 *
 * The reply is an `EventDto`. It is **not** the whole of what the server said: `ChangeStatusAsync`
 * composes a message carrying how many students were marked Absent, and `EventsController.ChangeStatus`
 * answers `Ok(response.Event)`, so that count never reaches this client. See `statusSettledText`.
 */
async function setEventStatus(id: string, status: EventStatusName): Promise<EventItem> {
  return writeJson(
    "PATCH /events/{id}/status",
    `/events/${encodeURIComponent(id)}/status`,
    { method: "PATCH", payload: { status } },
    "The status was changed",
    toEvent,
  );
}

/**
 * `DELETE /events/{id}` — §6.3, and **soft** (§4.5 `IsDeleted`).
 *
 * Allowed from any status including `Closed`: the attendance rows survive untouched, so nothing is
 * lost and the event is restorable by clearing one flag — though nothing in this SPA can clear it,
 * which is why the confirmation says so out loud.
 *
 * A second delete is `404`, deliberately: by then the event is invisible to every read, and reporting
 * success for a row the caller cannot see would be the misleading answer. Nothing here softens that
 * into a success — the screen re-reads afterwards and settles on "no event with this id", which is
 * both true and the same thing the user would see if someone else had deleted it first.
 *
 * Returns `void`. The server answers 204 with no body, and inventing an `EventItem` for a row that no
 * longer exists would be a value with nothing behind it.
 */
async function deleteEvent(id: string): Promise<void> {
  return writeNoContent("DELETE /events/{id}", `/events/${encodeURIComponent(id)}`, {
    method: "DELETE",
  });
}

async function getEvent(id: string): Promise<EventItem | undefined> {
  const what = "GET /events/{id}";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(id)}`);
  return body === undefined ? undefined : toEvent(asRow(body, what), what);
}

async function listAttendance(eventId: string): Promise<AttendanceRecord[]> {
  // Unwrapped at the seam. `EventDetail.tsx` prints `Live attendance ({records.length})` next to the
  // grid, so a partial page would render a count that contradicts the event summary beside it.
  const what = "GET /attendance";
  return listAll(what, "/attendance", { eventId }, toAttendance);
}

async function eventSummary(eventId: string): Promise<EventSummary | undefined> {
  const what = "GET /events/{id}/summary";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(eventId)}/summary`);
  return body === undefined ? undefined : toSummary(asRow(body, what), what);
}

/**
 * Explanation of why an admin-browser tap is refused. One string, one place — the message the
 * Snackbar shows and the reason in the report are the same text.
 */
const TAP_UNAVAILABLE =
  "Simulated taps are disabled against the real API: POST /attendance/tap is the capture surface " +
  "and requires a DeviceKey, which the admin SPA deliberately does not hold.";

/**
 * NOT WIRED — deliberately, and this is a finding rather than an oversight.
 *
 * `POST /api/v1/attendance/tap` is secured by the `DeviceKey` scheme
 * (`Authorization: DeviceKey eams_dk_<keyId>_<secret>`), a credential the plan's §11 scopes to
 * `attendance.capture` and nothing else. Putting one in this bundle would hand a page that can only
 * *display* attendance a key that can *write* it — the precise inversion the contract rejects in its
 * own note on `GET /attendance/live/{eventId}` — and a Vite env var is inlined at build time, so the
 * GitHub Pages artefact would publish a write credential as a static asset.
 *
 * Returns a truthful refusal rather than throwing, so `EventDetail`'s existing Snackbar says
 * something honest instead of the button appearing to do nothing. `POST /attendance/manual` is the
 * admin-scoped alternative (open, no device key) but records `captureMethod: Manual` and skips the
 * grace-period logic, so substituting it silently would change behaviour — that needs JJ's call.
 */
async function tap(_eventId: string, _cardUid: string): Promise<{ ok: boolean; message: string }> {
  return { ok: false, message: TAP_UNAVAILABLE };
}

/** One choice in the tap picker: a card that can still be tapped, and whose it is. */
export interface UntappedCard {
  uid: string;
  name: string;
}

/**
 * What the tap picker can offer, or why it can offer nothing.
 *
 * A bare `UntappedCard[]` cannot carry this: an empty array is read as "everyone has tapped in", so a
 * roster that failed to load would congratulate the user on a full house. That is the same collapse
 * of "failed" into "returned nothing" that `useApiResource` exists to prevent, reappearing one level
 * down inside the payload — where the hook cannot see it, because the read as a whole succeeded.
 */
export type TapRoster =
  | { status: "ready"; cards: UntappedCard[] }
  | { status: "unavailable"; error: unknown };

/** Everything the event screen renders, settled together. */
export interface EventDetailData {
  /** `undefined` when no event has that id — "no such event", which is an answer, not a failure. */
  event: EventItem | undefined;
  summary: EventSummary | undefined;
  records: AttendanceRecord[];
  roster: TapRoster;
}

/**
 * Who has not tapped yet, derived from rows the caller already holds rather than fetched again.
 *
 * No single endpoint answers this, so it is composed from reads that exist rather than an invented
 * third. `GET /events/{id}/roster` knows who is *expected*, which would be the better source, but
 * `EventRosterEntryDto` carries no card UID at all — it cannot drive a UID picker.
 */
function untappedFrom(students: Student[], recorded: AttendanceRecord[]): UntappedCard[] {
  const tapped = new Set(recorded.map((record) => record.studentId));
  return students.flatMap((student) => {
    if (tapped.has(student.id)) return [];
    // Only active cards: an inactive UID is a guaranteed `CardNotFound`, so offering it would put a
    // choice in the picker that cannot succeed.
    const card = student.cards.find((c) => c.isActive);
    return card ? [{ uid: card.cardUid, name: student.fullName }] : [];
  });
}

/**
 * The whole event screen as one read.
 *
 * It replaces four separate calls from the component, one of which — `untappedCards(id)` — walked
 * the attendance list a *second* time to work out who was missing from it, while the component was
 * already fetching that same list beside it. Every mount and every refresh paged through attendance
 * twice to render one grid. Deriving the picker from `records` here settles it: attendance is walked
 * once, and the composition stays behind this seam instead of leaking into the page.
 *
 * The four reads run concurrently, so the screen still costs one round of latency rather than four.
 *
 * Three of them fail together, which is the point — `event`, `summary` and `records` are three views
 * of one fact, and a screen assembled from parts that settle independently can render half a truth
 * whose halves contradict each other on the same page. The roster is not one of those views. It is an
 * input aid for the picker: it does not appear anywhere in what the screen asserts about the event,
 * and on a Closed event the picker it feeds is never rendered at all. Letting it take the page down
 * means a roster past `MAX_LIST_ROWS` — reachable at this school's scale — makes a perfectly healthy
 * attendance grid unviewable. So it is caught into a value and reported as `TapRoster`, which the
 * picker must then say out loud; what it must never become is an empty picker.
 */
async function eventDetail(eventId: string): Promise<EventDetailData> {
  const [event, summary, records, roster] = await Promise.all([
    getEvent(eventId),
    eventSummary(eventId),
    listAttendance(eventId),
    // Settled, not swallowed: the rejection is captured into a value here so the picker can render it
    // as a reason instead of it taking the whole page down with the three reads above.
    listStudents().then(
      (students) => ({ ok: true, students }) as const,
      (cause: unknown) => ({ ok: false, cause }) as const,
    ),
  ]);

  return {
    event,
    summary,
    records,
    roster: roster.ok
      ? { status: "ready", cards: untappedFrom(roster.students, records) }
      : { status: "unavailable", error: roster.cause },
  };
}

export const api = {
  listStudents,
  countStudents,
  getStudent,
  listEvents,
  createEvent,
  updateEvent,
  setEventStatus,
  deleteEvent,
  getEvent,
  listAttendance,
  eventSummary,
  tap,
  eventDetail,
};
