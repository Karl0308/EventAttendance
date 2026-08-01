// What `src/deviceDraft.ts` accepts, what it refuses, and — the half this file exists for — which of
// the five standings it hands a device, and why the order it asks in is the answer.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// `DEVICE_NAME_MAX_LENGTH` and `READER_MODEL_MAX_LENGTH` are a **hand-written copy** of `DeviceText` in
// `backend/EAMS.Domain/DeviceEntities.cs`, and the cleaning rule is `studentDraft`'s `cleanText`, itself
// a copy of `RosterText.Clean`. `deviceDraft.ts` says so above its own constants.
//
// **These tests pin the client's copy, and cannot detect server drift** — the same limitation
// `studentDraft.test.ts` and `eventDraft.test.ts` record, for the same reason: nothing in this
// repository compares the two, and a frontend unit test is structurally incapable of it. The binding
// artefact is those limits being published into `docs/api/openapi.json`, which is scheduled backend
// work (MDVault #206).
//
// There is one deliberate departure from the siblings, in `the limits against the server` below: the
// two numbers are additionally asserted against the values read out of `DeviceText` by hand on
// 2026-08-01. It cannot see a *server* change either — but it turns a change made on the *client* into
// a failing test that names the file to go and re-read, instead of a silently widened form. It is a
// tripwire, not a contract test, and it is labelled as one.
//
// What these tests can detect, and what they exist for:
//
//   1. **`keyStanding`'s five arms and the order they are asked in.** Each standing exists because each
//      has a different operator remedy, so "it returned a KeyStanding" is worth nothing — every case
//      below asserts *which*. The `indeterminate` residue is pinned hardest: it was a fall-through into
//      `device-inactive` until recently, which handed a switched-on device a remedy that does nothing.
//   2. **`KEY_STANDING_LABELS` covering the whole union at runtime**, not only at compile time.
//   3. **The measured value and the sent value being the same string** — `deviceDraft`'s share of the
//      cleaning rule the server fixed a 500 over.
//
// No time zone is involved anywhere in this module, so nothing here depends on the suite's baseline
// zone.

import { describe, expect, it } from "vitest";

import {
  DEVICE_NAME_MAX_LENGTH,
  DEVICE_TYPE_LABELS,
  EMPTY_DEVICE_DRAFT,
  KEY_STANDING_LABELS,
  READER_MODEL_MAX_LENGTH,
  VALIDATED_DEVICE_FIELDS,
  keyStanding,
  validateDevice,
} from "../src/deviceDraft";
import type { DeviceDraft, DeviceFieldErrors, DeviceValidated, KeyStandingKind } from "../src/deviceDraft";
import { DEVICE_TYPES } from "../src/types";
import type { Device, DeviceWriteRequest } from "../src/types";

// ---------------------------------------------------------------------------------------------
// The characters that cannot be typed into a test and read back out of it
// ---------------------------------------------------------------------------------------------
//
// Named numerically for the reason `RosterText` gives on the server and `studentDraft.test.ts` repeats:
// a literal zero-width character in source is invisible in every diff, editor and review, and in a test
// that is worse than in production code — the assertion silently stops being about what its name says.

const ZERO_WIDTH_SPACE = String.fromCodePoint(0x200b);
const BYTE_ORDER_MARK = String.fromCodePoint(0xfeff);
const NO_BREAK_SPACE = String.fromCodePoint(0x00a0);

// ---------------------------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------------------------

/** A draft that passes, so each test can move exactly one thing and know that is what it measured. */
const baseDraft = (overrides: Partial<DeviceDraft> = {}): DeviceDraft => ({
  ...EMPTY_DEVICE_DRAFT,
  name: "Main Gate Kiosk",
  ...overrides,
});

const characters = (count: number) => "x".repeat(count);

/** Fails loudly rather than returning a half-answer, so a mis-set-up test cannot read as a pass. */
function errorsOf(result: DeviceValidated): DeviceFieldErrors {
  if (result.ok) throw new Error("Expected validation to fail; it succeeded.");
  return result.errors;
}

function requestOf(result: DeviceValidated): DeviceWriteRequest {
  if (!result.ok) {
    throw new Error(`Expected validation to pass; it failed: ${JSON.stringify(result.errors)}`);
  }
  return result.request;
}

