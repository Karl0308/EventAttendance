// The only module in this SPA that ever holds a device key, and it exists **for development builds
// only**.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS IS ALLOWED TO EXIST AT ALL
// ---------------------------------------------------------------------------------------------
//
// `api.ts`'s note above `tap()` refuses to put a device key in this bundle, and that refusal is still
// correct: `POST /attendance/tap` is the *capture* surface, a key scoped to `attendance.capture` is a
// write credential, and **a Vite env var is inlined at build time** — supplying one that way would
// publish a write credential as a static asset in the GitHub Pages artefact, readable by anyone who
// opens the JS.
//
// A value the developer pastes at runtime is a different object entirely. It never goes near the
// build, so it is never in the bundle; it lives in one browser tab, put there deliberately by the
// person who also has access to `/devices`. That distinction — **build-time inlining versus
// runtime paste** — is the entire basis of this module, and it is the thing to preserve if anyone
// ever reaches for `import.meta.env.VITE_…` here.
//
// The second half of the guarantee is that none of this reaches production at all: every export is
// inert unless `import.meta.env.DEV`, which is a compile-time literal, so the production build
// eliminates the bodies below along with everything only they reference. That is checked on the real
// artefact (`npm run build && grep … dist/assets/*.js`), not assumed — see the panel's own note.
//
// ---------------------------------------------------------------------------------------------
// WHY SESSIONSTORAGE
// ---------------------------------------------------------------------------------------------
//
// `sessionStorage`, never `localStorage`. A capture credential that outlives the tab is a credential
// nobody remembers is there: `localStorage` would leave a key that can write attendance sitting in a
// developer's browser next week, on a shared machine, long after the afternoon it was pasted for. The
// tab closing is the revocation everyone will actually perform.

/**
 * The frozen token format, restated from `EAMS.Domain/DeviceKey.cs`.
 *
 * A copy, and worth saying so: nothing publishes these numbers, so the honest description is "a
 * restatement that will drift if the format ever moves". It is restated rather than skipped because
 * the alternative is a typo coming back as an opaque 401 from the capture endpoint — a message that
 * says nothing about which character was wrong, arriving after a round trip, on a surface where a
 * developer's next guess is usually "the server is broken".
 */
const TOKEN_PREFIX = "eams_dk_";

/** The separator between the public key id and the secret. */
const SEPARATOR = "_";

/** Characters in the public half. 48 bits of randomness as hex. */
const KEY_ID_LENGTH = 12;

/** Characters in the secret half. 256 bits as hex. */
const SECRET_LENGTH = 64;

/** 85. `eams_dk_` + key id + `_` + secret. */
const TOKEN_LENGTH = TOKEN_PREFIX.length + KEY_ID_LENGTH + SEPARATOR.length + SECRET_LENGTH;

/**
 * The `Authorization` scheme name — frozen contract, and the reason this constant lives here rather
 * than in `api.ts`: the scheme and the token are one credential, and this module is the only one that
 * is allowed to know what one looks like.
 */
export const DEVICE_KEY_SCHEME = "DeviceKey";

/**
 * Where the pasted token sits for the life of the tab.
 *
 * Namespaced so it is obvious in devtools what it is and that it is a development artefact — the
 * person most likely to find it there is the person who needs to know it can write attendance.
 */
const STORAGE_KEY = "eams.dev.deviceKey";

/** Both halves are lower-case hex, and **case is significant** — an upper-cased token is malformed. */
const LOWER_HEX = /^[0-9a-f]+$/;

/**
 * What this browser currently holds, in the terms a screen may render.
 *
 * `label` and `keyId` are the **public** half only. The secret never appears in this type, which is
 * what makes "the panel cannot display the secret" structural rather than remembered: the panel is
 * given no value that contains one.
 *
 * `unreadable` is its own arm rather than folded into `absent`. `sessionStorage` throws outright when
 * storage is disabled or partitioned, and reporting that as "no key set" would send a developer round
 * a loop of pasting a key that is silently discarded every time.
 *
 * `unsupported` is **defence in depth and is not expected to be reachable**: the panel is rendered
 * behind `import.meta.env.DEV` and nothing else calls into this module, so no production build has a
 * caller. It is modelled and returned anyway because the invariant it guards — this module never
 * hands out a capture credential outside a development build — is the kind that should hold by
 * construction at every level rather than by one caller remembering. Read the arms below that produce
 * it as an assertion, not as a branch anyone reaches.
 */
