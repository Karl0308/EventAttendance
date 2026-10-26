// The one place the suite stands in for the API.
//
// Every request to `/api/v1/**` is intercepted in the browser and answered from a responder registered
// by the spec. A request nobody registered a responder for is a TEST FAILURE, not a 404 the SPA might
// quietly render around: it is answered 501 so the page does not hang, recorded, and the `api` fixture
// asserts at teardown naming each method and URL. That is what stops a screen from growing a new read
// that the suite silently never covers.
//
// Responders are keyed `"METHOD /path"` with the path relative to `/api/v1` and no query string; the
// query and body are handed to the responder (and kept in `requests`) so specs can assert what the SPA SENT.

import { test as base, expect, type Page, type Route } from "@playwright/test";

import { issuedSession } from "../fixtures/session";

const API_PREFIX = "/api/v1";
const JSON_TYPE = "application/json";

export interface RecordedRequest {
  readonly method: string;
  readonly path: string;
  readonly query: URLSearchParams;
  /** Parsed JSON body, or `undefined` when there was none or it was not JSON (multipart uploads). */
  readonly json: unknown;
}

export interface MockReply {
  readonly status?: number;
  /** Serialised as the JSON body. */
  readonly json?: unknown;
  /** A raw body, for the non-JSON downloads. */
  readonly body?: string;
  readonly headers?: Readonly<Record<string, string>>;
  /** The reply is withheld until this settles - lets a spec observe a "running" state deterministically. */
  readonly hold?: Promise<void>;
}

export type Responder = MockReply | ((request: RecordedRequest) => MockReply);

export interface MockApi {
  /** Registers (or replaces) the answer for `"METHOD /path"`. */
  on(key: string, responder: Responder): void;
  /** Requests seen for one key, in arrival order. */
  seen(key: string): RecordedRequest[];
}

interface Options {
  /** The permission codes the signed-in fixture user holds. */
  grants: readonly string[];
}

function jsonOf(raw: string | null): unknown {
  if (raw === null || raw === "") return undefined;
  try {
    return JSON.parse(raw);
  } catch (cause) {
    // Multipart uploads are not JSON; that is expected, so the body is simply not parsed. Anything
    // else that fails to parse is still surfaced, because a spec asserting on `json` will see
    // `undefined` and fail with the request in hand.
    if (cause instanceof SyntaxError) return undefined;
    throw cause;
  }
}

async function fulfil(route: Route, reply: MockReply): Promise<void> {
  if (reply.hold !== undefined) await reply.hold;
  await route.fulfill({
    status: reply.status ?? 200,
    headers: { ...(reply.json !== undefined ? { "content-type": JSON_TYPE } : {}), ...reply.headers },
    body: reply.json !== undefined ? JSON.stringify(reply.json) : (reply.body ?? ""),
  });
}

async function install(
  page: Page,
  grants: readonly string[],
): Promise<{ api: MockApi; unmocked: string[] }> {
  const responders = new Map<string, Responder>();
  const requests: RecordedRequest[] = [];
  const unmocked: string[] = [];

  await page.route(`**${API_PREFIX}/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname.slice(url.pathname.indexOf(API_PREFIX) + API_PREFIX.length);
    const recorded: RecordedRequest = {
      method: request.method(),
      path,
      query: url.searchParams,
      json: jsonOf(request.postData()),
    };
    requests.push(recorded);

    const responder = responders.get(`${recorded.method} ${path}`);
    if (responder === undefined) {
      unmocked.push(`${recorded.method} ${url.pathname}${url.search}`);
      await route.fulfill({
        status: 501,
        contentType: JSON_TYPE,
        body: JSON.stringify({ title: "Unmocked in e2e" }),
      });
      return;
    }
    await fulfil(route, typeof responder === "function" ? responder(recorded) : responder);
  });

  const api: MockApi = {
    on: (key, responder) => void responders.set(key, responder),
    seen: (key) => requests.filter((r) => `${r.method} ${r.path}` === key),
  };

  // The SPA holds no token across a reload: on load it asks `POST /auth/refresh`, and a 200 is what
  // signs it in (src/api.ts `restoreSession`). Answering that call IS the session.
  api.on("POST /auth/refresh", { json: issuedSession(grants) });

  return { api, unmocked };
}

export const test = base.extend<{ api: MockApi } & Options>({
  grants: [[], { option: true }],
  // `auto`: installed for every test even if the spec never names `api`, so the catch-all can never be
  // skipped and an unrouted call fall through to the vite proxy and a real API on :5080.
  api: [
    async ({ page, grants }, provide) => {
      const { api, unmocked } = await install(page, grants);
      await provide(api);
      expect(unmocked, "requests the spec did not mock - add a responder for each").toEqual([]);
    },
    { auto: true },
  ],
});

export { expect };