// ---------------------------------------------------------------------------------------------
// keyStanding — the five kinds, each reached by its own device
// ---------------------------------------------------------------------------------------------
//
// Every fixture below is grounded in the server's own rule rather than in what makes the branch fire:
//
//     HasActiveKey = HasKey && ApiKeyRevokedAt is null && IsActive,   HasKey = ApiKeyId && ApiKeyHash
//
// (`EAMS.Domain/DeviceEntities.cs`, applied by `DeviceService`.) Two consequences the fixtures are built
// on. `hasActiveKey` is not an independent boolean — it is derived, so a fixture that set it true beside
// a revocation would be a row the server cannot produce and a test about nothing. And the hash is
// **not on the wire at all**: `Device` carries `apiKeyId` and no hash, which is exactly what makes
// `indeterminate` reachable — an id-without-hash row is indistinguishable, from here, from a healthy one
// except that the server says it cannot authenticate.

const REVOKED_AT = "2026-07-30T02:15:00Z";
const ISSUED_AT = "2026-07-01T02:15:00Z";

/** A registered kiosk. Every field the standing reads is set explicitly by the callers below. */
const aDevice = (overrides: Partial<Device> = {}): Device => ({
  id: "2b7d0000-0000-4000-8000-000000000001",
  name: "Main Gate Kiosk",
  deviceType: "Kiosk",
  readerModel: "ACR122U",
  isActive: true,
  hasActiveKey: false,
  apiKeyId: "a91f4c2d8e07",
  apiKeyIssuedAt: ISSUED_AT,
  ...overrides,
});

/**
 * One device per kind, keyed by the kind it must produce.
 *
 * `satisfies Record<KeyStandingKind, Device>` rather than a plain object: adding a sixth member to
 * `KeyStandingKind` without a fixture for it stops compiling here, so a new standing cannot arrive with
 * no test. The runtime half — that the keys of this table and of `KEY_STANDING_LABELS` are the same set
 * — is asserted below, which is what catches a `Record` drifting from the union in the other direction.
 */
const DEVICE_FOR = {
  // Holds a key, not revoked, switched on — the only combination the server derives `true` from.
  active: aDevice({ hasActiveKey: true }),

  // Never issued one: no id, so no `apiKeyIssuedAt` either.
  "never-issued": aDevice({ apiKeyId: undefined, apiKeyIssuedAt: undefined }),

  // Issued and then burned. Still switched on, so `revoked` is the only thing wrong with it.
  revoked: aDevice({ apiKeyRevokedAt: REVOKED_AT }),

  // Holds an unrevoked key; the device itself is off. Ticking Active restores the same key.
  "device-inactive": aDevice({ isActive: false }),

  // THE RESIDUE. Id present, not revoked, switched on — and the server still says it cannot
  // authenticate, which on the rule above leaves exactly one cause: a row with an id and no hash.
  indeterminate: aDevice(),
} satisfies Record<KeyStandingKind, Device>;

const ALL_KINDS = Object.keys(DEVICE_FOR) as KeyStandingKind[];

describe("keyStanding", () => {
  it.each(ALL_KINDS)("answers %s for the device that is exactly that", (kind) => {
    expect(keyStanding(DEVICE_FOR[kind]).kind).toBe(kind);
  });

  it.each(ALL_KINDS)("labels %s with the one agreed wording", (kind) => {
    // The grid chip, the page legend and this module's own prose all read from `KEY_STANDING_LABELS`.
    // A label composed here instead would be a legend for a different screen.
    expect(keyStanding(DEVICE_FOR[kind]).label).toBe(KEY_STANDING_LABELS[kind]);
  });

  it.each(ALL_KINDS)("gives %s a detail that says what would change it", (kind) => {
    // Prose, so this asserts presence rather than wording — but an empty `detail` is a row whose
    // action panel renders a heading over nothing.
    expect(keyStanding(DEVICE_FOR[kind]).detail.length).toBeGreaterThan(0);
  });

  it("gives every kind a distinct label", () => {
    // Two standings sharing a wording is two different remedies presented as one.
    const labels = ALL_KINDS.map((kind) => KEY_STANDING_LABELS[kind]);
    expect(new Set(labels).size).toBe(labels.length);
  });
});

// ---------------------------------------------------------------------------------------------
// THE RESIDUE — the case the fall-through got wrong
// ---------------------------------------------------------------------------------------------