export type DeviceKeyStatus =
  | { kind: "unsupported" }
  | { kind: "absent" }
  | { kind: "set"; keyId: string; label: string }
  | { kind: "unreadable"; reason: string };

/** The outcome of a paste. `reason` is written to be read in the field, beside the box. */
export type SaveDeviceKeyResult = { ok: true; keyId: string } | { ok: false; reason: string };

/** The outcome of Clear. It can fail for the same storage reasons a read can. */
export type ClearDeviceKeyResult = { ok: true } | { ok: false; reason: string };

/**
 * What is said when something asks this module to do anything in a production build.
 *
 * Same standing as the `unsupported` status arm: no caller exists in a production build, so this
 * string is not expected to be rendered. It is the sentence the assertion carries if the guard ever
 * does fire, and it is written for a person rather than as a placeholder for that reason.
 */
const NOT_A_DEV_BUILD = "Device keys are a development-build feature and are not available here.";

/** How the public half is shown: enough to compare against `/devices`, and no secret. */
const labelFor = (keyId: string) => `${TOKEN_PREFIX}${keyId}…`;

/**
 * `sessionStorage` reached through a function that reports its own failure rather than through a
 * bare property access.
 *
 * Nothing is swallowed here: the DOMException's message is carried out as a `reason` and rendered.
 * A `catch` that returned `undefined` would be the silent kind — a developer would paste a key, see
 * the panel go back to empty, and have no sentence anywhere telling them why.
 */
function openStorage(): { ok: true; storage: Storage } | { ok: false; reason: string } {
  try {
    return { ok: true, storage: window.sessionStorage };
  } catch (cause) {
    const detail = cause instanceof Error ? cause.message : String(cause);
    return {
      ok: false,
      reason: `This browser refused access to sessionStorage, so a device key cannot be held: ${detail}`,
    };
  }
}

/**
 * The shape check, mirroring `DeviceKey.TryParse` — length, prefix, separator position, lower-hex on
 * both halves — and failing with a sentence that names which of those it was.
 *
 * Exported for the panel to reuse? Deliberately not. Validation happens on the way *in*, once, in
 * `setDeviceKey`, so there is one answer to "is this a key" and no second copy to drift.
 */
function parseToken(token: string): SaveDeviceKeyResult {
  if (token === "") {
    return { ok: false, reason: "Nothing was pasted. Copy the whole token from /devices." };
  }

  if (token.length !== TOKEN_LENGTH) {
    return {
      ok: false,
      reason:
        `That is not a well-formed token: a device key is exactly ${TOKEN_LENGTH} characters and ` +
        `this one is ${token.length}. It is usually a partial copy — select the whole value.`,
    };
  }

  if (!token.startsWith(TOKEN_PREFIX)) {
    return {
      ok: false,
      reason: `That is not a well-formed token: it does not start with \`${TOKEN_PREFIX}\`.`,
    };
  }

  const body = token.slice(TOKEN_PREFIX.length);
  if (body[KEY_ID_LENGTH] !== SEPARATOR) {
    return {
      ok: false,
      reason:
        "That is not a well-formed token: the `_` between the key id and the secret is not where " +
        "it should be.",
    };
  }

  const keyId = body.slice(0, KEY_ID_LENGTH);
  const secret = body.slice(KEY_ID_LENGTH + SEPARATOR.length);
  if (!LOWER_HEX.test(keyId) || !LOWER_HEX.test(secret)) {
    return {
      ok: false,
      reason:
        "That is not a well-formed token: both halves are lower-case hex (0-9, a-f) only. Note that " +
        "case is significant — an upper-cased token is malformed and the server will refuse it.",
    };
  }

  return { ok: true, keyId };
}

