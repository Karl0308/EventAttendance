// What is in a device form's boxes, the rules those boxes are held to, and the request they become.
//
// `studentDraft.ts`'s sibling and deliberately the same shape: `POST /devices` and `PUT /devices/{id}`
// take the *same* body and are checked by the *same* server code (`DeviceService.Validate`), so a
// second copy of these rules beside the edit dialog would be two readings of one contract with nothing
// binding them.
//
// Free of React and of `api.ts`: pure functions over plain values.

import { DEVICE_TYPES } from "./types";
import type { Device, DeviceTypeName, DeviceWriteRequest } from "./types";
import { cleanText } from "./studentDraft";

// ---------------------------------------------------------------------------------------------
// The server's rules, restated
// ---------------------------------------------------------------------------------------------
//
// Both limits are `DeviceText` in `EAMS.Domain/DeviceEntities.cs`, checked again here — not because
// the client is trusted, but because a round trip to be told a name is four characters too long is a
// bad way to learn it. Restated rather than fetched: no endpoint publishes them, so the honest
// description is "a copy that will drift if §4.10 changes".

/** `Devices.Name nvarchar(100)`. */
export const DEVICE_NAME_MAX_LENGTH = 100;

/** `Devices.ReaderModel nvarchar(100)`. */
export const READER_MODEL_MAX_LENGTH = 100;

/**
 * §4.10's most common reader, and the value the server itself substitutes for a null or blank
 * `DeviceType`. Stated here so a new form starts on the same value the API would have chosen, rather
 * than on whichever member happens to be first in the list.
 */
const DEFAULT_DEVICE_TYPE: DeviceTypeName = "Kiosk";

/** What the type picker calls each value. The values themselves are the wire's, and are not changed. */
export const DEVICE_TYPE_LABELS: Record<DeviceTypeName, string> = {
  Kiosk: "Kiosk — fixed reader at a door",
  Mobile: "Mobile — phone or tablet running the capture app",
  Handheld: "Handheld — portable reader carried by a marshal",
};

// ---------------------------------------------------------------------------------------------
// The device draft
// ---------------------------------------------------------------------------------------------

/**
 * What is in the boxes, which is not what is sent. `readerModel` is `string` because a text box cannot
 * hold `null` while it is being retyped; the conversion happens once, in `validateDevice`.
 *
 * **There is deliberately nothing key-shaped here.** A device key is minted by the server or it is not
 * minted at all — `DeviceKey.Issue` takes no input — so a draft field for one would be a box whose
 * value has nowhere to go, and the box itself would suggest that supplying a key is a thing an
 * operator may do.
 */
export interface DeviceDraft {
  name: string;
  deviceType: DeviceTypeName;
  readerModel: string;
  isActive: boolean;
}

export type DeviceDraftField = keyof DeviceDraft;

export const EMPTY_DEVICE_DRAFT: DeviceDraft = {
  name: "",
  deviceType: DEFAULT_DEVICE_TYPE,
  readerModel: "",
  isActive: true,
};

/**
 * The fields that can carry an error, **in the order they appear on screen** — which is what makes
 * "focus the first invalid one" land on the first invalid one the user can see. `deviceType` and
 * `isActive` are absent because a select and a checkbox cannot hold a value outside their own options.
 */
export const VALIDATED_DEVICE_FIELDS = ["name", "readerModel"] as const;

export type ValidatedDeviceField = (typeof VALIDATED_DEVICE_FIELDS)[number];

export type DeviceFieldErrors = Partial<Record<ValidatedDeviceField, string>>;

export const NO_DEVICE_ERRORS: DeviceFieldErrors = {};

/**
 * Stable ids, because MUI derives `<label for>` and the `aria-describedby` that ties an input to its
 * error text from the `id` given to the `TextField` — and because "focus the first invalid box" needs
 * something to look the box up by. Built from a prefix so the register and edit dialogs cannot collide.
 */
export const deviceFieldIdsFor = (prefix: string): Record<DeviceDraftField, string> => ({
  name: `${prefix}-name`,
  deviceType: `${prefix}-device-type`,
  readerModel: `${prefix}-reader-model`,
  isActive: `${prefix}-is-active`,
});

/**
 * The wire's device type as one this client can send back, or nothing. `Device.deviceType` is `string`
 * because a response cannot prove a union; this is where that becomes a decision instead of an
 * assumption.
 *
 * Case-insensitive, matching `DomainValueSet.TryNormalize` on the server, and it answers with the
 * **canonical** spelling rather than with what arrived — so a device stored as `kiosk` fills the
 * picker with `Kiosk` and is sent back as `Kiosk`, which is the value the column already holds after
 * the server's own normalisation.
 */