describe("the indeterminate residue", () => {
  /** Id present, unrevoked, switched on — and refused anyway. */
  const residue = DEVICE_FOR.indeterminate;

  it("is reached by a device that is switched on, holds an id, and is not revoked", () => {
    // The premise, asserted rather than assumed: this fixture is the residue only while all four of
    // these hold. Any one of them drifting would silently move the test onto a different branch, and it
    // would still pass — for the wrong reason.
    expect(residue.hasActiveKey).toBe(false);
    expect(residue.apiKeyId).toBeDefined();
    expect(residue.apiKeyRevokedAt).toBeUndefined();
    expect(residue.isActive).toBe(true);

    expect(keyStanding(residue).kind).toBe("indeterminate");
  });

  it("is NOT device-inactive — this is the regression the fall-through produced", () => {
    // `device-inactive` used to be the fall-through arm, so this device got it. The answer is wrong in
    // the way that costs the most: the remedy it names is "edit it and tick Active" on a device whose
    // box is *already ticked*, and it promises the same key back afterwards. The operator ticks nothing,
    // nothing changes, and the one thing that would have helped — rotating — is the thing they were
    // told they did not need.
    expect(keyStanding(residue).kind).not.toBe("device-inactive");
  });

  it("sends the operator to a rotation rather than to a checkbox", () => {
    // The behavioural half of the assertion above: the two arms are only worth telling apart because
    // their remedies differ, so the remedy is what is checked, not just the tag.
    const detail = keyStanding(residue).detail;
    expect(detail).toContain("new key");
    expect(detail).not.toContain("Active");
  });

  it("still answers device-inactive for a device that really is switched off", () => {
    // The negative control for the pair above. Without it, `indeterminate` could be the answer to
    // everything and the test above would not notice.
    expect(keyStanding(DEVICE_FOR["device-inactive"]).kind).toBe("device-inactive");
    expect(keyStanding(aDevice({ isActive: false })).detail).toContain("Active");
  });
});

// ---------------------------------------------------------------------------------------------
// THE ORDER — which true thing is reported when more than one is
// ---------------------------------------------------------------------------------------------

describe("the order the standings are asked in", () => {
  it("reports revoked, not device-inactive, for a device that is both", () => {
    // The load-bearing one. Rotating is what clears a revocation, whichever way the active flag is set —
    // so "switched off" first would send someone to a checkbox that does not bring the credential back,
    // and they would tick it, watch nothing happen, and have learned nothing about why.
    const both = aDevice({ apiKeyRevokedAt: REVOKED_AT, isActive: false });
    expect(both.apiKeyRevokedAt).toBeDefined();
    expect(both.isActive).toBe(false);

    expect(keyStanding(both).kind).toBe("revoked");
  });

  it("tells a revoked AND switched-off device that a new key alone will not bring it back", () => {
    // The other half of the ordering decision above. Reporting `revoked` first is right — rotating is
    // what clears the revocation — but the arm's own sentence says issuing is "the only way back", and
    // for this device it is not: rotation clears `ApiKeyRevokedAt` and leaves `IsActive` false, so
    // `HasActiveKey` stays false and the reader still refuses. Without the clause the operator spends a
    // one-shot token, walks to the door, types it in, and nothing taps.
    //
    // Two clicks away and the *correct* response to a compromised device: revoke the key, then untick
    // Active.
    const standing = keyStanding(aDevice({ apiKeyRevokedAt: REVOKED_AT, isActive: false }));

    expect(standing.kind).toBe("revoked");
    expect(standing.deviceOff).toBe(true);
    expect(standing.detail).toContain("Active");
  });

  it("does not tell a revoked but switched-ON device to go and tick anything", () => {
    // The negative control: the clause has to be conditional, or the arm that is honest today starts
    // sending people to a checkbox that is already ticked — the exact regression `indeterminate` exists
    // to have fixed.
    const standing = keyStanding(aDevice({ apiKeyRevokedAt: REVOKED_AT }));

    expect(standing.deviceOff).toBe(false);
    expect(standing.detail).not.toContain("Active");
  });

  it("reports never-issued, not revoked, for a device that has no key at all", () => {
    // A device that never held a key has no revocation to report. A row carrying a revocation timestamp
    // with no id is drift rather than a state, and naming the revocation would send someone looking for
    // a credential that never existed.
    const neither = aDevice({ apiKeyId: undefined, apiKeyRevokedAt: REVOKED_AT });
    expect(keyStanding(neither).kind).toBe("never-issued");
  });

  it("reports never-issued, not device-inactive, for a switched-off device with no key", () => {
    // Issuing is the remedy; the active flag is a second thing to fix afterwards, not the first thing
    // to be told about.
    expect(keyStanding(aDevice({ apiKeyId: undefined, isActive: false })).kind).toBe("never-issued");
  });

  it("believes hasActiveKey over everything below it", () => {
    // `hasActiveKey` is the server's own answer to "can this tap right now". The arms below exist to
    // explain a `false`; none of them may override a `true`. A row like this cannot be produced by the
    // rule — it is here as the ordering assertion, not as a state that occurs.
    const contradictory = aDevice({ hasActiveKey: true, apiKeyRevokedAt: REVOKED_AT, isActive: false });
    expect(keyStanding(contradictory).kind).toBe("active");
  });
});