/**
 * The token, for `api.ts` and nothing else.
 *
 * It returns the secret, so it is the one export a component must never call — and the one the panel
 * has no reason to: `deviceKeyStatus()` answers everything a screen needs.
 *
 * **The token is not validated on the way out, and what makes that safe is the caller's ordering, not
 * the storage.** "It passed `parseToken` on the way in" would be the tempting claim and it is false:
 * `deviceKeyStatus` has an `unreadable` arm precisely because something other than `setDeviceKey` can
 * write the slot — a hand-edited devtools entry. What holds instead is that
 * `simulateTapWithDeviceKey` calls `deviceKeyStatus()` and returns on `unreadable` *before* it calls
 * this function, so a malformed slot is reported rather than sent. A future caller that reaches
 * straight for this export skips that check and will send whatever is in the slot; it must run the
 * status check first, or validate here.
 */
export function getDeviceKey(): string | undefined {
  if (!import.meta.env.DEV) return undefined;

  const opened = openStorage();
  // Not a swallow: the reason is what `deviceKeyStatus()` returns, and `api.tap` reads that status
  // rather than this function when it needs to explain itself.
  if (!opened.ok) return undefined;

  return opened.storage.getItem(STORAGE_KEY) ?? undefined;
}

/** What a screen may say about the key. Never carries the secret. */
export function deviceKeyStatus(): DeviceKeyStatus {
  if (!import.meta.env.DEV) return { kind: "unsupported" };

  const opened = openStorage();
  if (!opened.ok) return { kind: "unreadable", reason: opened.reason };

  const token = opened.storage.getItem(STORAGE_KEY);
  if (token === null) return { kind: "absent" };

  const parsed = parseToken(token);
  // Only reachable if something other than `setDeviceKey` wrote the slot — a hand-edited devtools
  // entry, realistically. Reported rather than trusted or silently dropped: a token this module
  // cannot parse would come back from the server as an unexplained 401.
  if (!parsed.ok) {
    return {
      kind: "unreadable",
      reason: `The stored value is not a well-formed device key. ${parsed.reason} Clear it and paste again.`,
    };
  }

  return { kind: "set", keyId: parsed.keyId, label: labelFor(parsed.keyId) };
}

/**
 * Validates and stores a pasted token.
 *
 * **Validated before it is stored**, which is the whole point of the function: the alternative is
 * that a mistyped character is discovered by the capture endpoint as a 401 several minutes later,
 * where it is indistinguishable from a revoked key, a wrong server, or a device that was never
 * registered.
 *
 * Whitespace is trimmed and nothing else is normalised. A paste routinely brings a trailing newline,
 * and no valid token contains whitespace, so trimming cannot turn one token into another. Case is
 * emphatically *not* normalised — `DeviceKey.cs` is explicit that lower-case is the only accepted
 * spelling, and a client that quietly lower-cased input would be inventing a second valid form of a
 * frozen credential.
 */
export function setDeviceKey(token: string): SaveDeviceKeyResult {
  if (!import.meta.env.DEV) return { ok: false, reason: NOT_A_DEV_BUILD };

  const parsed = parseToken(token.trim());
  if (!parsed.ok) return parsed;

  const opened = openStorage();
  if (!opened.ok) return { ok: false, reason: opened.reason };

  try {
    opened.storage.setItem(STORAGE_KEY, token.trim());
  } catch (cause) {
    // Quota or a storage that accepts a handle and refuses a write. Reported, never dropped: the
    // panel would otherwise show "saved" over a slot that is still empty.
    const detail = cause instanceof Error ? cause.message : String(cause);
    return { ok: false, reason: `The device key could not be stored: ${detail}` };
  }

  return { ok: true, keyId: parsed.keyId };
}

/** Forgets the token. The tab closing does the same thing; this is the deliberate version. */
export function clearDeviceKey(): ClearDeviceKeyResult {
  if (!import.meta.env.DEV) return { ok: false, reason: NOT_A_DEV_BUILD };

  const opened = openStorage();
  if (!opened.ok) return opened;

  try {
    opened.storage.removeItem(STORAGE_KEY);
  } catch (cause) {
    const detail = cause instanceof Error ? cause.message : String(cause);
    return { ok: false, reason: `The device key could not be cleared: ${detail}` };
  }

  return { ok: true };
}
