// `toDeviceKeyIssued` in `src/api.ts` — the one mapper in this codebase where failing loud is the
// *wrong* default, and the only reply the SPA ever receives that carries a credential.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS FILE EXISTS
// ---------------------------------------------------------------------------------------------
//
// `POST /devices` and `POST /devices/{id}/regenerate-key` return `eams_dk_<keyId>_<secret>` in plaintext
// and **nothing else ever will**. The server keeps `SHA-256(secret)`, so there is no "show it again"
// endpoint, no support path, and no recovery but rotating a device the operator has just created.
//
// That makes the invariant asymmetric, and the asymmetry is the whole subject:
//
//   - **A well-formed `apiKey` must survive a device that cannot be read.** The nested object is a full
//     `DeviceDto` on the wire, and it used to go through `toDevice`, which narrows five fields as
//     required. A server that dropped `hasActiveKey` from that one nested object therefore threw
//     *after* the key had been minted; `writeJson` turned it into `malformed`; and the token sitting in
//     the parsed body was discarded with it. A credential that exists on the server, has no plaintext
//     copy anywhere, and was shown to nobody. Five display fields must not be able to do that.
//   - **A missing `apiKey` must NOT be best-effort.** An absent token rendered as an empty reveal panel
//     tells the operator their device was registered, shows them nothing, and lets them close the one
//     dialog that would ever have held the key. Failing routes it through `writeJson`'s catch, which
//     says the device *was* created and that this build could not read the reply — the sentence that
//     gets them to check the list instead of pressing again.
//
// `toDeviceKeyIssued` is not exported, and this file deliberately does not change that: the mapper is
// reached through `api.registerDevice` / `api.regenerateDeviceKey` with `fetch` stubbed, which is also
// the only way to observe the half of the behaviour that lives in `writeJson` rather than in the mapper
// — that a mapper throw becomes `shape: "write"` and says the device was registered anyway.
//
// The first suite in this repository to stub `fetch`. `vitest.config.ts` sets `restoreMocks`, which
// restores spies and does **not** unstub globals, so the teardown below is explicit.

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError, api } from "../src/api";

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------

/** A complete token of the shape the server mints: `eams_dk_<12-char id>_<secret>`. */
const API_KEY = "eams_dk_a91f4c2d8e07_ZmFrZXNlY3JldGZvcnRlc3Rzb25seW5vdHJlYWxseWFrZXk";
const KEY_ID = "a91f4c2d8e07";
const DEVICE_ID = "2b7d0000-0000-4000-8000-000000000001";
const DEVICE_NAME = "Main Gate Kiosk";

/** The nested object as the contract publishes it — a full `DeviceDto`, not the three fields read. */
const wireDevice = (overrides: Record<string, unknown> = {}): Record<string, unknown> => ({
  id: DEVICE_ID,
  name: DEVICE_NAME,
  deviceType: "Kiosk",
  readerModel: "ACR122U",
  isActive: true,
  apiKeyId: KEY_ID,
  hasActiveKey: true,
  apiKeyIssuedAt: "2026-08-01T02:15:00Z",
  apiKeyLastUsedAt: null,
  apiKeyRevokedAt: null,
  lastSeenAt: null,
  ...overrides,
});

const issuedBody = (overrides: Record<string, unknown> = {}): Record<string, unknown> => ({
  device: wireDevice(),
  apiKey: API_KEY,
  ...overrides,
});

/**
 * Answers every request with this body, and hands back the mock so a test can read what was sent.
 *
 * A real `Response` rather than a hand-rolled object: `writeJson` reads `res.ok`, `res.status` and
 * `res.json()`, and a stub that only implements the three would stop resembling the thing it replaces
 * the moment one of them moves.
 */