// ---------------------------------------------------------------------------------------------
// KEY_STANDING_LABELS — the Record against the union
// ---------------------------------------------------------------------------------------------

describe("KEY_STANDING_LABELS", () => {
  it("has exactly one entry per kind, and no others", () => {
    // A `Record<KeyStandingKind, string>` that drifts from the union is a compile error — but only while
    // someone is compiling this repository. Asserted at runtime as well, so a suite run says so too, and
    // so a kind added with a label but no fixture (or the reverse) fails here rather than rendering an
    // `undefined` chip on the one row nobody has yet.
    expect(Object.keys(KEY_STANDING_LABELS).sort()).toEqual([...ALL_KINDS].sort());
  });

  it.each(ALL_KINDS)("gives %s a non-empty label", (kind) => {
    expect(KEY_STANDING_LABELS[kind].trim().length).toBeGreaterThan(0);
  });

  it("is reachable for every kind it names", () => {
    // The other direction of the same guard: a label with no device that produces it is prose for a
    // state the UI can never show.
    const produced = ALL_KINDS.map((kind) => keyStanding(DEVICE_FOR[kind]).kind);
    expect([...produced].sort()).toEqual([...ALL_KINDS].sort());
  });
});

// ---------------------------------------------------------------------------------------------
// validateDevice — the §4.10 limits
// ---------------------------------------------------------------------------------------------

describe("the limits against the server", () => {
  // A tripwire and NOT a contract test — see this file's header. `DeviceText` in
  // `backend/EAMS.Domain/DeviceEntities.cs` read `NameMaxLength = 100` and `ReaderModelMaxLength = 100`
  // on 2026-08-01. Nothing here can see the server change; what it can see is *this* file's copy
  // changing, which is the moment to go and re-read that one.
  const SERVER_NAME_MAX_LENGTH = 100;
  const SERVER_READER_MODEL_MAX_LENGTH = 100;

  it("copies DeviceText.NameMaxLength", () => {
    expect(DEVICE_NAME_MAX_LENGTH).toBe(SERVER_NAME_MAX_LENGTH);
  });

  it("copies DeviceText.ReaderModelMaxLength", () => {
    expect(READER_MODEL_MAX_LENGTH).toBe(SERVER_READER_MODEL_MAX_LENGTH);
  });
});

describe("the field limits", () => {
  const cases = [
    ["name", DEVICE_NAME_MAX_LENGTH],
    ["readerModel", READER_MODEL_MAX_LENGTH],
  ] as const;

  it.each(cases)("accepts a %s of exactly the maximum", (field, max) => {
    expect(validateDevice(baseDraft({ [field]: characters(max) })).ok).toBe(true);
  });

  it.each(cases)("refuses a %s one character over, and says how long it is", (field, max) => {
    const errors = errorsOf(validateDevice(baseDraft({ [field]: characters(max + 1) })));
    expect(errors[field]).toBeDefined();
    expect(errors[field]).toContain(String(max + 1));
  });

  it("measures after cleaning, as the server does", () => {
    // The doubled space is one character that will never be stored, so measuring the raw box would
    // refuse a name the server accepts.
    const raw = `${characters(DEVICE_NAME_MAX_LENGTH - 3)}  ab`;
    expect(raw.length).toBe(DEVICE_NAME_MAX_LENGTH + 1);

    const request = requestOf(validateDevice(baseDraft({ name: raw })));
    expect(request.name).toHaveLength(DEVICE_NAME_MAX_LENGTH);
  });
});

