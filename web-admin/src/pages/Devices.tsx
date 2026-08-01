// The devices screen — §4.10 / §6.6 — and the one page in this app that hands out a credential.
//
// It is built around a single asymmetry. Everything on the list/detail path is safe to render forever:
// the name, the type, the public key id, the lifecycle timestamps. The plaintext token is safe to
// render exactly once, in the reply that minted it, and never again — so the two are kept structurally
// apart. `Device` has no key field, `toDevice` has no line that could read one, and the only component
// that ever sees a token is `DeviceKeyDialog`, which the *page* opens from a mutation outcome. No row,
// no cell and no Snackbar on this screen can reach one.
//
// The shape otherwise follows `Students.tsx`: the page owns every write, so dismissing a dialog does
// not end the request that dialog started, and a failure that arrives after its dialog is gone is
// announced rather than lost. That matters more here than there — the outcome a dismissed register
// dialog would take with it is the only copy of a device key.

import { useLayoutEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Chip,
  IconButton,
  Snackbar,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import VpnKeyIcon from "@mui/icons-material/VpnKey";
import KeyOffIcon from "@mui/icons-material/KeyOff";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import NewDeviceDialog from "../components/NewDeviceDialog";
import EditDeviceDialog from "../components/EditDeviceDialog";
import DeviceKeyDialog from "../components/DeviceKeyDialog";
import RegenerateDeviceKeyDialog from "../components/RegenerateDeviceKeyDialog";
import RevokeDeviceKeyDialog from "../components/RevokeDeviceKeyDialog";
import { KEY_STANDING_LABELS, keyStanding, knownDeviceType } from "../deviceDraft";
import type { KeyStandingKind } from "../deviceDraft";
import type { Device, DeviceKeyIssued, DeviceWriteRequest } from "../types";

const loadDevices = () => api.listDevices();

const NO_DEVICES = "No devices are registered yet.";
const NO_MATCH = "No device matches that search.";

/** What a column reads as when the device has no value for it. */
const NO_VALUE = "—";

/** What the Snackbar is currently saying, and how loudly. As `Students.tsx`, for the same reasons. */
interface Notice {
  severity: "success" | "error";
  text: string;
}

const NOTICE_MS = 6000;

/**
 * A failure does not time out — it is only ever announced here when the dialog that would have carried
 * it is gone, so it is the only copy the user gets. MUI reads `null` as "stay until dismissed".
 */
const NO_AUTO_HIDE = null;

/**
 * The token that was just minted, and whether it replaced a working one.
 *
 * Held as page state rather than passed straight through, because the dialog that produced it is
 * unmounted the moment it arrives: a register succeeds, the form closes, and this takes its place. It
 * is cleared only by the acknowledge button on `DeviceKeyDialog`, never by a re-read, never by a
 * failure elsewhere on the page, and never automatically — every one of those would be a way to lose
 * the only copy of a credential.
 */
interface IssuedKey {
  issued: DeviceKeyIssued;
  replaced: boolean;
}

// ---------------------------------------------------------------------------------------------
// Whether this build may edit a device at all
// ---------------------------------------------------------------------------------------------
//
// `PUT /devices/{id}` is a **full replacement**, so this client can only offer the form for a device it
// can reproduce exactly. One thing stops it: a `deviceType` outside the three `DeviceWriteRequest` can
// carry, which the form would send back as `Kiosk` — quietly reclassifying a device on a save someone
// made to fix a typo in its name. A refusal rather than a fallback, for the reason `Students.tsx`
// records: every fallback here changes data on a save the user made for an unrelated reason.
//
// The name has no equivalent arm because it cannot get this far: `DeviceDto` declares it non-nullable
// and `toDevice` narrows it as required, so a server that stopped sending it fails at the seam.

type Editability = { can: true } | { can: false; reason: string };

function editabilityOf(device: Device): Editability {
  if (knownDeviceType(device.deviceType) === undefined) {
    return {
      can: false,
      reason:
        `This device's type (“${device.deviceType}”) is not one this admin build recognises, so the ` +
        "edit form cannot send it back unchanged — saving would rewrite it. This build and the API " +
        "are probably different versions.",
    };
  }
  return { can: true };
}

/**
 * The Chip colour for a key standing.
 *
 * Colour is never the only carrier: `keyStanding` gives every state a text label and the chip renders
 * it, so a monochrome screen or a red-green-blind reader loses nothing. The colour is a second channel
 * for the same fact — `error` for revoked, which is the one an operator has to notice at a glance, and
 * `warning` for a device switched off, which is fixable from the edit form rather than alarming.
 */
const standingColor = (kind: KeyStandingKind): "success" | "default" | "error" | "warning" => {
  switch (kind) {
    case "active":
      return "success";
    case "never-issued":
      return "default";
    case "revoked":
      return "error";
    case "device-inactive":
      return "warning";
    // `error`, alongside revoked rather than alongside switched-off: this device is broken in a way no
    // checkbox fixes, and the only way out is the same rotation a revoked one needs.
    case "indeterminate":
      return "error";
    default: {
      // A guard, not a fallback: a standing added to the union without a colour here is a compile
      // error rather than an unlabelled grey chip nobody notices.
      const unhandled: never = kind;
      throw new Error(`Unhandled KeyStandingKind: ${String(unhandled)}`);
    }
  }
};

/** An instant as a person reads it, or the dash. */
const whenText = (iso: string | undefined): string =>
  iso === undefined ? NO_VALUE : new Date(iso).toLocaleString();

export default function Devices() {
  // No deps: the read takes nothing, so it runs once per mount, and again on Retry or after a write.
  const devices = useApiResource(loadDevices, []);
  const [search, setSearch] = useState("");

  /**
   * All four writes live here rather than inside the dialogs that start them — D1's shape, and the
   * reason for it is sharper on this page than anywhere else: two of these writes answer with a
   * credential that exists nowhere else, and a dialog owning its own write ends that write when it
   * unmounts. A user who pressed Escape while a registration was in flight would destroy the only copy
   * of the key it was about to return.
   */
  const register = useApiMutation((request: DeviceWriteRequest) => api.registerDevice(request));
  const edit = useApiMutation((deviceId: string, request: DeviceWriteRequest) =>
    api.updateDevice(deviceId, request),
  );
  const regenerate = useApiMutation((deviceId: string) => api.regenerateDeviceKey(deviceId));
  const revoke = useApiMutation((deviceId: string) => api.revokeDeviceKey(deviceId));

  // Mounting a dialog only while it is open is what keeps its form honest: every open starts from an
  // empty or freshly-filled draft with no leftover text.
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Device | undefined>(undefined);
  const [regenerating, setRegenerating] = useState<Device | undefined>(undefined);
  const [revoking, setRevoking] = useState<Device | undefined>(undefined);

  /** The one-shot reveal. See `IssuedKey` — nothing but the acknowledge button clears this. */
  const [issuedKey, setIssuedKey] = useState<IssuedKey | undefined>(undefined);

  const rows = devices.data;

  /**
   * Whether each dialog is on screen, read at the moment a write *settles* rather than from the closure
   * that started it. The state captured there is a render old, and the interesting case is exactly the
   * one where it changed in between — the user dismissed the dialog while the write was in flight, so
   * there is no longer an alert for the failure to appear in.
   */
  const createOpen = useRef(creating);
  const editOpen = useRef(editing !== undefined);
  const regenerateOpen = useRef(regenerating !== undefined);
  const revokeOpen = useRef(revoking !== undefined);
  useLayoutEffect(() => {
    createOpen.current = creating;
    editOpen.current = editing !== undefined;
    regenerateOpen.current = regenerating !== undefined;
    revokeOpen.current = revoking !== undefined;
  });

  // Two pieces rather than one `Notice | undefined` driving both, as `Events.tsx` records: MUI keeps a
  // Snackbar's children mounted through its closing fade, so clearing the notice to close it would
  // blank the text mid-fade and leave an empty bar on screen for the length of the transition.
  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);

  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const filtered = useMemo(() => {
    if (rows === undefined) return [];
    const q = search.toLowerCase().trim();
    if (!q) return rows;
    // Key id as well as name, and that is the point of showing it: an operator arrives at this screen
    // holding a key id out of a log line, and needs to find out which door it belongs to.
    return rows.filter(
      (d) =>
        d.name.toLowerCase().includes(q) ||
        (d.apiKeyId !== undefined && d.apiKeyId.toLowerCase().includes(q)) ||
        (d.readerModel !== undefined && d.readerModel.toLowerCase().includes(q)),
    );
  }, [rows, search]);

  // ------------------------------------------------------------------------------------- opening
  //
  // Every one of these resets its mutation first. A failure from an attempt the user walked away from
  // is still in the hook, and without the reset it would greet them as though it were about the thing
  // they have not decided on yet.

  /**
   * Why a key-issuing action must not start right now, or `undefined` if it may.
   *
   * **The two key-issuing writes are separate `useApiMutation` instances, so nothing serialises them
   * against each other** — `inFlight` is per-hook, and both settle by calling `setIssuedKey`. Register,
   * press Cancel while it is in flight, rotate a row: the rotation reveals token A, the registration
   * then lands and overwrites the reveal with token B, and token A is gone. On a rotation that means a
   * reader is already offline with no way back except rotating again.
   *
   * The answer is to refuse rather than to queue, which is JJ's call and the right one: a queued second
   * issue would mint a credential the operator has stopped expecting, and the reveal is a thing they
   * have to *act* on, not a notification to stack up. One token on screen at a time, acknowledged
   * before the next may be asked for.
   *
   * Note what this does *not* guard: `edit` and `revoke`. Neither mints anything, so neither can take a
   * reveal away.
   *
   * **Part of why this refusal is total is that every dialog on this page is modal by default** — none
   * passes `disableEnforceFocus`, `hideBackdrop`, `keepMounted` or `disablePortal` — so two of them
   * cannot be interactable at once, and there is no second live opener to reach an issuing action from
   * while a reveal is up. A non-modal drawer here, or a keyboard shortcut that opens a dialog directly,
   * would reopen that hole while looking like a styling change. It is written down because nothing in
   * the code enforces it.
   */
  const keyIssueRefusal = (): string | undefined => {
    if (issuedKey !== undefined) {
      return (
        "A device key is on screen and has not been acknowledged yet. Save it and press “I have " +
        "saved the key — close” first: issuing another one now would replace it, and it is the only " +
        "copy that will ever exist."
      );
    }
    if (register.status === "running" || regenerate.status === "running") {
      return (
        "A key is already being issued and its token has not come back yet. Wait for it and " +
        "acknowledge the key you are shown first — starting a second one now would overwrite the " +
        "first token before anyone could read it."
      );
    }
    return undefined;
  };

  const openCreate = () => {
    // Refused by *saying so* rather than by a greyed button, for the reason `openEdit` records below:
    // a control that greys out without a reason leaves the user guessing, and here the reason is also
    // the instruction — the way forward is to acknowledge the key they were just shown.
    const refusal = keyIssueRefusal();
    if (refusal !== undefined) {
      announce({ severity: "error", text: refusal });
      return;
    }
    register.reset();
    setCreating(true);
  };

  const openEdit = (device: Device) => {
    const editable = editabilityOf(device);
    if (!editable.can) {
      // Refused by *saying so* rather than by a greyed button, for the reason `Students.RowActions`
      // records: a disabled control in a grid row has nowhere to put its reason and drops out of the
      // tab order, so a keyboard user can never reach an explanation even if one existed.
      announce({ severity: "error", text: editable.reason });
      return;
    }
    edit.reset();
    setEditing(device);
  };

  const openRegenerate = (device: Device) => {
    const refusal = keyIssueRefusal();
    if (refusal !== undefined) {
      announce({ severity: "error", text: refusal });
      return;
    }
    regenerate.reset();
    setRegenerating(device);
  };

  const openRevoke = (device: Device) => {
    revoke.reset();
    setRevoking(device);
  };

  // ------------------------------------------------------------------------------------ settling
  //
  // Each of these re-reads the list whichever way the write settled: a write that failed on the way
  // back may still have been applied, and a list that goes on asserting the state from before it is
  // what has someone press it a second time.

  const submitCreate = (request: DeviceWriteRequest) => {
    // `run` never rejects; it answers with an outcome. The floating promise is deliberate and marked.
    void register.run(request).then((settled) => {
      // `ignored` — a register was already in flight and this submit sent nothing.
      if (settled.outcome === "ignored") return;
      devices.reload();

      if (settled.outcome === "succeeded") {
        setCreating(false);
        // The reveal replaces the form. Note what is NOT here: the token is not announced, not logged
        // and not put anywhere but the dialog — a Snackbar carrying a credential would leave it on
        // screen behind whatever the user does next, and would auto-dismiss it.
        setIssuedKey({ issued: settled.data, replaced: false });
        return;
      }

      if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const submitEdit = (deviceId: string, request: DeviceWriteRequest) => {
    void edit.run(deviceId, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      devices.reload();

      if (settled.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to “${settled.data.name}”.` });
        return;
      }

      if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmRegenerate = (device: Device) => {
    void regenerate.run(device.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      devices.reload();

      if (settled.outcome === "succeeded") {
        setRegenerating(undefined);
        setIssuedKey({ issued: settled.data, replaced: true });
        return;
      }

      if (!regenerateOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmRevoke = (device: Device) => {
    void revoke.run(device.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      devices.reload();

      if (settled.outcome === "succeeded") {
        setRevoking(undefined);
        // Says what is now true and what the way back is, rather than "done": an operator who thinks a
        // revoke is reversible goes looking for an un-revoke button that does not exist.
        announce({
          severity: "success",
          text: `“${device.name}” can no longer authenticate. Issue a new key to put it back in service.`,
        });
        return;
      }

      if (!revokeOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  // --------------------------------------------------------------------------------------- grid

  const cols: GridColDef<Device>[] = [
    { field: "name", headerName: "Device", flex: 1, minWidth: 180 },
    {
      field: "hasActiveKey",
      headerName: "Can it tap?",
      width: 150,
      // The value the column sorts and filters on is the *label*, so ordering the grid by this column
      // groups devices by what the cell says rather than by a boolean that three different cells share.
      valueGetter: (_v, row) => keyStanding(row).label,
      renderCell: (p) => (
        <Chip
          size="small"
          label={keyStanding(p.row).label}
          color={standingColor(keyStanding(p.row).kind)}
          variant="outlined"
        />
      ),
    },
    {
      field: "apiKeyId",
      headerName: "Key ID",
      width: 130,
      // Monospace: this is the value an operator compares character by character against a log line,
      // and a proportional font makes that a worse job than it has to be. It is the PUBLIC half — safe
      // to show, safe to log, and not a credential. The secret half is never on this screen.
      renderCell: (p) => (
        <Box component="span" sx={{ fontFamily: "monospace" }}>
          {p.row.apiKeyId ?? NO_VALUE}
        </Box>
      ),
    },
    { field: "deviceType", headerName: "Type", width: 110 },
    {
      field: "readerModel",
      headerName: "Reader model",
      width: 150,
      valueGetter: (_v, row) => row.readerModel ?? NO_VALUE,
    },
    {
      // Load-bearing, not decorative. `RegenerateDeviceKeyDialog`'s withheld-resend sentence sends the
      // operator here after a rotation whose outcome is unknown: if this moved, the key was minted and
      // is unrecoverable, so rotate once more deliberately. "Key last used" cannot answer that — a key
      // nobody has typed in yet reads “—” whether the rotation landed or not. It is also the only column
      // that distinguishes a key minted this morning from one minted last year.
      field: "apiKeyIssuedAt",
      headerName: "Key issued",
      width: 180,
      valueGetter: (_v, row) => whenText(row.apiKeyIssuedAt),
    },
    {
      field: "apiKeyLastUsedAt",
      headerName: "Key last used",
      width: 180,
      valueGetter: (_v, row) => whenText(row.apiKeyLastUsedAt),
    },
    {
      field: "lastSeenAt",
      headerName: "Last seen",
      width: 180,
      valueGetter: (_v, row) => whenText(row.lastSeenAt),
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 150,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <RowActions
          device={p.row}
          onEdit={openEdit}
          onRegenerate={openRegenerate}
          onRevoke={openRevoke}
        />
      ),
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Devices
        </Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate}>
          Register device
        </Button>
      </Stack>

      {devices.status === "loading" && <LoadingState label="Loading devices…" />}

      {devices.status === "error" && (
        <ErrorState subject="devices" error={devices.error} onRetry={devices.reload} />
      )}

      {devices.status === "ready" && (
        <>
          <Stack direction="row" spacing={2} sx={{ mb: 2 }} alignItems="center">
            <TextField
              size="small"
              label="Search name, key ID or model"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              sx={{ width: 320 }}
            />
            {/* Mounted whether or not a re-read is running, so the live region exists in the DOM before
                anything is put into it — a region inserted and populated in the same commit is
                announced unreliably. */}
            <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center" }}>
              {devices.refreshing && (
                <Typography variant="body2" color="text.secondary">
                  Refreshing…
                </Typography>
              )}
            </Box>
          </Stack>

          {filtered.length === 0 ? (
            // Two different nothings, and the user is told which.
            <EmptyState message={devices.data.length === 0 ? NO_DEVICES : NO_MATCH} />
          ) : (
            <>
              <div style={{ height: 520, width: "100%" }}>
                <DataGrid
                  rows={filtered}
                  columns={cols}
                  getRowId={(r) => r.id}
                  disableRowSelectionOnClick
                  pageSizeOptions={[10, 25]}
                  initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
                />
              </div>
              <StandingLegend />
            </>
          )}
        </>
      )}

      {/* Outside the `ready` branch on purpose: a re-read that fails replaces that branch with the
          error state, and a dialog living inside it would be torn down mid-write, taking the failure
          alert — or, for the two key-issuing writes, the only copy of the key — with it. */}
      {creating && (
        <NewDeviceDialog
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={register.status === "running"}
          failure={register.status === "failed" ? { error: register.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <EditDeviceDialog
          device={editing}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {regenerating !== undefined && (
        <RegenerateDeviceKeyDialog
          device={regenerating}
          onClose={() => setRegenerating(undefined)}
          onConfirm={() => confirmRegenerate(regenerating)}
          running={regenerate.status === "running"}
          failure={regenerate.status === "failed" ? { error: regenerate.error } : undefined}
        />
      )}

      {revoking !== undefined && (
        <RevokeDeviceKeyDialog
          device={revoking}
          onClose={() => setRevoking(undefined)}
          onConfirm={() => confirmRevoke(revoking)}
          running={revoke.status === "running"}
          failure={revoke.status === "failed" ? { error: revoke.error } : undefined}
        />
      )}

      {/* Last, so it sits above anything else that could still be mounted. Cleared only from its own
          acknowledge button — no timeout, no re-read, no other outcome takes it down. */}
      {issuedKey !== undefined && (
        <DeviceKeyDialog
          issued={issuedKey.issued}
          replaced={issuedKey.replaced}
          // Both mutations are reset here, not only the page state. The dialog tells the operator the
          // token is gone the moment they acknowledge it, and until this line that was true of one
          // copy out of two: `register`/`regenerate` go on holding `{status:"succeeded", data}` — with
          // the plaintext key in it — until the *next* open happens to reset them, which may be never.
          // Whichever one produced this reveal, resetting both is correct and neither can be running.
          onClose={() => {
            setIssuedKey(undefined);
            register.reset();
            regenerate.reset();
          }}
        />
      )}

      {/* Outside the branches on purpose: a re-read that fails replaces the ready branch, and a
          Snackbar living inside it would take the message it was just given down with it — at exactly
          the moment the message is a failure the user needs to read. */}
      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {/* Rendered from the notice rather than defaulted from it: a fallback severity would paint a
            failure green for one render if the two ever came apart. */}
        {notice ? (
          <Alert
            severity={notice.severity}
            role={notice.severity === "error" ? "alert" : "status"}
            onClose={() => setAnnouncing(false)}
          >
            {notice.text}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Box>
  );
}

/**
 * What the four chips mean, in prose, permanently on the page.
 *
 * Not a tooltip. The distinction between "no key", "revoked" and "switched off" is the whole reason
 * `hasActiveKey` is one boolean and these are four states, and each one has a *different* remedy — so
 * hiding it behind hover would put it out of reach of every touch and keyboard user, which is this
 * codebase's standing complaint about `title` as an explanation.
 */
const LEGEND_HEADING_ID = "device-standing-legend-heading";

function StandingLegend() {
  return (
    <Box sx={{ mt: 2 }}>
      {/* The heading is a sibling of the list, not a child of it. A `<dl>` may contain only `dt`/`dd`
          (and wrappers of them), so a `<p>` inside one is invalid markup that a screen reader is free
          to drop or to misattribute; `aria-labelledby` is what ties the two back together. */}
      <Typography id={LEGEND_HEADING_ID} variant="subtitle2" component="p" sx={{ mb: 1 }}>
        What “Can it tap?” means
      </Typography>
      <Box component="dl" aria-labelledby={LEGEND_HEADING_ID} sx={{ m: 0 }}>
        {LEGEND.map(({ kind, text }) => (
          <Stack key={kind} direction="row" spacing={1} alignItems="flex-start" sx={{ mb: 0.5 }}>
            <Box component="dt" sx={{ m: 0, minWidth: 120 }}>
              <Chip
                size="small"
                label={KEY_STANDING_LABELS[kind]}
                color={standingColor(kind)}
                variant="outlined"
              />
            </Box>
            <Typography component="dd" variant="body2" color="text.secondary" sx={{ m: 0 }}>
              {text}
            </Typography>
          </Stack>
        ))}
      </Box>
    </Box>
  );
}

/**
 * The legend's rows. Written out rather than derived from `keyStanding`, which needs a `Device` to
 * answer and would have to be fed five fabricated ones — and the wording differs on purpose: the
 * standings' own `detail` addresses one device the operator is looking at, these describe a category.
 *
 * The *labels* are not written out: they come from `KEY_STANDING_LABELS`, so a legend entry cannot end
 * up naming a chip differently from the chip it is explaining. Only the prose is local.
 */
const LEGEND: readonly { kind: KeyStandingKind; text: string }[] = [
  {
    kind: "active",
    text: "Holds a key, not revoked, device switched on. Nothing to do.",
  },
  {
    kind: "never-issued",
    text: "Never issued a key. Use “Issue a new key” — the token is shown once and cannot be retrieved after.",
  },
  {
    kind: "revoked",
    text:
      "Its key was burned, so it is refused outright. Issuing a new key is the only way back — and if " +
      "the device is also switched off, tick Active too, or the new key will be refused as well.",
  },
  {
    kind: "device-inactive",
    text: "Marked inactive, so its key is not accepted — but the key is kept. Edit it and tick Active; nothing needs rotating.",
  },
  {
    kind: "indeterminate",
    text: "Switched on and not revoked, yet still refused — the stored key is incomplete. Only issuing a new key fixes it.",
  },
];

/**
 * The three row actions.
 *
 * Icon-only, so each carries an `aria-label` naming **both the action and the device**: a screen reader
 * reads a button's label out of context, and a grid of twenty rows would otherwise announce "Edit,
 * Issue, Revoke" twenty times with nothing to tell them apart.
 *
 * None is ever disabled, including Edit, which `openEdit` can still refuse — a disabled control in a
 * grid row has nowhere beside it for the reason and drops out of the tab order, so pressing it and
 * being told why is strictly more accessible than not being able to press it at all.
 *
 * Revoke is offered on every row rather than only where there is a key to burn, and that is deliberate
 * for the same reason: the endpoint is idempotent, so pressing it on a device with no key is a 200 and
 * a no-op, and the confirmation explains what revoking is *for*. Hiding it would leave the only place
 * that distinguishes revoke from rotate unreachable exactly when someone is guessing between them.
 */
function RowActions({
  device,
  onEdit,
  onRegenerate,
  onRevoke,
}: {
  device: Device;
  onEdit: (device: Device) => void;
  onRegenerate: (device: Device) => void;
  onRevoke: (device: Device) => void;
}) {
  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton
        // 44 × 44, the smallest touch target that can be hit reliably; MUI's own default is 40 and
        // these three sit side by side in a narrow column.
        sx={{ p: 1.25 }}
        onClick={() => onEdit(device)}
        aria-label={`Edit ${device.name}`}
        title={`Edit ${device.name}`}
      >
        <EditIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color="warning"
        onClick={() => onRegenerate(device)}
        aria-label={`Issue a new key for ${device.name}`}
        title={`Issue a new key for ${device.name}`}
      >
        <VpnKeyIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color="error"
        onClick={() => onRevoke(device)}
        aria-label={`Revoke the key for ${device.name}`}
        title={`Revoke the key for ${device.name}`}
      >
        <KeyOffIcon fontSize="small" />
      </IconButton>
    </Stack>
  );
}