function respondWith(body: unknown, status = 201) {
  const fetchMock = vi.fn(() =>
    Promise.resolve(
      new Response(JSON.stringify(body), {
        status,
        headers: { "Content-Type": "application/json" },
      }),
    ),
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

/** A registration that is expected to succeed. */
const register = () =>
  api.registerDevice({
    name: DEVICE_NAME,
    deviceType: "Kiosk",
    readerModel: "ACR122U",
    isActive: true,
  });

/**
 * The `ApiError` a call raised, or a loud failure — so a call that unexpectedly *succeeded* cannot read
 * as a pass, and neither can one that threw something else.
 */
async function apiErrorFrom(call: () => Promise<unknown>): Promise<ApiError> {
  try {
    await call();
  } catch (thrown) {
    if (thrown instanceof ApiError) return thrown;
    throw new Error(`Expected an ApiError; got ${String(thrown)}`);
  }
  throw new Error("Expected the call to fail; it succeeded.");
}

afterEach(() => {
  vi.unstubAllGlobals();
});

// ---------------------------------------------------------------------------------------------
// The ordinary reply
// ---------------------------------------------------------------------------------------------

describe("a well-formed reply", () => {
  it("reads the token and the device the token belongs to", () => {
    respondWith(issuedBody());
    return register().then((issued) => {
      expect(issued.apiKey).toBe(API_KEY);
      expect(issued.device.id).toBe(DEVICE_ID);
      expect(issued.device.name).toBe(DEVICE_NAME);
      expect(issued.device.apiKeyId).toBe(KEY_ID);
    });
  });

  it("reports no drift", async () => {
    respondWith(issuedBody());
    expect((await register()).deviceDrift).toBeUndefined();
  });

  it("reads the token verbatim, not a normalised version of it", async () => {
    // The token is case-significant and 85 characters. Anything that trims, lower-cases or re-wraps it
    // hands the operator a string that will not authenticate, with no way to compare it against the
    // original because there is no original left.
    respondWith(issuedBody());
    const issued = await register();
    expect(issued.apiKey).toStrictEqual(API_KEY);
  });

  it("keeps nothing key-shaped beyond the token itself", () => {
    // `IssuedKeyDevice` is three fields: the two the reveal titles itself with, plus the *public* key
    // id. The nested `DeviceDto` carries eight more, and none of them belong in a dialog that must be
    // closable without leaving a credential in a page's state.
    respondWith(issuedBody());
    return register().then((issued) => {
      expect(Object.keys(issued.device).sort()).toEqual(["apiKeyId", "id", "name"]);
    });
  });

  it("posts to /devices", async () => {
    const fetchMock = respondWith(issuedBody());
    await register();
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(String(url)).toContain("/devices");
    expect(init.method).toBe("POST");
  });
});

// ---------------------------------------------------------------------------------------------
// THE INVARIANT — the token survives a device that cannot be read
// ---------------------------------------------------------------------------------------------

describe("a device that cannot be read", () => {
  /** Every shape `isRow` refuses. `undefined` is the absent key; the rest are wrong types. */
  const notAnObject = [
    ["null", null],
    ["a string", "Main Gate Kiosk"],
    ["a number", 42],
    ["an array", [wireDevice()]],
    ["absent", undefined],
  ] as const;

  it.each(notAnObject)("still delivers the token when `device` is %s", async (_label, device) => {
    // The load-bearing assertion in this file. The key has already been minted on the server by the
    // time this body arrives; withholding it because the display object is unreadable destroys a
    // credential that cannot be re-fetched.
    respondWith(issuedBody({ device }));
    const issued = await register();
    expect(issued.apiKey).toBe(API_KEY);
  });

  it.each(notAnObject)("says why the device is thin when it is %s", async (_label, device) => {
    respondWith(issuedBody({ device }));
    const issued = await register();
    expect(issued.deviceDrift).toBeDefined();
    // The caveat has to be actionable: the operator has a token and no name, so the sentence that
    // helps is the one that tells them how to find the row.
    expect(issued.deviceDrift).toContain("key id");
  });

  it("hands back an empty device rather than a half-built one", async () => {
    respondWith(issuedBody({ device: null }));
    const issued = await register();
    expect(issued.device).toEqual({});
  });

  it.each([
    ["id", { id: undefined }],
    ["name", { name: undefined }],
    ["id, name", { id: undefined, name: undefined }],
  ])("delivers the token and names %s as missing", async (missing, patch) => {
    respondWith(issuedBody({ device: wireDevice(patch) }));
    const issued = await register();

    expect(issued.apiKey).toBe(API_KEY);
    expect(issued.deviceDrift).toContain(missing);
  });

  it("keeps the fields it could read even when one is missing", async () => {
    // Best-effort means best-*effort*: a missing name must not take the key id with it, because the key
    // id is what makes the drift caveat's "find it in the list" possible at all.
    respondWith(issuedBody({ device: wireDevice({ name: undefined }) }));
    const issued = await register();

    expect(issued.device.id).toBe(DEVICE_ID);
    expect(issued.device.apiKeyId).toBe(KEY_ID);
    expect(issued.device.name).toBeUndefined();
  });

  it.each([["a number", 42], ["null", null]] as const)(
    "treats a %s where a name should be as missing rather than rendering it",
    async (_label, name) => {
      respondWith(issuedBody({ device: wireDevice({ name }) }));
      const issued = await register();

      expect(issued.apiKey).toBe(API_KEY);
      expect(issued.device.name).toBeUndefined();
      expect(issued.deviceDrift).toContain("name");
    },
  );

  it("does not treat an absent apiKeyId as drift", async () => {
    // The contract makes it nullable, and the dialog says "not reported" for it. Calling that drift
    // would put a scary caveat on a perfectly ordinary reply — and a caveat that fires on healthy data
    // is one nobody reads on the day it means something.
    respondWith(issuedBody({ device: wireDevice({ apiKeyId: undefined }) }));
    const issued = await register();

    expect(issued.device.apiKeyId).toBeUndefined();
    expect(issued.deviceDrift).toBeUndefined();
  });

  it("does not treat a null apiKeyId as drift either", async () => {
    respondWith(issuedBody({ device: wireDevice({ apiKeyId: null }) }));
    expect((await register()).deviceDrift).toBeUndefined();
  });

  it("survives a nested device missing the fields toDevice would have required", async () => {
    // The regression this mapper was changed for, expressed as the case that produced it. `hasActiveKey`
    // and `deviceType` are `reqBool`/`reqStr` in `toDevice`; routing this body through that mapper threw,
    // and the token went into the bin with the exception.
    respondWith(
      issuedBody({
        device: { id: DEVICE_ID, name: DEVICE_NAME, apiKeyId: KEY_ID },
      }),
    );
    const issued = await register();

    expect(issued.apiKey).toBe(API_KEY);
    expect(issued.device.name).toBe(DEVICE_NAME);
    expect(issued.deviceDrift).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------------------------
// THE HARD FAILURE — a reply with no usable token
// ---------------------------------------------------------------------------------------------

describe("a reply with no usable token", () => {
  const notAToken = [
    ["absent", {}],
    ["null", { apiKey: null }],
    ["a number", { apiKey: 42 }],
    ["an object", { apiKey: { value: API_KEY } }],
    ["an array", { apiKey: [API_KEY] }],
    ["a boolean", { apiKey: false }],
  ] as const;

  it.each(notAToken)("fails rather than delivering an empty reveal when apiKey is %s", async (
    _label,
    patch,
  ) => {
    // The counterweight to every best-effort case above, and the reason the token is narrowed *first*
    // and outside them. An `undefined` here reaches the dialog as an empty panel: the operator is told
    // the device was registered, shown nothing, and closes the only thing that would ever have held the
    // key.
    respondWith({ device: wireDevice(), ...patch });
    const error = await apiErrorFrom(register);
    expect(error.kind).toBe("malformed");
  });

  it("says the device WAS registered, so the operator checks rather than presses again", async () => {
    // Supplied by `writeJson`'s catch rather than by the mapper, which is why this is exercised through
    // `api.registerDevice`. Getting this wrong is worse than the empty panel: "it was not saved" over a
    // device that was is how a second device appears.
    respondWith({ device: wireDevice() });
    const error = await apiErrorFrom(register);

    expect(error.shape).toBe("write");
    expect(error.message).toContain("registered");
    expect(error.message).toContain("do not send it again");
  });

  it("names the field it could not read", async () => {
    respondWith({ device: wireDevice() });
    const error = await apiErrorFrom(register);
    expect(error.message).toContain("apiKey");
  });

  it("refuses an empty-string token, which the type narrowing alone would have let through", async () => {
    // This used to pin the opposite, because `reqStr` narrows the *type* and not the value, so `""` is a
    // string and passed — leaving the empty reveal this whole section exists to prevent reachable by one
    // specific reply. Closed deliberately with an explicit value check in `toDeviceKeyIssued`.
    //
    // Today's server cannot send it: `DeviceKey.Issue()` always builds an 85-character token and
    // `DeviceKeyIssuedDto.ApiKey` is non-nullable and constructed in one place from `issued.Token`. The
    // guard is therefore about a proxy, a stub, or some future server that is not this one — which is
    // the same class of reply every other case in this section is about.
    respondWith(issuedBody({ apiKey: "" }));
    const error = await apiErrorFrom(register);

    expect(error.kind).toBe("malformed");
    // The write half, as above: the device WAS registered, so the operator must check rather than press
    // again — an empty token that read as "not saved" is how a second device appears.
    expect(error.shape).toBe("write");
    expect(error.message).toContain("apiKey");
  });

  it("fails when the whole body is not an object", async () => {
    respondWith([issuedBody()]);
    const error = await apiErrorFrom(register);
    expect(error.kind).toBe("malformed");
    expect(error.shape).toBe("write");
  });
});

// ---------------------------------------------------------------------------------------------
// Rotation reaches the same mapper
// ---------------------------------------------------------------------------------------------

describe("regenerateDeviceKey", () => {
  const rotate = () => api.regenerateDeviceKey(DEVICE_ID);

  it("reads the new token", async () => {
    respondWith(issuedBody(), 200);
    expect((await rotate()).apiKey).toBe(API_KEY);
  });

  it("delivers the token even when the device beside it is unreadable", async () => {
    // Rotation is the *remedy* for a lost key. A rotation that discards the replacement because the
    // display object drifted leaves the device with a burnt old key and no new one — strictly worse
    // than not having pressed it.
    respondWith(issuedBody({ device: "not an object" }), 200);
    const issued = await rotate();

    expect(issued.apiKey).toBe(API_KEY);
    expect(issued.deviceDrift).toBeDefined();
  });

  it("says a new key was issued when the reply cannot be read at all", async () => {
    respondWith({ device: wireDevice() }, 200);
    const error = await apiErrorFrom(rotate);

    expect(error.shape).toBe("write");
    expect(error.message).toContain("issued");
  });
});