describe("the device name", () => {
  it("is required", () => {
    expect(errorsOf(validateDevice(baseDraft({ name: "" }))).name).toBeDefined();
  });

  it("treats a whitespace-only name as missing rather than as a name of length three", () => {
    expect(errorsOf(validateDevice(baseDraft({ name: "   " }))).name).toBeDefined();
  });

  it.each([ZERO_WIDTH_SPACE, BYTE_ORDER_MARK, `${ZERO_WIDTH_SPACE}${NO_BREAK_SPACE}`])(
    "treats an invisible-only name as missing",
    (name) => {
      // The premise and the negative control in one: U+200B is Unicode category Cf, NOT whitespace, so a
      // `trim()`-based emptiness check passes it straight through. That gap is what reaches a NOT NULL
      // column on the server — a valid-looking form earning a 400 naming a field the user believes they
      // filled in.
      expect(ZERO_WIDTH_SPACE.trim().length).toBe(1);
      expect(errorsOf(validateDevice(baseDraft({ name }))).name).toBeDefined();
    },
  );

  it("carries the cleaned name into the request, not the raw one", () => {
    expect(requestOf(validateDevice(baseDraft({ name: "  Main   Gate  " }))).name).toBe("Main Gate");
  });

  it("strips invisible characters from inside a name", () => {
    const request = requestOf(validateDevice(baseDraft({ name: `Main${ZERO_WIDTH_SPACE} Gate` })));
    expect(request.name).toBe("Main Gate");
  });
});

describe("the reader model", () => {
  it("is optional", () => {
    expect(validateDevice(baseDraft({ readerModel: "" })).ok).toBe(true);
  });

  it("sends null rather than an empty string for an omitted one", () => {
    // `""` is a reader model, and the column would store it as one — after which "this device reports
    // its hardware" is true of a device that does not.
    expect(requestOf(validateDevice(baseDraft({ readerModel: "" }))).readerModel).toBeNull();
  });

  it.each(["   ", ZERO_WIDTH_SPACE])("sends null for a %j that cleans to nothing", (readerModel) => {
    expect(requestOf(validateDevice(baseDraft({ readerModel }))).readerModel).toBeNull();
  });

  it("sends the cleaned text when there is text", () => {
    expect(requestOf(validateDevice(baseDraft({ readerModel: "  ACR122U  " }))).readerModel).toBe(
      "ACR122U",
    );
  });
});

describe("validateDevice's answer", () => {
  /** Every key `DeviceWriteRequest` may carry, and nothing else. */
  const WIRE_FIELDS = ["deviceType", "isActive", "name", "readerModel"];

  it("answers ok:true with a request", () => {
    const result = validateDevice(baseDraft());
    expect(result.ok).toBe(true);
    expect(requestOf(result)).toBeDefined();
  });

  it("answers ok:false with at least one error", () => {
    const result = validateDevice(baseDraft({ name: "" }));
    expect(result.ok).toBe(false);
    expect(Object.keys(errorsOf(result)).length).toBeGreaterThan(0);
  });

  it("builds a body of exactly the fields the contract models", () => {
    // No key-shaped field, ever: `DeviceKey.Issue` takes no input, so a key on a write body is a value
    // with nowhere to go and a suggestion that supplying one is a thing an operator may do.
    expect(Object.keys(requestOf(validateDevice(baseDraft()))).sort()).toEqual(WIRE_FIELDS);
  });

  it("only ever reports errors on fields VALIDATED_DEVICE_FIELDS knows about", () => {
    // `VALIDATED_DEVICE_FIELDS` drives "focus the first invalid box". An error on a field missing from
    // it would be an error nothing can focus — the form would refuse to submit and look inert.
    const errors = errorsOf(
      validateDevice({
        name: "",
        deviceType: "Kiosk",
        readerModel: characters(READER_MODEL_MAX_LENGTH + 1),
        isActive: true,
      }),
    );

    const reported = Object.keys(errors);
    expect(reported.length).toBeGreaterThan(0);
    for (const field of reported) {
      expect(VALIDATED_DEVICE_FIELDS).toContain(field);
    }
  });

  it.each(DEVICE_TYPES)("carries %s into the request unchanged", (deviceType) => {
    expect(requestOf(validateDevice(baseDraft({ deviceType }))).deviceType).toBe(deviceType);
  });

  it("carries isActive through untouched", () => {
    expect(requestOf(validateDevice(baseDraft({ isActive: false }))).isActive).toBe(false);
  });

  it("starts a new device as an active Kiosk", () => {
    // The server substitutes `Kiosk` for a null or blank type, so a new form starting anywhere else
    // would disagree with the API about what "I did not choose" means.
    expect(EMPTY_DEVICE_DRAFT.deviceType).toBe("Kiosk");
    expect(EMPTY_DEVICE_DRAFT.isActive).toBe(true);
  });

  it("names every device type the picker can offer", () => {
    // A type in `DEVICE_TYPES` with no label renders as an empty `MenuItem`.
    expect(Object.keys(DEVICE_TYPE_LABELS).sort()).toEqual([...DEVICE_TYPES].sort());
  });
});