export const knownDeviceType = (wire: string): DeviceTypeName | undefined =>
  DEVICE_TYPES.find((type) => type.toLowerCase() === wire.trim().toLowerCase());

/**
 * The boxes filled from a device that already exists — the edit form's starting state.
 *
 * `deviceType` is narrowed against the same set the picker is built from rather than asserted, and the
 * fallback is the last line of defence rather than the first: `Devices.tsx` refuses to open the form at
 * all for a type this build does not recognise, because falling back silently is how a Handheld becomes
 * a Kiosk on a save someone made to fix a typo in its name.
 */
export function draftFromDevice(device: Device): DeviceDraft {
  return {
    name: device.name,
    deviceType: knownDeviceType(device.deviceType) ?? DEFAULT_DEVICE_TYPE,
    readerModel: device.readerModel ?? "",
    isActive: device.isActive,
  };
}

// ---------------------------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------------------------

/**
 * Either the request, or why there is not one. One function, so "may this be sent" and "what exactly
 * is sent" cannot disagree.
 */
export type DeviceValidated =
  | { ok: true; request: DeviceWriteRequest }
  | { ok: false; errors: DeviceFieldErrors };

const tooLong = (max: number, actual: number) => `${max} characters at most; this is ${actual}.`;

/**
 * §4.10's column rules, applied to the values that will actually be written.
 *
 * `cleanText` rather than `.trim()`, for the reason it records: the server trims before measuring, and
 * a field holding nothing but a zero-width space is not empty by `trim()` but cleans to nothing — so a
 * client measuring the raw box would show a valid-looking form and earn a 400 naming a field the user
 * believes they filled in. The checked value and the sent value are the same string, which is the
 * whole point of cleaning once.
 */
export function validateDevice(draft: DeviceDraft): DeviceValidated {
  const errors: DeviceFieldErrors = {};

  const name = cleanText(draft.name);
  if (name === null) {
    errors.name = "A device name is required.";
  } else if (name.length > DEVICE_NAME_MAX_LENGTH) {
    errors.name = tooLong(DEVICE_NAME_MAX_LENGTH, name.length);
  }

  const readerModel = cleanText(draft.readerModel);
  if (readerModel !== null && readerModel.length > READER_MODEL_MAX_LENGTH) {
    errors.readerModel = tooLong(READER_MODEL_MAX_LENGTH, readerModel.length);
  }

  // The `name === null` arm is what narrows the required value for the request below; it cannot fire on
  // its own, because it set an error above. The `errors` check is the real condition and it is first.
  if (Object.keys(errors).length > 0 || name === null) return { ok: false, errors };

  // Field by field, never `{ ...device }`: a body's contents are a decision rather than an inheritance,
  // and a spread of a `Device` would carry six read-only key-lifecycle fields into a write.
  return {
    ok: true,
    request: {
      name,
      deviceType: draft.deviceType,
      readerModel,
      isActive: draft.isActive,
    },
  };
}

// ---------------------------------------------------------------------------------------------
// Why a device can or cannot tap — the one thing the list leads with
// ---------------------------------------------------------------------------------------------

/**
 * `Device.hasActiveKey` is one boolean folding three facts together, which is right for "can this tap
 * right now" and useless for "so what do I press". This splits the `false` case back into the things it
 * can be, because **each one has a different remedy**: issue a key, un-revoke by rotating, switch the
 * device back on — or, for the residue, rotate because nothing else will help.
 *
 * `kind` is what UI branches on; `label` and `detail` are prose. The label is always rendered as
 * **text** and never as colour alone — the chip colours repeat what the words already say.
 */
export type KeyStandingKind =
  | "active"
  | "never-issued"
  | "revoked"
  | "device-inactive"
  /**
   * The server says it cannot authenticate, and none of the three explanations fits: it holds a key id,
   * the key is not revoked, the device is switched on. `HasActiveKey` is `HasKey && revokedAt is null
   * && IsActive`, and `HasKey` needs **both** `ApiKeyId` and `ApiKeyHash` — so the reachable cause is a
   * row with an id and no hash (contemplated in `EAMS.Domain/DeviceEntities.cs`). Its own kind rather
   * than a fall-through, because the remedy differs from every other arm: only issuing a new key writes
   * a hash, and the checkbox this row's neighbour points at is already ticked.
   */
  | "indeterminate";

