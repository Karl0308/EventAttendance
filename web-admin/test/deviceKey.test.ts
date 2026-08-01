/** @vitest-environment happy-dom */

// `src/deviceKey.ts` — the dev-only device-key store, and the shape check that stands in for
// `DeviceKey.TryParse`.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// The validator is a **restatement of a frozen server-side format** (85 characters, `eams_dk_`, a `_`
// at a fixed offset, lower-case hex either side). A copy of a contract with nothing binding it to the
// original is exactly the thing that drifts, and the failure mode when it drifts is bad in both
// directions: too strict and a valid token is refused with a confident sentence explaining that it is
// malformed, too loose and the whole point of validating on the way in — not learning about a typo
// from an opaque 401 — is gone.
//
// It runs in `happy-dom` because the subject reaches `window.sessionStorage`. That is the second
// thing pinned here: `sessionStorage` and not `localStorage`, which is a security decision (a capture
// credential must die with the tab) and is otherwise a one-word difference nobody would notice
// changing.

import { beforeEach, describe, expect, it } from "vitest";
import { clearDeviceKey, deviceKeyStatus, getDeviceKey, setDeviceKey } from "../src/deviceKey";

const KEY_ID = "096b2085a1c3";
const SECRET = "a".repeat(64);
const VALID = `eams_dk_${KEY_ID}_${SECRET}`;

beforeEach(() => {
  window.sessionStorage.clear();
  window.localStorage.clear();
});

describe("setDeviceKey", () => {
  it("accepts a well-formed token and reports its public half", () => {
    expect(setDeviceKey(VALID)).toEqual({ ok: true, keyId: KEY_ID });
  });

  it("trims surrounding whitespace, because a paste brings a newline", () => {
    expect(setDeviceKey(`  ${VALID}\n`)).toEqual({ ok: true, keyId: KEY_ID });
    expect(getDeviceKey()).toBe(VALID);
  });

  // Every arm of the shape check, each with the reason it exists. The assertion is on `ok` plus a
  // fragment of the sentence, not on the whole sentence: the wording is prose and is reworded freely,
  // but *which* rule was broken is the part the reader acts on.
  it.each([
    ["empty", "", "Nothing was pasted"],
    ["too short", VALID.slice(0, -1), "characters"],
    ["too long", `${VALID}a`, "characters"],
    ["wrong prefix", `eams_xx_${KEY_ID}_${SECRET}`, "does not start with"],
    // 85 characters and all lower-case hex, with only the `_` missing — so this is the one case that
    // reaches the separator check rather than being caught by length or by the hex rules.
    ["separator moved", `eams_dk_${KEY_ID}a${SECRET}`, "between the key id"],
    ["non-hex in the key id", `eams_dk_zzzzzzzzzzzz_${SECRET}`, "lower-case hex"],
    ["non-hex in the secret", `eams_dk_${KEY_ID}_${"z".repeat(64)}`, "lower-case hex"],
    // The case rule is the one a reader is most likely to think is pedantry, and `DeviceKey.cs` is
    // explicit that it is not: an upper-cased token is malformed and 401s, so refusing it here is the
    // honest answer and lower-casing it silently would invent a second valid spelling.
    ["upper-cased", VALID.toUpperCase(), "not start with"],
  ])("refuses a %s token", (_name, token, fragment) => {
    const result = setDeviceKey(token);
    expect(result.ok).toBe(false);
    if (result.ok) throw new Error("unreachable — the assertion above has already failed");
    expect(result.reason).toContain(fragment);
  });

  it("stores nothing when the token is refused", () => {
    setDeviceKey("nonsense");
    expect(getDeviceKey()).toBeUndefined();
    expect(deviceKeyStatus()).toEqual({ kind: "absent" });
  });
});

describe("where the key lives", () => {
  // The security decision, asserted rather than described. `localStorage` would leave a credential
  // that can write attendance in a developer's browser next week.
  it("uses sessionStorage and never localStorage", () => {
    setDeviceKey(VALID);
    expect(Object.values(window.sessionStorage)).toContain(VALID);
    expect(window.localStorage.length).toBe(0);
  });
});

describe("deviceKeyStatus", () => {
  it("is absent before anything is pasted", () => {
    expect(deviceKeyStatus()).toEqual({ kind: "absent" });
  });

  it("carries the key id and a label, and never the secret", () => {
    setDeviceKey(VALID);
    const status = deviceKeyStatus();
    expect(status).toEqual({ kind: "set", keyId: KEY_ID, label: `eams_dk_${KEY_ID}…` });
    expect(JSON.stringify(status)).not.toContain(SECRET);
  });

  it("reports a hand-written storage value as unreadable rather than as set", () => {
    window.sessionStorage.setItem("eams.dev.deviceKey", "not-a-key");
    expect(deviceKeyStatus().kind).toBe("unreadable");
  });
});

describe("clearDeviceKey", () => {
  it("forgets the token", () => {
    setDeviceKey(VALID);
    expect(clearDeviceKey()).toEqual({ ok: true });
    expect(getDeviceKey()).toBeUndefined();
    expect(deviceKeyStatus()).toEqual({ kind: "absent" });
  });
});
