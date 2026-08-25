// The single seam to the EAMS .NET API (`EAMS.Api`, base path `/api/v1`). Components import `api`
// and nothing reaches around it.
//
// Shapes are pinned to the committed contract at `docs/api/openapi.json` — camelCase JSON, RFC 7807
// `application/problem+json` on every error. Responses are narrowed field by field on the way in:
// this is a system boundary, so a payload that drifts from the contract fails loud here instead of
// arriving three components deep as `undefined`.

import { GROUP_SOURCE_TYPE, GROUP_TYPE_SECTION } from "./types";
import type {
  AuthUser,
  Student,
  StudentCardRequest,
  StudentWriteRequest,
  Card,
  Device,
  DeviceKeyIssued,
  IssuedKeyDevice,
  DeviceWriteRequest,
  EventAudience,
  EventScan,
  EventScanLog,
  EventAudienceGroup,
  EventAudienceRequest,
  EventAudienceResult,
  EventAudienceStudent,
  EventItem,
  EventStatusName,
  EventWriteRequest,
  AttendanceRecord,
  EventSummary,
  SisImportBatch,
  SisImportPreview,
  SisImportRow,
  SisImportRowEntity,
  SisImportRunRequest,
  StudentGroup,
  Term,
  TermWriteRequest,
} from "./types";

// The one security-relevant dependency this module has, and the reason it is in the import block
// rather than tucked in beside the code that uses it: a reader scanning these lines has to be able to
// learn that `api.ts` holds the only caller of `getDeviceKey()`. Everything it provides is inert
// outside `import.meta.env.DEV`, so nothing it names survives a production build.
import { DEVICE_KEY_SCHEME, deviceKeyStatus, getDeviceKey } from "./deviceKey";

// The session this seam signs its requests with. `api.ts` is the only module that reads the access
// token and the only one that renews it — see `authSession.ts` for why the store is not a hook and
// why the refresh token is not in it.
import {
  accessToken,
  beginSession,
  csrfToken,
  CSRF_HEADER_NAME,
  endSession,
} from "./authSession";

/**
 * **Root-relative, so the SPA and the API are the same origin.** It used to be
 * `http://localhost:5080/api/v1`, and that has to change now that a session travels in a cookie:
 * `eams_rt` is `SameSite=Strict` and the browser will not attach it to a request from `:5173` to
 * `:5080` without the API opting into credentialed CORS — which is a wider hole than the one it
 * would close. `npm run dev` proxies `/api` to the API host instead (see `vite.config.ts`), so the
 * cookie is first-party and no CORS policy is involved at all.
 *
 * A deployment that serves the SPA from somewhere other than the API's own origin sets
 * VITE_API_BASE_URL (see `.env.example`) — under IIS the two are sibling applications, so it is
 * `/eamsapi/api/v1` rather than a host. Vite inlines that at build time.
 */
const DEFAULT_BASE_URL = "/api/v1";

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

/**
 * A required array of strings — `AuthUserDto.permissions` is the only one on this surface.
 *
 * An empty array is accepted and is not the same as a missing key: a user whose roles grant nothing
 * is a real, sign-in-able account that simply sees no gated screen, and refusing it here would fail
 * their login with a contract error over a permission set the server considers perfectly valid.
 */