export interface KeyStanding {
  kind: KeyStandingKind;
  /** Short enough for a grid cell, and a complete sentence to a screen reader reading it alone. */
  label: string;
  /** What it means and what would change it. Shown beside the row's actions, not in the cell. */
  detail: string;
  /**
   * `!isActive`, carried alongside the kind rather than folded into it.
   *
   * Two arms are reported *before* the active flag is looked at — `revoked` and `never-issued` — because
   * their remedy is the one to lead with. But both of those remedies are then **incomplete** on a device
   * that is also switched off: rotating clears `ApiKeyRevokedAt` and writes a hash, and `HasActiveKey`
   * stays `false` anyway because `IsActive` is still `false`. The operator spends the one-shot token,
   * walks to the reader, types it in, and it still does not tap.
   *
   * So the fact travels with the standing and each of those arms appends a clause when it is true. Not a
   * sixth kind: the *first* thing to do is unchanged, and a "revoked and switched off" chip would split
   * a category whose remedy is the same in both halves. Not a re-ordering either — that would put the
   * checkbox first on a device the checkbox cannot fix.
   */
  deviceOff: boolean;
}

/**
 * Appended to the arms that report before `isActive` is tested, and only where it is true. Phrased as
 * the *second* step rather than a correction, because the first step those arms name is still right.
 */
const ALSO_SWITCHED_OFF =
  " This device is also switched off, so a new key alone will not bring it back — edit it and tick " +
  "“Active” as well.";

/**
 * The one place each standing is named. Exported because the grid chips, the page legend and this
 * module's own `detail` sentences all have to agree on the words — a legend that says "Switched off"
 * beside a chip reading something else is a legend for a different screen. The *prose* stays
 * duplicated on purpose: `detail` addresses the one device in front of the operator, the legend
 * describes a category.
 */
export const KEY_STANDING_LABELS: Record<KeyStandingKind, string> = {
  active: "Can tap",
  "never-issued": "No key",
  revoked: "Key revoked",
  "device-inactive": "Switched off",
  indeterminate: "Key unusable",
};

/**
 * Ordered so the *first* true thing is the one the operator has to act on. `never-issued` is tested
 * before `revoked` because a device that has never held a key has no revocation to report, and
 * `revoked` before `device-inactive` because rotating is what clears a revocation whichever way the
 * active flag is set — reporting "switched off" first would send someone to a checkbox that will not
 * bring the credential back.
 *
 * Every arm is a **test**, including the last-but-one. `device-inactive` used to be the fall-through,
 * which meant it also caught the case it is the worst possible answer for: a device that is already
 * active was told to tick a box that is already ticked, and promised the same key back.
 */
export function keyStanding(device: Device): KeyStanding {
  const deviceOff = !device.isActive;

  if (device.hasActiveKey) {
    return {
      kind: "active",
      label: KEY_STANDING_LABELS.active,
      detail: "This device holds a key that has not been revoked, and the device is switched on.",
      deviceOff,
    };
  }

  if (device.apiKeyId === undefined) {
    return {
      kind: "never-issued",
      label: KEY_STANDING_LABELS["never-issued"],
      detail:
        "This device has never been issued a key, so it cannot authenticate. Issuing one is what " +
        "“Issue a new key” does — the token is shown once and cannot be retrieved afterwards." +
        (deviceOff ? ALSO_SWITCHED_OFF : ""),
      deviceOff,
    };
  }

  if (device.apiKeyRevokedAt !== undefined) {
    return {
      kind: "revoked",
      label: KEY_STANDING_LABELS.revoked,
      detail:
        "This device's key was revoked, so it is refused with a 403 rather than merely not " +
        "recognised. Issuing a new key clears the revocation and is the only way back." +
        (deviceOff ? ALSO_SWITCHED_OFF : ""),
      deviceOff,
    };
  }

  if (!device.isActive) {
    return {
      kind: "device-inactive",
      label: KEY_STANDING_LABELS["device-inactive"],
      deviceOff,
      detail:
        "This device holds a valid, unrevoked key, but the device itself is marked inactive so the " +
        "key is not accepted. Editing it and ticking “Active” restores the same key — nothing needs " +
        "rotating.",
    };
  }

  return {
    kind: "indeterminate",
    label: KEY_STANDING_LABELS.indeterminate,
    deviceOff,
    detail:
      "This device is switched on and its key is not revoked, yet the server still refuses it — the " +
      "stored credential is incomplete, so there is nothing to switch back on. Issuing a new key is " +
      "the only remedy.",
  };
}