function reqStrings(row: Row, key: string, what: string): readonly string[] {
  const value = row[key];
  if (!Array.isArray(value)) {
    throw offContract(what, `\`${key}\` should be an array, got ${describeType(value)}`);
  }
  return value.map((item, i) => {
    if (typeof item !== "string") {
      throw offContract(what, `\`${key}[${i}]\` should be a string, got ${describeType(item)}`);
    }
    return item;
  });
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

/**
 * A required array of strings — `EventAudienceResultDto.warnings`, and nothing else so far.
 *
 * Required rather than defaulted to `[]` when the key is missing, which is the tempting shortcut and
 * the wrong one here: an absent `warnings` and an empty one would then be indistinguishable, and the
 * empty case is the *ordinary* one. A build that silently read a drifted reply as "no warnings" would
 * swallow exactly the field whose entire purpose is not being swallowed — a group attached from
 * another term warns rather than refuses, so the warning is the only evidence it happened.
 */
function reqStrs(row: Row, key: string, what: string): string[] {
  const value = row[key];
  if (!Array.isArray(value)) {
    throw offContract(what, `\`${key}\` should be an array, got ${describeType(value)}`);
  }
  return value.map((item, i) => {
    if (typeof item !== "string") {
      throw offContract(what, `\`${key}[${i}]\` should be a string, got ${describeType(item)}`);
    }
    return item;
  });
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
 *
 * It is the *default*, not the only budget — `send` takes a per-request override, because a read's
 * budget is the wrong shape for a request whose duration is dominated by bytes going out. See
 * `UPLOAD_TIMEOUT_MS`.
 */
const REQUEST_TIMEOUT_MS = 15_000;

/**
 * The budget for the one request on this seam that uploads a file.
 *
 * A read's 15 s is a statement about how long a server may think. An upload's duration is mostly the
 * client's own upstream link: `MAX_UPLOAD_BYTES` is 10 MB (the server's `RequestSizeLimit`, which the
 * screen shows the operator as the permitted size), and 10 MB over the ~1 Mbps upstream of a school
 * connection is around 80 seconds of sending before the server has seen the whole request. Under the
 * read budget the client would accept a file it then aborts mid-send — and that abort is the expensive
 * failure, not a harmless one: a timed-out **write** is `may-duplicate`, so Upload is withheld and the
 * operator is told to reload having uploaded nothing.
 *
 * So the ceiling stays where the server put it and the clock is widened to match it. Generous rather
 * than exact, because the point is to bound a hung request, not to police a slow one.
 */
const UPLOAD_TIMEOUT_MS = 180_000;

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
const timedOut = (what: string, cause: unknown, shape: ApiRequestShape, budgetMs: number) =>
  new ApiError(
    "network",
    0,
    // The budget is passed in rather than read from `REQUEST_TIMEOUT_MS`: an upload runs on its own,
    // and a message naming a number the request was not actually held to is a false statement in the
    // one place someone is trying to work out what happened.
    `The EAMS API at ${baseUrl} did not finish answering ${what} within ${budgetMs} ms.` +
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
 * The scheme a *person's* token uses, as `AuthTokenResponse.tokenType` names it.
 *
 * A sibling of `DEVICE_KEY_SCHEME` and never a substitute for it: a kiosk sends
 * `Authorization: DeviceKey …` and is scoped to `attendance.capture` alone, a person sends
 * `Authorization: Bearer …`. Two credentials reach this API and they never mix — the tap path below
 * composes its own header for exactly that reason and is not routed through `send`.
 */
const BEARER_SCHEME = "Bearer";

/** The one status that asks "is this session still live?". Everything else is the caller's answer. */
const HTTP_UNAUTHORIZED = 401;

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
/**
 * `form` is the multipart arm, and it exists because one endpoint on this API does not take JSON:
 * `POST /sis/import/upload` is `multipart/form-data` with a `file` part and a `termId` part. Widening
 * the union was the honest way to express that — the alternative was a second `fetch` call site beside
 * `send`, which would have had its own timeout signal, its own error taxonomy and its own answer to
 * `shape`, i.e. the seam this file exists to be, with a hole in it.
 *
 * It is a distinct member rather than an optional field on the JSON arm so that **a request cannot
 * carry both**, and the two are discriminated by `"form" in write` rather than by `method`: both are
 * `POST`, so the method no longer discriminates and pretending it does would narrow wrongly.
 *
 * The `never` fields are what make "cannot carry both" hold for values as well as for literals. Excess
 * property checking catches `{ method: "POST", payload, form }` written out at a call site and catches
 * nothing at all when the same object arrives in a variable; typing each arm's missing field as
 * optional-`never` makes the *type* incompatible either way. Same idiom as `StudentWriteRequest`.
 */
type RequestBody =
  | { method: "POST" | "PUT" | "PATCH"; payload: unknown; form?: never }
  | { method: "POST"; form: FormData; payload?: never }
  | { method: "DELETE" };

/**
 * What actually goes on the wire: the body, and the `Content-Type` that describes it — decided
 * together, in the one place, because they are one decision.
 *
 * **The multipart arm returns no content type on purpose, and that is load-bearing.** A
 * `multipart/form-data` header is incomplete without the `boundary=` parameter, which is generated
 * per-request; setting the header by hand means sending a boundary the body does not use, and the
 * server's multipart reader then finds no parts at all — a `400 "No file was uploaded."` for a request
 * that carried the file. Left unset, `fetch` writes the header itself from the `FormData` and the two
 * agree by construction.
 */
interface WireBody {
  body: BodyInit | undefined;
  contentType: string | undefined;
}

const NO_BODY: WireBody = { body: undefined, contentType: undefined };

const wireBodyOf = (write: RequestBody | undefined): WireBody => {
  if (write === undefined || write.method === "DELETE") return NO_BODY;
  if ("form" in write) return { body: write.form, contentType: undefined };
  return { body: JSON.stringify(write.payload), contentType: JSON_MEDIA_TYPE };
};

/**
 * One request, signed with the token it is handed, and no renewal logic of its own.
 *
 * Split out of `send` so that the retry `send` performs is *this* function called a second time
 * rather than `send` calling itself: a self-call would carry the whole renewal path with it, and
 * "one refresh, then one replay" would become a recursion whose depth is decided by how long the
 * server keeps answering 401. The bound is structural here — `send` calls this at most twice.
 *
 * @param bearer the access token to sign with, captured by the caller *before* the request goes out.
 *   Passed in rather than read here, because the value that matters on the way back is the one this
 *   attempt actually used — see `send`.
 */
async function sendOnce(
  what: string,
  path: string,
  query: Record<string, string | undefined> | undefined,
  write: RequestBody | undefined,
  timeoutMs: number,
  bearer: string | undefined,
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
  // content type is describing something it did not send, and a multipart body declaring one written
  // here would declare the wrong boundary. `shape` above is deliberately NOT derived this way; see the
  // comment there.
  const { body, contentType } = wireBodyOf(write);

  // Composed here and nowhere else in the SPA. A component that built its own `Authorization` header
  // would be holding the access token, which is the one value `authSession.ts` keeps out of the React
  // tree entirely — and it would miss the renewal below, so its request would be the one that fails
  // fifteen minutes in while every other request quietly renewed.
  const headers: Record<string, string> = { Accept: JSON_MEDIA_TYPE };
  if (contentType !== undefined) headers["Content-Type"] = contentType;
  if (bearer !== undefined) headers.Authorization = `${BEARER_SCHEME} ${bearer}`;

  try {
    return await fetch(buildUrl(path, query), {
      method,
      headers,
      body,
      signal: AbortSignal.timeout(timeoutMs),
      // Not `include`. Every route reached through here authenticates with `Authorization: Bearer`,
      // and the refresh cookie is scoped to `/api/v1/auth`, so there is nothing for the browser to
      // attach — asking it to attach credentials anyway would only widen what a future same-site
      // page could ride on. The two routes that *do* need the cookie go through `sendAuth`.
      credentials: "same-origin",
    });
  } catch (cause) {
    if (isTimeout(cause)) throw timedOut(what, cause, shape, timeoutMs);
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

/**
 * A request, and — if the session had just aged out from under it — **one** renewal and **one**
 * replay.
 *
 * ---------------------------------------------------------------------------------------------
 * WHY THE STAMPEDE IS THE THING THIS GUARDS AGAINST
 * ---------------------------------------------------------------------------------------------
 *
 * The obvious implementation renews on every 401, and it is wrong in a way that only shows up on a
 * screen that reads more than one thing. `eventDetail()` fires four requests in one `Promise.all`; if
 * the access token expired a moment earlier, all four come back 401 and all four call
 * `POST /auth/refresh` with the same cookie. Refresh rotation is **single-use, and a second
 * presentation of an already-rotated token is read as a replay** — which revokes every live token in
 * the family (`AuthController.Refresh` says so in its own remarks). So the naive version does not
 * merely make three redundant calls: three of them are treated as a stolen-token replay and the user
 * is signed out of every tab, by their own dashboard loading.
 *
 * Two things stop that, and both are needed.
 *
 * **One in-flight renewal, shared.** `renewSession()` hands every caller the same promise while one
 * is running, so requests that 401 *concurrently* wait on a single call rather than each making one.
 *
 * **A staleness check, for the ones that are not concurrent.** A request that went out with token
 * `T1`, was answered slowly, and comes back 401 *after* someone else already renewed to `T2` must not
 * start a second renewal — the first one already fixed it. Comparing the token this attempt actually
 * used against the token the store holds now is what tells those apart, and it is why `sendOnce`
 * takes the bearer as an argument instead of reading it itself: read on the way back, the value would
 * always be the current one and the comparison would be against itself.
 *
 * **A 401 on a request that carried no token is not a renewal question.** Nothing signed it, so there
 * is nothing that could have expired; it is a call made before sign-in and it is returned as it
 * stands rather than made the reason to spend a rotation.
 *
 * The replay is the same `sendOnce` call with the new token, once. If *that* 401s the response is
 * returned and becomes an ordinary `ApiError` — where `advise()`'s 401 arm now reads it as a dead
 * session rather than as something worth pressing again.
 *
 * **Replaying a write is safe here, and it is the one thing in this function worth checking rather
 * than assuming.** Everywhere else in this file a re-sent `POST` is how a user ends up with two
 * events — `ApiRequestShape` exists for exactly that. It is different here because the first attempt
 * was answered **401**: authentication runs before the action, so the server refused it before
 * anything could be applied. The replay is the first attempt that the server actually considered.
 * That reasoning holds for 401 alone, which is why this branch is on that status and not on a range.
 */
async function send(
  what: string,
  path: string,
  query?: Record<string, string | undefined>,
  write?: RequestBody,
  /** Overridden only where the default is the wrong shape of budget — see `UPLOAD_TIMEOUT_MS`. */
  timeoutMs: number = REQUEST_TIMEOUT_MS,
) {
  const signedWith = accessToken();
  const answer = await sendOnce(what, path, query, write, timeoutMs, signedWith);

  if (answer.status !== HTTP_UNAUTHORIZED || signedWith === undefined) return answer;

  // Someone else's renewal already replaced the token this attempt used, so this request needs the
  // new one and not a rotation of its own.
  const renewed = accessToken() !== signedWith ? true : (await renewSession()) === "renewed";
  if (!renewed) return answer;

  return sendOnce(what, path, query, write, timeoutMs, accessToken());
}

async function parseBody(
  res: Response,
  what: string,
  shape: ApiRequestShape,
  /** The budget the request was actually held to — the same signal is still on the body stream. */
  timeoutMs: number = REQUEST_TIMEOUT_MS,
): Promise<unknown> {
  try {
    return await res.json();
  } catch (cause) {
    // The timeout signal stays attached to the response body, so a server that writes its headers and
    // then stalls mid-stream lands here rather than in `send`. It is still a timeout, and calling it
    // `malformed` would tell the user their build and the API are on different versions and that
    // retrying will not help — both false, and the second one strands them.
    if (isTimeout(cause)) throw timedOut(what, cause, shape, timeoutMs);
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
 * @param timeoutMs the budget for this one request. Defaulted, and overridden only by the upload,
 *   whose duration is dominated by the bytes going out rather than by the server thinking.
 */
async function writeJson<T>(
  what: string,
  path: string,
  write: RequestBody,
  applied: string,
  map: (row: Row, what: string) => T,
  timeoutMs: number = REQUEST_TIMEOUT_MS,
): Promise<T> {
  const res = await send(what, path, undefined, write, timeoutMs);
  if (!res.ok) throw httpError(what, await readProblem(res), "write");
  const body = await parseBody(res, what, "write", timeoutMs);
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
// Sessions — §6.1 / §11, and the only three routes in this file that are not ordinary requests
// ---------------------------------------------------------------------------------------------
//
// `POST /auth/login`, `POST /auth/refresh` and `POST /auth/logout` do not go through `send`, and each
// of the three differences is load-bearing:
//
//   - **`credentials: "include"`.** These are the only routes the refresh cookie is scoped to, so
//     they are the only ones where the browser has anything to attach. Every other endpoint
//     authenticates with a header a cross-site page cannot set, which is what confines this API's
//     CSRF exposure to these two — see `AuthCookies.CsrfTokenMatches`.
//   - **The `X-CSRF-Token` header**, on refresh and logout. Its value is the readable `eams_csrf`
//     cookie, re-read on every call because the server re-mints it on every issue.
//   - **No renewal on a 401.** A 401 from `refresh` *is* the answer; asking `send` to renew it would
//     be a refresh that refreshes itself.
//
// `GET /auth/me` is deliberately not wired. `login` and `refresh` both return the identical
// `AuthUserDto`, so calling it would be a round trip to re-ask a question the reply in hand already
// answered — and it would answer it from the same token, so not even more freshly.

const AUTH_LOGIN_PATH = "/auth/login";
const AUTH_REFRESH_PATH = "/auth/refresh";
const AUTH_LOGOUT_PATH = "/auth/logout";

const SIGN_IN_WHAT = "Signing in";
const RENEWAL_WHAT = "Renewing the session";
const SIGN_OUT_WHAT = "Signing out";

/**
 * What a renewal settled as. Three arms rather than a boolean, because the middle one is the whole
 * reason this is not a boolean.
 *
 * `renewed` — a new access token is in the store. `ended` — the server refused definitively (401, or
 * a 403 saying the double-submit pair did not match); the session is over and has been cleared.
 * `unreachable` — the API did not answer, or failed while answering, or rate-limited the attempt.
 *
 * **`unreachable` deliberately does not sign the user out.** A 502 from a proxy or a dropped Wi-Fi
 * connection is not evidence that a session ended, and treating it as one throws away whatever the
 * user had typed at the moment the network hiccuped — while offering them a login form they cannot
 * submit either, because the API is exactly as unreachable for that. The failed request surfaces as
 * an ordinary `ApiError` instead and the screen says what happened.
 */
type Renewal = "renewed" | "ended" | "unreachable";

/** The refusals that mean the session is over: 401 from the token, 403 from the CSRF pair. */
const HTTP_FORBIDDEN = 403;

/**
 * The renewal that may be running. **The single most important variable in this file.**
 *
 * See `send` for what a stampede of parallel refreshes does to the rotation's replay detection. This
 * cell is what makes concurrent callers share one call; the token comparison in `send` is what stops
 * sequential ones from starting a second.
 */
let renewalInFlight: Promise<Renewal> | undefined;

/**
 * Renews the session, or joins the renewal already in progress.
 *
 * The cell is cleared in `finally` — before the promise it holds resolves for anyone awaiting it,
 * which is fine (they hold the promise, not the cell) and is what lets a *later* 401 start a genuinely
 * new renewal rather than being answered by a stale settled one.
 */
function renewSession(): Promise<Renewal> {
  renewalInFlight ??= runRenewal().finally(() => {
    renewalInFlight = undefined;
  });
  return renewalInFlight;
}

async function runRenewal(): Promise<Renewal> {
  let res: Response;
  try {
    res = await sendAuth(AUTH_REFRESH_PATH, { csrf: true, bearer: false });
  } catch (cause) {
    // Not a swallow: the outcome is returned, the session is deliberately left alone (see `Renewal`),
    // and the reason is recorded. `debug` rather than `error` — a renewal racing a network blip is
    // ordinary, and red console entries for ordinary things teach people to scroll past the console.
    console.debug(`EAMS: ${RENEWAL_WHAT.toLowerCase()} could not reach the API`, cause);
    return "unreachable";
  }

  if (res.ok) {
    try {
      const issued = issuedSessionFrom(await parseBody(res, RENEWAL_WHAT, "write"), RENEWAL_WHAT);
      beginSession(issued.accessToken, issued.user);
      return "renewed";
    } catch (cause) {
      // The server rotated the cookie and this build cannot read the token it answered with, so
      // there is no way to sign another request: the session is over in every sense that matters
      // here, even though the browser now holds a perfectly good new cookie. `warn`, not `debug` —
      // unlike a network blip, this is the two sides being on different versions.
      console.warn(`EAMS: ${AUTH_REFRESH_PATH} answered off-contract`, cause);
      endSession("expired");
      return "ended";
    }
  }

  if (res.status === HTTP_UNAUTHORIZED || res.status === HTTP_FORBIDDEN) {
    // 401: no cookie, or one that is expired, unknown, or already used — the API deliberately does
    // not say which. 403: the double-submit pair did not match, which after a browser restart is what
    // a surviving `eams_rt` beside a lost session-scoped `eams_csrf` looks like. Both are "this
    // browser cannot renew", and both are answered by signing in.
    endSession("expired");
    return "ended";
  }

  console.debug(`EAMS: ${AUTH_REFRESH_PATH} answered ${res.status}; the session is left as it was`);
  return "unreachable";
}

/**
 * The three cookie-bearing routes, on their own `fetch` for the reasons the section note gives.
 *
 * The `AbortSignal.timeout` is the same budget an ordinary read gets: a sign-in that hangs forever is
 * the one failure a user cannot act on, and it is worse here than anywhere else because the form has
 * nothing on screen to fall back to.
 */
async function sendAuth(
  path: string,
  options: { readonly payload?: unknown; readonly csrf: boolean; readonly bearer: boolean },
): Promise<Response> {
  const headers: Record<string, string> = { Accept: JSON_MEDIA_TYPE };
  if (options.payload !== undefined) headers["Content-Type"] = JSON_MEDIA_TYPE;

  if (options.csrf) {
    const token = csrfToken();
    // Sent when there is one and simply omitted when there is not, rather than refused here. A
    // missing cookie and a mismatched one are the same condition to the server (`403
    // CsrfTokenInvalid`), and inventing a *second*, client-side way for the same thing to fail would
    // mean one condition reaching the user as two different sentences depending on which side noticed
    // — the exact split `AuthCookies.ClearRefreshToken` refuses to create on the server.
    if (token !== undefined) headers[CSRF_HEADER_NAME] = token;
  }

  if (options.bearer) {
    const token = accessToken();
    if (token !== undefined) headers.Authorization = `${BEARER_SCHEME} ${token}`;
  }

  return fetch(buildUrl(path), {
    method: "POST",
    headers,
    body: options.payload === undefined ? undefined : JSON.stringify(options.payload),
    signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    credentials: "include",
  });
}

/** The access token and the person it speaks for, as `login` and `refresh` both answer. */
interface IssuedSession {
  readonly accessToken: string;
  readonly user: AuthUser;
}

/**
 * `AuthTokenResponse`, narrowed at the boundary like every other reply.
 *
 * `tokenType` is checked rather than ignored, and it is the one field here worth being fussy about:
 * the DTO says it exists precisely so a client composes its header from the response instead of from
 * a hard-coded string it will get wrong once. Checking it means a server that ever answered a
 * different scheme fails here, loudly, instead of every subsequent request being refused for a reason
 * that names nothing.
 *
 * `expiresAt` is deliberately **not** read. The DTO suggests refreshing ahead of it because "a 401
 * mid-navigation is a lost page" — and with the replay in `send`, it is not: the request that meets
 * an expired token is renewed and re-sent without the caller ever seeing it. A timer would add a
 * second renewal path, firing in background tabs nobody is looking at, to solve a problem the
 * reactive path already solves losslessly.
 */
function issuedSessionFrom(body: unknown, what: string): IssuedSession {
  const row = asRow(body, what);

  const scheme = reqStr(row, "tokenType", what);
  if (scheme !== BEARER_SCHEME) {
    throw offContract(what, `\`tokenType\` should be "${BEARER_SCHEME}", got "${scheme}"`);
  }

  return {
    accessToken: reqStr(row, "accessToken", what),
    user: authUserFrom(asRow(row.user, `${what}.user`), `${what}.user`),
  };
}

const authUserFrom = (row: Row, what: string): AuthUser => ({
  id: reqStr(row, "id", what),
  schoolId: reqStr(row, "schoolId", what),
  email: reqStr(row, "email", what),
  fullName: reqStr(row, "fullName", what),
  permissions: reqStrings(row, "permissions", what),
});

/**
 * `POST /auth/login`. On success the session is live before this resolves; on failure it throws an
 * `ApiError` carrying the server's own sentence and its `traceId`.
 *
 * **Every failure is one 401 with `code: InvalidCredentials`** — unknown address, wrong password and
 * deactivated account alike, deliberately, so that the answer is not an account-enumeration oracle.
 * There is nothing finer for a screen to branch on and it must not invent one. A 429 is the other
 * refusal a form will meet, and it is the rate limiter rather than the credential.
 *
 * The password is passed straight through and is never stored, logged, or retried with.
 */
async function signIn(email: string, password: string): Promise<AuthUser> {
  let res: Response;
  try {
    res = await sendAuth(AUTH_LOGIN_PATH, {
      payload: { email, password },
      csrf: false,
      bearer: false,
    });
  } catch (cause) {
    if (isTimeout(cause)) throw timedOut(SIGN_IN_WHAT, cause, "write", REQUEST_TIMEOUT_MS);
    throw new ApiError(
      "network",
      0,
      `Cannot reach the EAMS API at ${baseUrl} — ${SIGN_IN_WHAT.toLowerCase()} was not sent.`,
      { shape: "write", cause },
    );
  }

  if (!res.ok) throw httpError(SIGN_IN_WHAT, await readProblem(res), "write");

  const issued = issuedSessionFrom(await parseBody(res, SIGN_IN_WHAT, "write"), SIGN_IN_WHAT);
  beginSession(issued.accessToken, issued.user);
  return issued.user;
}

/**
 * The silent renewal a page load starts — **the intended startup path, not an error**.
 *
 * The access token lives in memory only, so a reload always begins with none; whether the browser
 * still holds a renewable session is a question only this call can answer, and a UI that skipped it
 * would sign a signed-in user out every time they pressed F5.
 *
 * It is the same single-flight call `send` uses, which is what stops a reload whose first screen
 * fires four reads from racing its own startup refresh.
 *
 * **It resolves the `unknown` state in every case, including the one `renewSession` deliberately
 * leaves alone.** A renewal that could not reach the API does not sign a *signed-in* user out — but
 * at startup there is nobody to keep signed in, and leaving the store on `unknown` would hold the app
 * on its loading screen for as long as the API stays down, with nothing on screen saying why.
 * `never` rather than `expired`, because "we could not find out" must not be shown to a first-time
 * visitor as "you were signed out".
 */
async function restoreSession(): Promise<void> {
  if ((await renewSession()) === "unreachable") endSession("never");
}

/**
 * `POST /auth/logout`, then the local session, in that order — and the local session goes either way.
 *
 * **This session only.** Signing out on a laptop does not sign the user out on their phone; the
 * operation that ends every session is `POST /auth/change-password`.
 *
 * A failure is reported to the console and does not stop the local half. Refusing to clear would
 * leave someone who pressed Sign out looking at a signed-in interface, which is worse than the thing
 * being hedged against; and the honest statement of what a failed logout leaves behind is that the
 * refresh cookie may still be live until it expires, which is a fact no amount of client-side
 * insistence changes. `warn` rather than `debug`: unlike a renewal racing a blip, a logout the server
 * did not honour is not ordinary.
 */
async function signOut(): Promise<void> {
  try {
    const res = await sendAuth(AUTH_LOGOUT_PATH, { csrf: true, bearer: true });
    if (!res.ok) {
      console.warn(
        `EAMS: ${AUTH_LOGOUT_PATH} answered ${res.status}. This browser is signed out, but the ` +
          "session may not have been revoked on the server until it expires.",
      );
    }
  } catch (cause) {
    console.warn(`EAMS: ${SIGN_OUT_WHAT.toLowerCase()} did not reach the API`, cause);
  } finally {
    endSession("signedOut");
  }
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

/**
 * `shape: "read"` and provably so: every caller is a list read — `listAll`'s page walk, and
 * `getImportRows`, which is a bare array rather than a paged envelope and so applies the ceiling
 * itself.
 */
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
  return {
    id: reqStr(row, "id", what),
    studentNumber: reqStr(row, "studentNumber", what),
    fullName: reqStr(row, "fullName", what),
    // Read, not dropped, since the student edit form landed — and for the reason `toEvent` records
    // below, which the contract states for this DTO too: `PUT /students/{id}` is a full replacement,
    // so a field this client cannot read back is a field it cannot preserve. `fullName` above is
    // composed by the server and cannot be split into these three, so without them the edit form had
    // no way to fill itself at all. Dropping `middleName`, `gender` or `photoUrl` here would blank
    // that column on every student edited through the UI, with nothing on screen having said so.
    //
    // `firstName` and `lastName` are `reqStr` and `middleName` is not, matching the contract exactly:
    // `StudentDto` declares the first two non-nullable (they are NOT NULL columns) and the third
    // nullable. Narrowing them as required is what makes a drift here fail loud rather than arrive in
    // the form as `undefined`.
    firstName: reqStr(row, "firstName", what),
    middleName: optStr(row.middleName),
    lastName: reqStr(row, "lastName", what),
    email: optStr(row.email),
    gender: optStr(row.gender),
    photoUrl: optStr(row.photoUrl),
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

/**
 * `DeviceDto` — and **the absence of a key field here is load-bearing.**
 *
 * The server's own `DeviceDto` has none: the plaintext token is carried by `DeviceKeyIssuedDto` alone,
 * and `DeviceLifecycleTests.No_read_response_ever_carries_the_key` asserts that on the serialized
 * bytes. So if a `key`/`apiKey`/`secret` ever appears on a read reply, this mapper dropping it on the
 * floor is the correct behaviour and not an oversight — a line reading it here would put a credential
 * into every row of a grid.
 *
 * `apiKeyId` is the *public* twelve characters and is `optStr` because the contract makes it null
 * until a key has been issued. The four timestamps are nullable for the same contract reasons: never
 * issued, never used (the column is written throttled, so a device can tap before it is set), never
 * revoked, never seen.
 */
function toDevice(row: Row, what: string): Device {
  return {
    id: reqStr(row, "id", what),
    name: reqStr(row, "name", what),
    // `reqStr`: the column is NOT NULL and the server substitutes `Kiosk` for a blank on the way in,
    // so an absent one is drift rather than an ordinary empty.
    deviceType: reqStr(row, "deviceType", what),
    readerModel: optStr(row.readerModel),
    isActive: reqBool(row, "isActive", what),
    apiKeyId: optStr(row.apiKeyId),
    // Required, and narrowed as such deliberately: this is the single boolean the whole page leads
    // with, and a missing one arriving as `undefined` would render every device as unable to tap —
    // a screenful of confident wrong answers. Failing loud at the seam is the alternative.
    hasActiveKey: reqBool(row, "hasActiveKey", what),
    apiKeyIssuedAt: optStr(row.apiKeyIssuedAt),
    apiKeyLastUsedAt: optStr(row.apiKeyLastUsedAt),
    apiKeyRevokedAt: optStr(row.apiKeyRevokedAt),
    lastSeenAt: optStr(row.lastSeenAt),
  };
}

/**
 * `DeviceKeyIssuedDto` — the only reply in this module that carries a credential, and therefore the one
 * mapper in this file where fail-loud is the *wrong* default.
 *
 * `apiKey` is `reqStr` rather than `optStr`, and that is the important choice. An absent token would
 * otherwise arrive as `undefined` and be rendered as an empty reveal panel: the operator would be told
 * their device was registered, shown nothing, and close the one dialog that would ever have held the
 * key — after which the only remedy is rotating a device they just created. Failing here instead
 * routes it through `writeJson`'s catch, which says the device *was* created and that this build could
 * not read the reply back, which is the sentence that gets them to check the list rather than press
 * again.
 *
 * **The nested device is read best-effort, and that asymmetry is the whole point.** It used to go
 * through `toDevice`, which narrows five fields as required — so a server that dropped, say,
 * `hasActiveKey` from this one nested object would throw *after* the key had been minted, `writeJson`
 * would turn it into `malformed`, and the token sitting in `body` would be discarded with it: a
 * credential that exists on the server, has no plaintext copy anywhere, and was never shown to anyone.
 * Narrowing a display field must not be able to do that. So `apiKey` is narrowed first and hard, the
 * device is reduced to the two fields the reveal titles itself with plus the public key id, and
 * whatever could not be read becomes `deviceDrift` — a caveat rendered beside the token rather than
 * instead of it. The authoritative row arrives from `listDevices` a moment later regardless.
 */
function toDeviceKeyIssued(row: Row, what: string): DeviceKeyIssued {
  // First, and outside every best-effort branch below: this is the field the reply exists for.
  const apiKey = reqStr(row, "apiKey", what);
  // `reqStr` narrows the type and not the value, and `""` is a string. This server cannot send it —
  // `DeviceKey.Issue()` always builds an 85-character token and `DeviceKeyIssuedDto.ApiKey` is
  // non-nullable — so this is a guard against a proxy or a future non-EAMS server, not against today's
  // backend. It is here rather than in a `reqNonEmptyStr` helper because an empty string is a perfectly
  // good value for every other field in this file; it is only the *credential* that must not be blank.
  if (apiKey === "") throw offContract(what, "`apiKey` should be a token, got an empty string");

  const nested: unknown = row.device;
  if (!isRow(nested)) {
    return {
      apiKey,
      device: {},
      deviceDrift:
        `The key below is valid, but this build could not read the device details that came with ` +
        `it (\`device\` was ${describeType(nested)}). Find the device by its key id in the list.`,
    };
  }

  const device: IssuedKeyDevice = {
    id: optStr(nested.id),
    name: optStr(nested.name),
    apiKeyId: optStr(nested.apiKeyId),
  };

  // `apiKeyId` is absent from this list on purpose: the contract already allows it to be null, and the
  // dialog says "not reported" for it without that being drift.
  const missing = (["id", "name"] as const).filter((field) => device[field] === undefined);

  return {
    apiKey,
    device,
    deviceDrift:
      missing.length === 0
        ? undefined
        : `The key below is valid, but this build could not read every detail of the device it was ` +
          `issued for (missing: ${missing.join(", ")}). Find the device by its key id in the list.`,
  };
}

function toStudentGroup(row: Row, what: string): StudentGroup {
  return {
    id: reqStr(row, "id", what),
    name: reqStr(row, "name", what),
    type: reqStr(row, "type", what),
    sourceType: reqStr(row, "sourceType", what),
    sourceEntityType: reqStr(row, "sourceEntityType", what),
    // `optStr` and not `reqStr`, matching the contract exactly: these three are nullable on a
    // `Manual` group, which spans terms by nature and which the projection never touches. Narrowing
    // them as required would make the manual half of a perfectly healthy list fail the whole read.
    termId: optStr(row.termId),
    termCode: optStr(row.termCode),
    memberCount: reqNum(row, "memberCount", what),
    lastSyncedAt: optStr(row.lastSyncedAt),
  };
}

function toTerm(row: Row, what: string): Term {
  return {
    id: reqStr(row, "id", what),
    code: reqStr(row, "code", what),
    schoolYear: reqStr(row, "schoolYear", what),
    semester: reqStr(row, "semester", what),
    isCurrent: reqBool(row, "isCurrent", what),
    // Dates, not instants, and nullable in the contract — the roster source has no term date columns,
    // so these are absent on most real rows.
    startsOn: optStr(row.startsOn),
    endsOn: optStr(row.endsOn),
  };
}

function toAudienceGroup(row: Row, what: string): EventAudienceGroup {
  return {
    studentGroupId: reqStr(row, "studentGroupId", what),
    name: reqStr(row, "name", what),
    type: reqStr(row, "type", what),
    sourceType: reqStr(row, "sourceType", what),
    termId: optStr(row.termId),
    termCode: optStr(row.termCode),
    memberCount: reqNum(row, "memberCount", what),
  };
}

function toAudienceStudent(row: Row, what: string): EventAudienceStudent {
  return {
    studentId: reqStr(row, "studentId", what),
    studentNumber: reqStr(row, "studentNumber", what),
    fullName: reqStr(row, "fullName", what),
    section: optStr(row.section),
  };
}

function toAudience(row: Row, what: string): EventAudience {
  return {
    eventId: reqStr(row, "eventId", what),
    status: reqStr(row, "status", what),
    isFrozen: reqBool(row, "isFrozen", what),
    expected: reqNum(row, "expected", what),
    groups: asRows(row.groups, `${what}.groups`).map((group, i) =>
      toAudienceGroup(group, `${what}.groups[${i}]`),
    ),
    // Required, and empty is a legitimate answer rather than a missing one — on a terminal event it
    // is the *expected* answer (ADR-003 D-13; see `EventAudience`). A missing key would still fail
    // here, which is the distinction worth keeping: "the server said none" is not "the server said
    // nothing".
    students: asRows(row.students, `${what}.students`).map((student, i) =>
      toAudienceStudent(student, `${what}.students[${i}]`),
    ),
  };
}

function toAudienceResult(row: Row, what: string): EventAudienceResult {
  return {
    eventId: reqStr(row, "eventId", what),
    groupsAttached: reqNum(row, "groupsAttached", what),
    studentsAttached: reqNum(row, "studentsAttached", what),
    groupsAlreadyAttached: reqNum(row, "groupsAlreadyAttached", what),
    studentsAlreadyAttached: reqNum(row, "studentsAlreadyAttached", what),
    expected: reqNum(row, "expected", what),
    warnings: reqStrs(row, "warnings", what),
  };
}

/**
 * `SisImportBatchDto`. Every counter is `reqNum` and `countersReconcile` is `reqBool`, deliberately.
 *
 * They are what the results screen *is*: an import that reported "37 succeeded, 3 failed" with an
 * undefined somewhere in it would render "NaN" or a blank beside a roster that has just been rewritten,
 * and the operator's only way to check the write is the numbers. Failing loud at the seam is the
 * alternative to a screenful of confident nonsense.
 *
 * `countersReconcile` is read rather than computed here — see the field's own note in `types.ts`.
 */
function toImportBatch(row: Row, what: string): SisImportBatch {
  return {
    id: reqStr(row, "id", what),
    termId: reqStr(row, "termId", what),
    termCode: reqStr(row, "termCode", what),
    source: reqStr(row, "source", what),
    // Nullable in the contract: a batch can be created by a path that had no file (§10 leaves the
    // source open), so an absent name is an ordinary value rather than drift.
    fileName: optStr(row.fileName),
    sourceSheetName: optStr(row.sourceSheetName),
    fileHash: optStr(row.fileHash),
    status: reqStr(row, "status", what),
    totalRows: reqNum(row, "totalRows", what),
    insertedRows: reqNum(row, "insertedRows", what),
    updatedRows: reqNum(row, "updatedRows", what),
    failedRows: reqNum(row, "failedRows", what),
    skippedRows: reqNum(row, "skippedRows", what),
    warningRows: reqNum(row, "warningRows", what),
    // Null until the run starts and until it finishes — which is exactly the state a staged batch is
    // in, so this is the ordinary case rather than the exception.
    startedAt: optStr(row.startedAt),
    finishedAt: optStr(row.finishedAt),
    countersReconcile: reqBool(row, "countersReconcile", what),
  };
}

function toImportRowEntity(row: Row, what: string): SisImportRowEntity {
  return {
    entityType: reqStr(row, "entityType", what),
    entityId: reqStr(row, "entityId", what),
    action: reqStr(row, "action", what),
  };
}

/**
 * `SisImportRowDto` — **minus `rawData`, which is not read**.
 *
 * That is a decision about this surface rather than about this mapper: `rawData` is the source line as
 * the workbook held it, for every student in the institution, and §10 is open (ADR-001 D-6). Not
 * reading it means it cannot be rendered, cannot reach a console line, and is not sitting in a heap
 * snapshot of the tab. The row number is what an operator actually needs — it is the line they open the
 * workbook and jump to — and the server's own `errorMessage` / `skipReason` is the *why*.
 */
function toImportRow(row: Row, what: string): SisImportRow {
  return {
    id: reqStr(row, "id", what),
    rowNumber: reqNum(row, "rowNumber", what),
    result: reqStr(row, "result", what),
    skipReason: optStr(row.skipReason),
    warningCode: optStr(row.warningCode),
    warningMessage: optStr(row.warningMessage),
    errorMessage: optStr(row.errorMessage),
    studentId: optStr(row.studentId),
    entities: asRows(row.entities, `${what}.entities`).map((entity, i) =>
      toImportRowEntity(entity, `${what}.entities[${i}]`),
    ),
  };
}

function toImportPreview(row: Row, what: string): SisImportPreview {
  return {
    batch: toImportBatch(asRow(row.batch, `${what}.batch`), `${what}.batch`),
    // Required, and an empty array is a legitimate answer that this client says something about: a
    // workbook whose header row did not parse is the first thing the preview exists to show.
    columns: reqStrs(row, "columns", what),
    distinctStudents: reqNum(row, "distinctStudents", what),
    distinctColleges: reqNum(row, "distinctColleges", what),
    distinctPrograms: reqNum(row, "distinctPrograms", what),
    distinctCourses: reqNum(row, "distinctCourses", what),
    distinctSections: reqNum(row, "distinctSections", what),
    distinctInstructors: reqNum(row, "distinctInstructors", what),
    blankSectionRows: reqNum(row, "blankSectionRows", what),
    placeholderInstructorRows: reqNum(row, "placeholderInstructorRows", what),
    sampleRows: asRows(row.sampleRows, `${what}.sampleRows`).map((sample, i) =>
      toImportRow(sample, `${what}.sampleRows[${i}]`),
    ),
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

/**
 * `POST /students` — §6.2. Manual roster entry, alongside the §10 bulk import.
 *
 * The refusals worth knowing: **400** for a §4.3 field rule, and **409** for a student number already
 * in use in this school. The 409 is not a validation failure and the split is deliberate — the request
 * is entirely well formed and would be accepted a moment after the conflicting row is renamed or
 * deleted, which is what 409 is for. The server's `detail` says which number and, when the holder is
 * soft-deleted, that the index is not filtered on `IsDeleted` so the number is still taken.
 *
 * A **400 with `code: "FieldIsDerived"`** is the one this client should never be able to earn:
 * `course`, `yearLevel` and `section` are the ADR-001 D-2 cache and the contract captures unknown
 * members specifically so a supplied one is refused by name rather than dropped. `StudentWriteRequest`
 * types those three `never`, so a body carrying one does not compile — see the type's own note.
 */
async function createStudent(request: StudentWriteRequest): Promise<Student> {
  return writeJson(
    "POST /students",
    "/students",
    { method: "POST", payload: request },
    "The student was created",
    toStudent,
  );
}

/**
 * `PUT /students/{id}` — §6.2, and a **full replacement** of the student's own editable fields. That
 * is why `Student` reads the name parts, `gender` and `photoUrl`: a field the caller cannot read back
 * is a field it cannot send back unchanged, and here two of them are NOT NULL columns.
 *
 * Same body as create and the same refusals, plus **404** for a student that is not there or is
 * soft-deleted. Unlike `PUT /events/{id}` there is no status-lock 409: a student has no state that
 * forbids an edit.
 */
async function updateStudent(id: string, request: StudentWriteRequest): Promise<Student> {
  return writeJson(
    "PUT /students/{id}",
    `/students/${encodeURIComponent(id)}`,
    { method: "PUT", payload: request },
    "The change was saved",
    toStudent,
  );
}

/**
 * `DELETE /students/{id}` — §6.2, and **soft** (§4.3 `IsDeleted`).
 *
 * Two consequences the confirmation has to say out loud, because neither is guessable and both are
 * recorded on `StudentService.DeleteAsync`:
 *
 *   - **the student's cards are left active on purpose.** Deactivating them here would rewrite the
 *     issuance history ADR-001 D-3 exists to preserve, on an operation nobody asked for. They cannot
 *     record attendance — the tap path resolves through `!Student.IsDeleted` — but the UID keeps its
 *     slot in the active-card unique index, so re-issuing that physical card to someone else needs an
 *     explicit detach first.
 *   - **the student number stays taken.** The uniqueness index is not filtered on `IsDeleted`, and
 *     §6.2 defines no endpoint that restores a student.
 *
 * Returns `void`: the server answers 204 with no body.
 */
async function deleteStudent(id: string): Promise<void> {
  return writeNoContent("DELETE /students/{id}", `/students/${encodeURIComponent(id)}`, {
    method: "DELETE",
  });
}

/**
 * `POST /students/{id}/cards` — §6.2, assigning an RFID card.
 *
 * The UID is normalised **before it gets here** (`normalizeCardUid`), so that what the form previewed,
 * what this compares against the student's existing cards and what the index defends are one value.
 * The server normalises again; that is a guarantee, not a reason to send the raw reading.
 *
 * **409 `CardUidInUse`** when the UID is already active on another student in this school — one UID
 * identifies one student at a time, and the server's `detail` says to deactivate the existing card
 * first. Re-attaching a card the *same* student already holds is not a conflict: the server answers
 * 201 with the card it already had, which is why the form says so before the press rather than leaving
 * a success that appears to have done nothing.
 *
 * The reply is the `CardDto`, narrowed with the same `toCard` the student read uses.
 */
async function addStudentCard(id: string, request: StudentCardRequest): Promise<Card> {
  return writeJson(
    "POST /students/{id}/cards",
    `/students/${encodeURIComponent(id)}/cards`,
    { method: "POST", payload: request },
    "The card was assigned",
    toCard,
  );
}

/**
 * `DELETE /students/{id}/cards/{cardId}` — §6.2, and it **deactivates rather than deletes**.
 *
 * The row survives with `isActive: false`, because ADR-001 D-3's whole point is that a past tap keeps
 * resolving to the physical card that produced it. So this is not the mirror image of attaching: the
 * card stays in `Student.cards` afterwards, and a screen asking "what does this student tap with" has
 * to filter on `isActive` rather than take the first row.
 *
 * 204 whether or not the card was still active — the postcondition holds either way, so a repeat is
 * safe. A card that belongs to another student is a **404**, deliberately: this URL claims it belongs
 * to this one, and deactivating somebody else's through it would be a silent cross-roster edit.
 */
async function removeStudentCard(id: string, cardId: string): Promise<void> {
  return writeNoContent(
    "DELETE /students/{id}/cards/{cardId}",
    `/students/${encodeURIComponent(id)}/cards/${encodeURIComponent(cardId)}`,
    { method: "DELETE" },
  );
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

// ---------------------------------------------------------------------------------------------
// Devices — §6.6, and the two replies in this file that carry a credential
// ---------------------------------------------------------------------------------------------
//
// `POST /devices/{id}/heartbeat` is deliberately NOT here, for the same reason `tap` is not wired: it
// is authenticated by the `DeviceKey` scheme, which is a credential the admin SPA does not hold and
// must not be given. It is a device saying "I am alive", not an administrator's action, and there is
// nothing on this surface that could sensibly call it.

/**
 * `GET /devices` — every registered device in this school.
 *
 * **Not routed through `listAll`, and that is a fact about the endpoint rather than an omission.**
 * `DevicesController.List` answers a bare `DeviceDto[]`, not the `<Dto>PagedResult` envelope the
 * §6.2/§6.3 admin lists now use, so asking it for `?page=` would send parameters it ignores and then
 * fail to find `items` on the reply. Devices are counted in tens — one per door — so there is nothing
 * here for `MAX_LIST_ROWS` to protect against; if that ever stops being true it is the *server* that
 * needs the envelope first.
 *
 * The reply carries no key material. See `toDevice`.
 */
async function listDevices(): Promise<Device[]> {
  const what = "GET /devices";
  const body = await getJson(what, "/devices");
  // Sorted by name server-side (`OrderBy(d => d.Name)`) and taken as it comes: unlike the events list
  // there is no client ordering to impose, so re-sorting here would only be a second opinion.
  return asRows(body, what).map((row, i) => toDevice(row, `${what}[${i}]`));
}

// `GET /devices/{id}` is deliberately NOT wrapped here. It exists on the server, but nothing in this
// SPA reads one device: the grid is fed by `listDevices` and every write answers with the row it
// changed. A second read path over `DeviceDto` would be a second place for a future `key` field on a
// read reply to be picked up — and this slice's whole credential story rests on there being exactly
// two mappers that touch device JSON. Add it back when a screen needs it, not before.

/**
 * `POST /devices` — §6.6. Registers the device **and mints its first key in the same call**; there is
 * no separate "issue a key" endpoint, because a device with no credential is not yet a device that can
 * do anything.
 *
 * **The 201 body is the only time that key is ever readable.** The server keeps `SHA-256(secret)` and
 * nothing else, so there is no "show it again" endpoint to write and none to forget to protect. Every
 * caller of this method owes the operator a one-shot reveal they cannot dismiss by accident, and must
 * not log, cache or re-render the token anywhere else. An operator who loses it rotates.
 *
 * The refusals: **400** for a §4.10 field rule (name required and ≤ 100, `deviceType` outside the
 * three, reader model too long) and **409** for either no resolvable school or — vanishingly unlikely,
 * and reported rather than retried precisely so someone sees it — a collision on the generated key id.
 */
async function registerDevice(request: DeviceWriteRequest): Promise<DeviceKeyIssued> {
  return writeJson(
    "POST /devices",
    "/devices",
    { method: "POST", payload: request },
    "The device was registered and a key was issued",
    toDeviceKeyIssued,
  );
}

/**
 * `PUT /devices/{id}` — §6.6, and a **full replacement** of the device's own fields, which is why
 * `Device` reads all four: a field this client cannot read back is a field it cannot send back
 * unchanged.
 *
 * **It never touches the key.** `DeviceService.UpdateAsync` leaves every `ApiKey*` column alone, so
 * clearing `isActive` stops the device authenticating without revoking anything — ticking it again
 * restores the same key. That is the difference between "this kiosk is out of service" and "this
 * credential is burned", and the two are deliberately not collapsed.
 *
 * Refusals: **400** for the same field rules as register, and **404** for a device that is not there.
 */
async function updateDevice(id: string, request: DeviceWriteRequest): Promise<Device> {
  return writeJson(
    "PUT /devices/{id}",
    `/devices/${encodeURIComponent(id)}`,
    { method: "PUT", payload: request },
    "The change was saved",
    toDevice,
  );
}

/**
 * `POST /devices/{id}/regenerate-key` — §6.6, and a **hard cut with no overlap window**. The previous
 * token stops working the instant this returns: the new key overwrites the old one in the same row,
 * and there is no second key column for a grace period to live in.
 *
 * So the device is offline from this moment until someone types the new token into it. A caller must
 * say that *before* the press, not after.
 *
 * It also clears `ApiKeyRevokedAt`, which makes rotation the only way back for a device whose key was
 * revoked.
 *
 * Same one-shot rule as register: the 200 body is the only time this token is readable.
 *
 * Refusals: **404** for a device that is not there, **409** on a key-id collision.
 */
async function regenerateDeviceKey(id: string): Promise<DeviceKeyIssued> {
  return writeJson(
    "POST /devices/{id}/regenerate-key",
    `/devices/${encodeURIComponent(id)}/regenerate-key`,
    { method: "POST", payload: {} },
    "A new key was issued and the previous one stopped working",
    toDeviceKeyIssued,
  );
}

/**
 * `POST /devices/{id}/revoke-key` — burn the credential **without** issuing a replacement.
 *
 * Not defined by §6.6 and added deliberately (recorded as drift): regeneration alone conflates "this
 * key is compromised" with "give me a working one", while the published mobile contract already tells
 * the two apart on the wire — `401` for a bad key, `403` for a revoked one. Without this route that
 * 403 is unreachable.
 *
 * **Idempotent**, which is unusual on this surface and is the point: revoking twice, or revoking a
 * device that never held a key, is a 200. The postcondition — this device cannot authenticate — holds
 * either way, so a retry after a timeout is safe on the one operation an operator runs when something
 * has already gone wrong. `advise()` still withholds the one-click resend, because `shape` is a proxy
 * for idempotency and is exact only for POST; it errs safe.
 *
 * The reply is the `DeviceDto`, so the row's standing updates without a re-read. Refusals: **404**.
 */
async function revokeDeviceKey(id: string): Promise<Device> {
  return writeJson(
    "POST /devices/{id}/revoke-key",
    `/devices/${encodeURIComponent(id)}/revoke-key`,
    { method: "POST", payload: {} },
    "The key was revoked",
    toDevice,
  );
}

// ---------------------------------------------------------------------------------------------
// The audience — who an event expects
// ---------------------------------------------------------------------------------------------

/**
 * `GET /events/{id}/attendees` — the sections and the individually-attached students on one event.
 *
 * `undefined` for a 404, like the other three lookups: an event that is not there is an answer the
 * panel renders, not a failure.
 *
 * **What an empty `students` means depends on `isFrozen`, and the difference is not cosmetic.** On a
 * live event it means nobody is individually attached. On a terminal one it is the *expected* answer:
 * ADR-003 D-13 writes the resolved audience down as individual `EventGroups` student rows and those
 * rows are the denominator, but this endpoint does not republish them — the per-student frozen set is
 * `GET /events/{id}/roster`. A panel that read the empty array as "no audience" would say so directly
 * beneath a non-zero `expected`.
 */
function toScan(row: Row, what: string): EventScan {
  return {
    cardUid: reqStr(row, "cardUid", what),
    scannedAt: reqStr(row, "scannedAt", what),
    recordedAt: reqStr(row, "recordedAt", what),
    deviceTapId: optStr(row.deviceTapId),
    deviceId: optStr(row.deviceId),
    serverOutcome: reqStr(row, "serverOutcome", what),
    // Optional on purpose: most rows will not carry one. A device that never sends it is not
    // misbehaving, and an absent claim must not be read as a claim of absence.
    localOutcome: optStr(row.localOutcome),
  };
}

function toScanLog(row: Row, what: string): EventScanLog {
  return {
    eventId: reqStr(row, "eventId", what),
    totalScans: reqNum(row, "totalScans", what),
    distinctCards: reqNum(row, "distinctCards", what),
    // Empty is the ordinary answer and a good one - it means every card presented at this event
    // resolved to somebody. A missing key would still fail here, which is the distinction to keep:
    // "the server said none" is not "the server said nothing".
    scans: asRows(row.scans, `${what}.scans`).map((scan, i) => toScan(scan, `${what}.scans[${i}]`)),
  };
}

/**
 * `GET /events/{id}/scans` - scans at this event that resolved to no student.
 *
 * The roster's complement: a scan whose card resolves is an attendance row and is reported there, so
 * read together the two account for every tap the server accepted. This is the population an
 * attendance table structurally cannot hold, because a row there needs a student and these scans
 * have none.
 *
 * **The one endpoint in this API that requires being signed in.** Everything else is still open under
 * ADR-001 D-6. A 401 here therefore means the session expired rather than that the app is
 * misconfigured, and it is the only call where that is currently true.
 */
async function getEventScans(eventId: string): Promise<EventScanLog | undefined> {
  const what = "GET /events/{id}/scans";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(eventId)}/scans`);
  return body === undefined ? undefined : toScanLog(asRow(body, what), what);
}

async function getEventAudience(eventId: string): Promise<EventAudience | undefined> {
  const what = "GET /events/{id}/attendees";
  const body = await getJsonOrMissing(what, `/events/${encodeURIComponent(eventId)}/attendees`);
  return body === undefined ? undefined : toAudience(asRow(body, what), what);
}

/**
 * `POST /events/{id}/attendees` — §6.3, and **idempotent**, which is what makes it the one write on
 * this screen a user can safely be told to send again.
 *
 * Two filtered unique indexes (ADR-003 D-12) make that a property of the schema rather than of the
 * service remembering to check, so two concurrent posts of the same sections cannot both land. The
 * reply says which half happened: `groupsAttached` versus `groupsAlreadyAttached`, and
 * **"already attached" is a success, not a refusal** — see `EventAudienceResult`.
 *
 * The refusals: **400** for a malformed or cross-school reference, **404** for an event that is not
 * there, and **409** when the event's status forbids an audience change. The 409 is the one worth
 * planning for: `eventAudience.ts` disables the controls on a terminal event, but the event can be
 * closed in another tab between the read and the press, and that arrival must read as what it is
 * rather than as a generic write failure.
 *
 * A group from a **non-current term warns rather than refuses**, so a 200 can still carry something
 * the organizer needs to see. `warnings` is required at the seam for that reason.
 */
async function attachEventAudience(
  eventId: string,
  request: EventAudienceRequest,
): Promise<EventAudienceResult> {
  return writeJson(
    "POST /events/{id}/attendees",
    `/events/${encodeURIComponent(eventId)}/attendees`,
    { method: "POST", payload: request },
    "The audience was changed",
    toAudienceResult,
  );
}

/**
 * `DELETE /events/{id}/attendees/groups/{studentGroupId}` — detach one section.
 *
 * **204 whether or not it was attached**, deliberately: the postcondition holds either way, so a
 * retry is safe — which is the opposite of `DELETE /students/{id}` and worth not copying the wrong
 * habit from. A missing *event* is still 404, because that one is named by the URL; a locked event is
 * 409.
 *
 * Sub-resource `DELETE`s rather than a body on `DELETE /attendees`: a body on `DELETE` is legal and
 * is dropped by enough proxies to be a poor contract. `RequestBody` in this file makes the bodyless
 * shape the only representable one for the verb, so this cannot drift back.
 */
async function detachEventGroup(eventId: string, studentGroupId: string): Promise<void> {
  return writeNoContent(
    "DELETE /events/{id}/attendees/groups/{studentGroupId}",
    `/events/${encodeURIComponent(eventId)}/attendees/groups/${encodeURIComponent(studentGroupId)}`,
    { method: "DELETE" },
  );
}

/** `DELETE /events/{id}/attendees/students/{studentId}` — same semantics as the group detach above. */
async function detachEventStudent(eventId: string, studentId: string): Promise<void> {
  return writeNoContent(
    "DELETE /events/{id}/attendees/students/{studentId}",
    `/events/${encodeURIComponent(eventId)}/attendees/students/${encodeURIComponent(studentId)}`,
    { method: "DELETE" },
  );
}

/**
 * `GET /student-groups` — the audiences an event can be attached to. Every filter optional.
 *
 * Unwrapped at the seam like every other admin list, and **the paging here is not decoration**. The
 * contract records the argument it lost: a school's group count looked bounded by its academic
 * structure, which is true of *one term* — the ADR-001 D-1 projection writes a fresh row per section
 * and per offering on every term it runs for, so the real bound is per-term multiplied by every term
 * ever imported. `listAll` walks it and `MAX_LIST_ROWS` refuses loudly rather than silently handing a
 * picker the first fifty rows of several semesters.
 *
 * **`sourceType` outside the set returns an empty list, not every row** — a mistyped filter that
 * silently stops filtering is how a cohort-only flow ends up offering manual groups. `GROUP_SOURCE_TYPE`
 * is what the one caller passes, so the value is never hand-typed.
 *
 * There is no `type=` filter on the wire; selecting Sections is `sectionChoices`' client-side job.
 */
async function listStudentGroups(filter?: {
  sourceType?: string;
  termId?: string;
}): Promise<StudentGroup[]> {
  const what = "GET /student-groups";
  return listAll(
    what,
    "/student-groups",
    { sourceType: filter?.sourceType, termId: filter?.termId },
    toStudentGroup,
  );
}

/**
 * `GET /academic/terms` — every term, **current first and then newest first**, which is the ordering
 * `sectionChoices` leans on for its fallback rather than sorting on `startsOn`. Those columns are
 * frequently null (the roster source has no term dates at all), so sorting on them would produce an
 * arbitrary list on real data.
 */
async function listTerms(): Promise<Term[]> {
  const what = "GET /academic/terms";
  return listAll(what, "/academic/terms", {}, toTerm);
}

/**
 * `POST /academic/terms` — D-53, and **the only three writes anywhere under `/academic`**.
 *
 * Colleges, programs, courses and offerings stay read-only because the importer owns them and matches
 * them by natural key, so a hand-authored row there is silently overwritten by the next run. A term
 * is the one academic row a human authors rather than imports — it has no source in the roster file
 * and every import requires one to exist first — which is why the read-only rule that protects the
 * importer-owned tables does not apply to it.
 *
 * Until this existed, `CLAUDE.md` documented hand-written `INSERT INTO dbo.Terms` SQL as the only way
 * to create one, and a database carried over from before the seed row existed left the roster-import
 * page with an empty picker that refuses to stage a batch.
 *
 * **The created term is never current.** `TermWriteRequest` carries no `isCurrent` at all; moving
 * that flag is `setTermCurrent` below.
 *
 * The refusals worth knowing: **400** for a blank, over-length, whitespace-padded field or dates that
 * run backwards, and **409** for *two different things* — `TermCodeExists` when the code is taken,
 * and `NoSchoolResolved` when there is no school row to file the term under. `termDraft.ts` tests
 * those on the `code` extension rather than on the status, because putting the second one on the code
 * field would send an operator to rename a code that was never the problem.
 */
async function createTerm(request: TermWriteRequest): Promise<Term> {
  return writeJson(
    "POST /academic/terms",
    "/academic/terms",
    { method: "POST", payload: request },
    "The term was created",
    toTerm,
  );
}

/**
 * `PUT /academic/terms/{id}` — a **full replacement** of the term's authored fields, and `code` is
 * among them: fixing a typo in a code is the most likely edit anyone makes here.
 *
 * What a rename does *not* rewrite, worth knowing before offering it: derived `StudentGroup` display
 * names embed the code at projection time (`"BSFS 2-A (2025-2026-1)"`) and keep the old text until
 * the next import re-runs the projection. That is stale wording rather than a wrong audience, since
 * groups resolve on ids.
 *
 * It does not move the current-term flag — see `setTermCurrent`. Refusals: **400** as above, **404**
 * for a term that is not there, **409** for renaming onto another term's code.
 */
async function updateTerm(id: string, request: TermWriteRequest): Promise<Term> {
  return writeJson(
    "PUT /academic/terms/{id}",
    `/academic/terms/${encodeURIComponent(id)}`,
    { method: "PUT", payload: request },
    "The change was saved",
    toTerm,
  );
}

/**
 * `PATCH /academic/terms/{id}/current` — the only door into `IsCurrent`, for the same reason
 * `PATCH /events/{id}/status` is the only door into an event's status.
 *
 * `IsCurrent` is not a property of the row, it is a claim about the school: a filtered unique index
 * (`UNIQUE(SchoolId) WHERE IsCurrent = 1`) caps it at one term, so setting it **clears whichever term
 * holds it** — a two-row transaction rather than a field write. That consequence is the one this
 * client has to state before the press, which `currentTermConsequence` in `termDraft.ts` composes.
 *
 * **`isCurrent` is a required parameter rather than a defaulted one, and that is load-bearing.** The
 * server treats a missing member as a `400` rather than binding it to `false`, precisely because a
 * client that forgot the field would otherwise quietly retire the school's term and get a `200` for
 * it. Taking a `boolean` here makes the omission unrepresentable one level earlier.
 *
 * `false` is the whole of "retiring a term": D-53 offers **no deletion**, because a term with a batch
 * imported against it cannot be removed without data loss. Retiring leaves the school with no current
 * term, which is an ordinary state the reads already answer for.
 *
 * Idempotent — setting a term that is already current, or clearing one that is not, changes nothing
 * and answers 200. `advise()` still classifies a network failure here as `may-duplicate`, because
 * `shape` is a proxy for idempotency and is exact only for POST; it errs safe.
 */
async function setTermCurrent(id: string, isCurrent: boolean): Promise<Term> {
  return writeJson(
    "PATCH /academic/terms/{id}/current",
    `/academic/terms/${encodeURIComponent(id)}/current`,
    { method: "PATCH", payload: { isCurrent } },
    isCurrent ? "The current term was moved" : "The term was retired",
    toTerm,
  );
}

/**
 * Which term a section list was read for — and, when it is not the one that was asked for, the fact
 * that says the rows already fetched have to be thrown away.
 *
 * `"requested"` and `"default"` are separate arms rather than one `term` field precisely because the
 * caller must act differently: rows read for a term that turned out not to exist belong to nothing
 * the picker can label, and quietly showing them under the fallback's heading is a confident wrong
 * answer of the kind this whole term-scoping exists to prevent.
 */
export type TermResolution =
  | { kind: "requested"; term: Term }
  | { kind: "default"; term: Term }
  | { kind: "none" };

/**
 * The term a picker should be showing, given every term and whatever it asked for.
 *
 * Pure and exported so the rule is testable without a `fetch`: it is the only branching decision in
 * this module, and every arm of it is reachable in ordinary use (first open, a chosen term, a term
 * deleted or renamed under an open dialog, a school with no terms at all).
 *
 * The fallback order is `isCurrent`, then the first row — **not** a sort on `startsOn`. The contract
 * orders this list current-first then newest-first for a stated reason: the roster source has no term
 * date columns, so `startsOn`/`endsOn` are frequently null and sorting on them produces an arbitrary
 * list on real data. `isCurrent` is carried on the row so a picker need not make a second request to
 * `GET /academic/terms/current` to find it, and at most one term can hold it — a filtered unique
 * index, not a convention.
 */
export function resolveTerm(
  terms: readonly Term[],
  requestedId: string | undefined,
): TermResolution {
  if (requestedId !== undefined) {
    const requested = terms.find((term) => term.id === requestedId);
    if (requested !== undefined) return { kind: "requested", term: requested };
  }
  const fallback = terms.find((term) => term.isCurrent) ?? terms[0];
  return fallback === undefined ? { kind: "none" } : { kind: "default", term: fallback };
}

/** What the audience picker chooses from, for one term. */
export interface SectionChoices {
  /** Every term, so the picker can offer a different one. Current first, then newest first. */
  terms: Term[];
  /** The term `sections` was read for — `undefined` only when the school has no terms at all. */
  term: Term | undefined;
  /** That term's derived Section groups, with the member counts the organizer decides on. */
  sections: StudentGroup[];
}

/**
 * The picker's whole source, settled together and **term-scoped by construction**.
 *
 * The scoping is the point rather than an optimisation. A section name is reused every semester
 * against an entirely different cohort, so an unscoped list holds several distinct sets of students
 * under names differing only by the term suffix the projection composes in — and picking the wrong
 * one is invisible until the event closes against a denominator of the wrong people. `termId` is the
 * filter the contract calls the one most worth passing, and this is the only method that reads groups.
 *
 * The terms read comes first when no term has been chosen yet, because the default *is* the answer to
 * that read: `isCurrent` is carried on the row precisely so a picker does not need a second request
 * to `GET /academic/terms/current` to find it. Once the caller names a term the two reads are
 * independent and run together, so changing term costs one round of latency rather than two.
 *
 * A `termId` that is not in the list falls back to the default rather than being passed through. It
 * can only happen through version skew or a term deleted under an open dialog, and sending it anyway
 * would answer with an empty section list that reads as "this term has no sections" — a confident
 * wrong answer where the fallback is a visibly different term the user can see they are looking at.
 *
 * Sections are selected here rather than on the wire because `GET /student-groups` publishes no
 * `type=` filter. Filtering client-side over one term's rows is bounded by the scoping above, so it
 * is not the lossy client-side-filter trap `listStudents`' `course` note warns about.
 */
async function sectionChoices(termId: string | undefined): Promise<SectionChoices> {
  if (termId === undefined) {
    const terms = await listTerms();
    const resolved = resolveTerm(terms, undefined);
    if (resolved.kind === "none") return { terms, term: undefined, sections: [] };
    return { terms, term: resolved.term, sections: await sectionsIn(resolved.term.id) };
  }

  const [terms, sections] = await Promise.all([listTerms(), sectionsIn(termId)]);
  const resolved = resolveTerm(terms, termId);

  if (resolved.kind === "none") return { terms, term: undefined, sections: [] };
  // The requested term does not exist, so the rows just read belong to a term the picker cannot name.
  // They are **dropped and re-read** under the fallback rather than relabelled: a list of sections
  // under the wrong term heading is the exact confusion the scoping exists to prevent, and it is the
  // confusion that does not announce itself.
  if (resolved.kind === "default") {
    return { terms, term: resolved.term, sections: await sectionsIn(resolved.term.id) };
  }
  return { terms, term: resolved.term, sections };
}

/** One term's derived Section groups. Split out only so `sectionChoices` reads as its own decision. */
async function sectionsIn(termId: string): Promise<StudentGroup[]> {
  const groups = await listStudentGroups({ sourceType: GROUP_SOURCE_TYPE.Derived, termId });
  return groups.filter((group) => group.type === GROUP_TYPE_SECTION);
}

// ---------------------------------------------------------------------------------------------
// The SIS roster import — §10, and the only multipart request this seam makes
// ---------------------------------------------------------------------------------------------
//
// Two steps, on purpose and irreducibly: `upload` stages the workbook and writes **nothing** to the
// academic tables, `run` applies it. The preview between them is the point — it is where an operator
// sees what is in the file before it touches the roster of every student in the school — so nothing in
// this seam offers a combined call, and a caller that wants one would be removing the review, not
// saving a round trip.

/** The multipart part names `SisImportController.Upload` reads. Wrong names are a 400, not a 422. */
const UPLOAD_FILE_PART = "file";
const UPLOAD_TERM_PART = "termId";

/**
 * `POST /sis/import/upload` — stages a workbook and answers what is in it. **Writes nothing.**
 *
 * `multipart/form-data`, which is why `RequestBody` has a `form` arm; the `Content-Type` is left for
 * `fetch` to write, because the boundary is generated with the body. See `wireBodyOf`.
 *
 * **`termId` is operator input and is never inferred** (ADR-001 D-5). It is a required parameter here
 * rather than an optional one with a fallback for the reason the controller states: guessing it from
 * the filename or the upload date misfiles an entire batch in a way nothing downstream can detect,
 * because every downstream query is term-scoped and the data therefore looks perfectly fine.
 *
 * The two refusals are **different problems with different fixes**, and both reach the caller as an
 * `ApiError` carrying the status, so a screen can tell them apart (`sisImport.uploadRefusalOf`):
 *
 *   - **400** — the *request* is malformed: no file part, or no term. Something about how this build
 *     sent it, or a term that was never chosen.
 *   - **422** — the request is well-formed multipart and the *content* cannot be read: not a workbook,
 *     wrong columns, a `.csv` renamed. Re-sending the identical bytes gets the identical answer, which
 *     is exactly what separates it from the 400.
 *
 * The 201 body is a `SisImportPreviewDto`, narrowed by `writeJson` like every other write's reply.
 */
async function uploadRoster(file: File, termId: string): Promise<SisImportPreview> {
  const form = new FormData();
  form.append(UPLOAD_FILE_PART, file);
  form.append(UPLOAD_TERM_PART, termId);

  return writeJson(
    "POST /sis/import/upload",
    "/sis/import/upload",
    { method: "POST", form },
    // Staged, not imported — and the distinction is the whole design of this endpoint. The sentence
    // only ever surfaces on an unreadable 2xx reply, which is precisely the moment somebody needs to
    // know that whatever happened, the roster has not been touched.
    "The workbook was staged (nothing was written to the roster)",
    toImportPreview,
    // The one request on this seam that does not run on the read budget. See `UPLOAD_TIMEOUT_MS`.
    UPLOAD_TIMEOUT_MS,
  );
}

/**
 * `POST /sis/import/{batchId}/run` — applies a staged batch. This is the write.
 *
 * **Idempotent with respect to the database**: running the same roster twice leaves it identical and
 * reports every row `Skipped`. That is worth saying on screen, because the operator who does not know
 * it is the operator who will not re-run after a partial failure — which is the one time they should.
 *
 * `termId` is a **confirmation, and must match the batch's own or the run is refused with a 409**. It
 * is the caller's job to pass the term the operator chose at upload, carried forward, rather than one
 * re-read from the batch: a confirmation that asks the same source twice answers itself.
 *
 * The refusals: **400** for a missing term, **404** for a batch id that never existed — caught before
 * the general case on the server specifically so it is not confused with the next one — and **409**
 * for a batch whose state forbids the run, which is a term mismatch or a batch that has already run.
 */
async function runImport(batchId: string, request: SisImportRunRequest): Promise<SisImportBatch> {
  return writeJson(
    "POST /sis/import/{batchId}/run",
    `/sis/import/${encodeURIComponent(batchId)}/run`,
    { method: "POST", payload: request },
    "The import was run",
    toImportBatch,
  );
}

/**
 * `GET /sis/import/{batchId}` — one batch and its counters, without its rows.
 *
 * `undefined` for a 404, like the other lookups in this file: a batch id that is not there is an
 * answer a screen renders rather than a failure.
 */
async function getImportBatch(batchId: string): Promise<SisImportBatch | undefined> {
  const what = "GET /sis/import/{batchId}";
  const body = await getJsonOrMissing(what, `/sis/import/${encodeURIComponent(batchId)}`);
  return body === undefined ? undefined : toImportBatch(asRow(body, what), what);
}

/**
 * `GET /sis/import/{batchId}/rows` — a batch's staged rows, narrowed to one outcome.
 *
 * **`result: "Failed"` is the query this endpoint exists for**: an import reporting "37 succeeded, 3
 * failed" with no way to see which 3 is not something an operator can act on.
 *
 * **Not routed through `listAll`**, and that is a fact about the endpoint rather than an omission:
 * `SisImportController.Rows` answers a bare `SisImportRowDto[]`, not the `<Dto>PagedResult` envelope
 * the §6.2/§6.3 admin lists use, so asking it for `?page=` would send parameters it ignores and then
 * fail to find `items` on the reply.
 *
 * **`MAX_LIST_ROWS` still applies, and is enforced here rather than by the walk.** The reply is a whole
 * batch's rows in one array — a `Skipped` filter on a re-import matches *every* row by design, which is
 * the ordinary case rather than the pathological one — and the ceiling's own rule is that crossing it
 * is a design signal that fails loud. Truncating for display instead would be a second, quieter policy
 * for the same fact, applied after the whole array was already in memory and in React state: the
 * expensive half of loading everything, with the honesty of refusing removed.
 *
 * A `result` outside `SisImportRowResult.All` is the server's business, not this seam's; it passes
 * through and the reply is whatever the server decides that means.
 */
async function getImportRows(batchId: string, result?: string): Promise<SisImportRow[]> {
  const what = "GET /sis/import/{batchId}/rows";
  const body = await getJson(what, `/sis/import/${encodeURIComponent(batchId)}/rows`, { result });
  const rows = asRows(body, what);
  // Counted before the rows are narrowed: the refusal is about how many there are, and mapping them
  // first would spend the work this check exists to decline.
  if (rows.length > MAX_LIST_ROWS) {
    throw tooLarge(
      what,
      `this batch has ${rows.length} rows matching that filter and this screen shows at most ` +
        `${MAX_LIST_ROWS}`,
    );
  }
  return rows.map((row, i) => toImportRow(row, `${what}[${i}]`));
}

/**
 * Explanation of why an admin-browser tap is refused. One string, one place — the message the
 * Snackbar shows and the reason in the report are the same text.
 */
const TAP_UNAVAILABLE =
  "Simulated taps are disabled against the real API: POST /attendance/tap is the capture surface " +
  "and requires a DeviceKey, which the admin SPA deliberately does not hold.";

/**
 * What is said in a development build when nobody has pasted a key yet. Distinct from
 * `TAP_UNAVAILABLE` on purpose: that one describes a refusal with no remedy, this one is a refusal
 * whose remedy is a box on the screen the reader is already looking at.
 */
const TAP_NEEDS_DEVICE_KEY =
  "Simulated taps need a device key: set one on this page first. Register a device at /devices and " +
  "paste the token it shows once — the admin SPA holds no capture credential of its own.";

/** How the tap request names itself in a failure sentence. */
const TAP_WHAT = "The simulated tap";

/**
 * The idempotency key for a tap that has not yet been definitively answered, per event and card.
 *
 * **`deviceTapId` is the whole basis of §8.2's offline sync, and a retry must reuse it.** A fresh id
 * on the second press is what turns "the reply was lost" into a second attendance row; sending the
 * same one comes back `DuplicateIgnored` instead. That is what a real device does, and it is the
 * behaviour the endpoint exists to support — so the simulator has to do it too, or it simulates
 * something the mobile client will never send.
 *
 * `tappedAt` is minted with it and reused with it, for the reason the endpoint's own remarks give:
 * with a null `tappedAt` the instant is re-derived on every attempt, so a tap retried after the
 * event's window closed comes back `TappedAtOutsideEventWindow` rather than `DuplicateIgnored`.
 *
 * Entries are dropped once the server has answered in a way that settles this attempt: a 2xx this
 * build could read, or a refusal the contract calls final (see `tapIsAnsweredForGood`). Every other
 * ending — a network failure, a 5xx, a 401/403/408/429, a reply that will not parse — is precisely
 * the case where the id must survive into the retry.
 */
const pendingTaps = new Map<string, { deviceTapId: string; tappedAt: string }>();

/** A `|` cannot occur in a UUID or a normalized card UID, so the two halves cannot run together. */
const tapAttemptKey = (eventId: string, cardUid: string) => `${eventId}|${cardUid}`;

/** The 4xx statuses that mean "try again with the same bytes", per `AttendanceController.Tap`. */
const RETRYABLE_CLIENT_STATUSES = new Set([401, 403, 408, 429]);

/**
 * Whether the server answered in a way that ends *this* attempt for good — so the held `deviceTapId`
 * and `tappedAt` must be dropped rather than resent.
 *
 * `AttendanceController.Tap` documents its 400s and 404s as definitively answered: "the same request
 * will be refused forever". Keeping the attempt across one of those is not caution, it is a bug with
 * two faces. A 404 `CardNotFound`, once the card is registered and the button pressed again ten
 * minutes later, resends the ten-minute-old `tappedAt` — and `graceMinutes` decides Present versus
 * Late from `tappedAt`, so near the boundary that is a wrong status that looks right. A 400
 * `TappedAtOutOfRange` is worse: every retry resends the very timestamp that was just refused, and
 * the only escape is reloading the tab, which nothing on the screen says.
 *
 * The exceptions are the four 4xx the contract does *not* call final. 401/403 are credential states
 * that a paste on this page can change with the request otherwise unaltered; 408 and 429 are
 * explicitly "send it again" — and there, reusing the id is what stops a retry becoming a second row.
 * 5xx, a network failure and an unreadable 2xx are all "the server may have acted", which is the case
 * the held id exists for; none of them reach here.
 */
const tapIsAnsweredForGood = (status: number) =>
  status >= 400 && status < 500 && !RETRYABLE_CLIENT_STATUSES.has(status);

/**
 * `POST /attendance/tap`, in a **development build only**, using a key the developer pasted at
 * runtime — and the reason that is not the thing the note above refuses.
 *
 * The refusal stands for production and is what `tap()` still returns there. What it rules out is a
 * key that ships *in the bundle*: `import.meta.env.VITE_…` is inlined at build time, so the GitHub
 * Pages artefact would publish a write credential as a static asset. A runtime paste never reaches
 * the build, so it is never in the artefact — and this function, along with `deviceKey.ts` and the
 * panel that feeds it, is eliminated from the production build entirely, because
 * `import.meta.env.DEV` is a compile-time literal. That is verified against `dist/assets/*.js` rather
 * than assumed.
 */
async function simulateTapWithDeviceKey(
  eventId: string,
  cardUid: string,
): Promise<{ ok: boolean; message: string }> {
  const status = deviceKeyStatus();
  if (status.kind === "unreadable") return { ok: false, message: status.reason };
  if (status.kind !== "set") return { ok: false, message: TAP_NEEDS_DEVICE_KEY };

  const token = getDeviceKey();
  if (token === undefined) return { ok: false, message: TAP_NEEDS_DEVICE_KEY };

  // Typed as always present, and not always present: `crypto.randomUUID` is a secure-context API, so
  // a dev server reached over plain http on a LAN address has none. Said out loud rather than
  // silently substituting a weaker id — an idempotency key that can collide is worse than no tap.
  if (typeof crypto.randomUUID !== "function") {
    return {
      ok: false,
      message:
        "This browser exposes no `crypto.randomUUID`, so no idempotency key can be minted for the " +
        "tap. Open the dev server on localhost or over https.",
    };
  }

  const key = tapAttemptKey(eventId, cardUid);
  const attempt = pendingTaps.get(key) ?? {
    deviceTapId: crypto.randomUUID(),
    tappedAt: new Date().toISOString(),
  };
  pendingTaps.set(key, attempt);

  let res: Response;
  try {
    res = await fetch(buildUrl("/attendance/tap"), {
      method: "POST",
      headers: {
        Accept: JSON_MEDIA_TYPE,
        "Content-Type": JSON_MEDIA_TYPE,
        // The one Authorization header this SPA ever sends. Composed here, in the seam, from the
        // scheme `deviceKey.ts` owns — no component builds a request.
        Authorization: `${DEVICE_KEY_SCHEME} ${token}`,
      },
      // No `deviceId`: this build cannot know the device's id from the token (the key carries the
      // public key *id*, which is a different thing), and a wrong one is a 400 `DeviceMismatch`. The
      // server resolves the device from the key.
      body: JSON.stringify({
        eventId,
        cardUid,
        deviceTapId: attempt.deviceTapId,
        tappedAt: attempt.tappedAt,
      }),
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
  } catch (cause) {
    // A write, so "it was not sent" is a claim this cannot make — and the retry is safe *because* the
    // attempt above is kept, which is the sentence worth giving the reader.
    return {
      ok: false,
      message: isTimeout(cause)
        ? `${TAP_WHAT} went unanswered for ${REQUEST_TIMEOUT_MS} ms. It may still have been ` +
          "recorded; pressing Tap again sends the same deviceTapId, so it cannot double-record."
        : `Cannot reach the EAMS API at ${baseUrl} — ${TAP_WHAT.toLowerCase()} may or may not have ` +
          "been recorded. Pressing Tap again sends the same deviceTapId, so it cannot double-record.",
    };
  }

  if (res.ok) {
    try {
      const row = asRow(await res.json(), TAP_WHAT);
      const success = reqBool(row, "success", TAP_WHAT);
      const message = reqStr(row, "message", TAP_WHAT);
      // Branching happens on `code`, never on `message` — and the code is shown because on this
      // surface it is the interesting half: `Recorded` and `DuplicateIgnored` are both a 200 with
      // cheerful prose, and telling them apart is the entire reason to press the button twice.
      const code = reqStr(row, "code", TAP_WHAT);
      // The server decided and this build read the decision, so the next press is a new tap rather
      // than a retry of this one.
      pendingTaps.delete(key);
      return { ok: success, message: `${message} (${code})` };
    } catch (cause) {
      return {
        ok: false,
        message:
          `${TAP_WHAT} was accepted, but the reply is off-contract and this build cannot read it ` +
          `back: ${describeApiError(cause)} The tap is recorded — check the grid rather than ` +
          "pressing again.",
      };
    }
  }

  const problem = await readProblem(res);
  // Answered, and answered the same way forever: the next press is a new tap, so it needs a new
  // `deviceTapId` and — the half that actually changes a record — a fresh `tappedAt`.
  if (tapIsAnsweredForGood(res.status)) pendingTaps.delete(key);
  return { ok: false, message: withTrace(tapRefusal(res, problem), problem) };
}

/** The correlation handle, appended where the server sent one. `describeApiError`'s convention. */
const withTrace = (message: string, problem: ApiProblem) =>
  problem.traceId === undefined ? message : `${message} (traceId ${problem.traceId})`;

/**
 * Why the capture endpoint refused, in the reader's terms.
 *
 * **401 and 403 are kept apart because the server went out of its way to keep them apart.** 401 is a
 * key that is malformed or unknown — a bad copy, or a token from another server — and the fix is in
 * the box on this page. 403 is a key that *authenticated* and was then found to be revoked, or whose
 * device was retired: nothing about the paste is wrong, and re-pasting it is the one action
 * guaranteed not to help. Collapsing the two into "the key was refused" would hand the reader the
 * wrong next step half the time, and would throw away a distinction the handler was built to draw.
 */
function tapRefusal(res: Response, problem: ApiProblem): string {
  if (res.status === 401) {
    return (
      `${TAP_WHAT} was refused (401): the device key is malformed, or unknown to the API at ` +
      `${baseUrl}. Re-copy the whole token from /devices — a partial copy and a token issued by a ` +
      "different server both land here."
    );
  }

  if (res.status === 403) {
    return (
      `${TAP_WHAT} was refused (403): this key was recognised and then rejected — it has been ` +
      "revoked, or its device is retired. Pasting it again cannot help; issue a new key from " +
      "/devices, or use a device that is still active."
    );
  }

  if (res.status === 429) {
    const retryAfter = res.headers.get("Retry-After");
    return (
      `${TAP_WHAT} hit the capture rate limit (429).` +
      (retryAfter === null ? " Wait a moment and press Tap again." : ` Wait ${retryAfter}s and press Tap again.`) +
      " The same deviceTapId is resent, so nothing can double-record."
    );
  }

  // `.message`, not `describeApiError(…)`. `httpError` puts the problem's `traceId` on the ApiError,
  // and `describeApiError` appends it — so routing through it here would produce the handle twice,
  // once from there and once from `withTrace` at the only call site. Every arm of this function
  // returns a bare sentence and `withTrace` is the single place a trace handle is added; this arm is
  // the one that has to be spelt this way to keep that true, and it is also the arm every ordinary
  // refusal lands in (`EventNotOpen` 400, `CardNotFound` 404).
  return httpError(TAP_WHAT, problem, "write").message;
}

/**
 * A tap, refused in production and wired in development — and the difference is a runtime paste, not
 * a build-time secret.
 *
 * `POST /api/v1/attendance/tap` is secured by the `DeviceKey` scheme
 * (`Authorization: DeviceKey eams_dk_<keyId>_<secret>`), a credential the plan's §11 scopes to
 * `attendance.capture` and nothing else. Putting one in this bundle would hand a page that can only
 * *display* attendance a key that can *write* it — the precise inversion the contract rejects in its
 * own note on `GET /attendance/live/{eventId}` — and a Vite env var is inlined at build time, so the
 * GitHub Pages artefact would publish a write credential as a static asset. **That refusal is
 * unchanged and still exactly true of every production build**: the first line below is the only
 * thing a production bundle contains of this function, because `import.meta.env.DEV` is a
 * compile-time literal and everything past it is eliminated along with `deviceKey.ts`.
 *
 * What development gets instead is a key the developer pastes into the page at runtime. It is never
 * in the bundle, because it never goes near the build.
 *
 * Returns a truthful refusal rather than throwing, so `EventDetail`'s existing Snackbar says
 * something honest instead of the button appearing to do nothing. `POST /attendance/manual` is the
 * admin-scoped alternative (open, no device key) but records `captureMethod: Manual` and skips the
 * grace-period logic, so substituting it silently would change behaviour — that needs JJ's call.
 */
async function tap(eventId: string, cardUid: string): Promise<{ ok: boolean; message: string }> {
  if (!import.meta.env.DEV) return { ok: false, message: TAP_UNAVAILABLE };
  return simulateTapWithDeviceKey(eventId, cardUid);
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
  signIn,
  restoreSession,
  signOut,
  listStudents,
  countStudents,
  getStudent,
  createStudent,
  updateStudent,
  deleteStudent,
  addStudentCard,
  removeStudentCard,
  listEvents,
  createEvent,
  updateEvent,
  setEventStatus,
  deleteEvent,
  getEvent,
  listAttendance,
  eventSummary,
  getEventAudience,
  getEventScans,
  attachEventAudience,
  detachEventGroup,
  detachEventStudent,
  listDevices,
  registerDevice,
  updateDevice,
  regenerateDeviceKey,
  revokeDeviceKey,
  listStudentGroups,
  listTerms,
  createTerm,
  updateTerm,
  setTermCurrent,
  sectionChoices,
  uploadRoster,
  runImport,
  getImportBatch,
  getImportRows,
  tap,
  eventDetail,
};
