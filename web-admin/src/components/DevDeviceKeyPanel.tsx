// The box a developer pastes a device key into, so that "Simulate RFID tap" can actually tap.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS IS NOT A SECURITY HOLE, AND WHAT KEEPS IT THAT WAY
// ---------------------------------------------------------------------------------------------
//
// `POST /attendance/tap` needs a credential that can *write* attendance, and `api.ts`'s note is right
// that this bundle must not carry one: `import.meta.env.VITE_…` is inlined at build time, so a key
// supplied that way is published as a static asset in the GitHub Pages artefact. A value typed into
// this box at runtime is the opposite — it exists in one tab, put there by someone who already has
// `/devices`, and it is in no build output anywhere.
//
// The second guarantee is that this component does not exist in production at all. `EventDetail`
// renders it behind `import.meta.env.DEV`, which is a compile-time literal, so the production build
// drops the element, the import, this module and `deviceKey.ts` with it. That is verified against the
// real artefact — `npm run build && grep -c "eams_dk_\|DeviceKey\|deviceKey" dist/assets/*.js` must
// not gain a match — rather than trusted to the bundler.
//
// Three rules this file keeps and the next editor should keep:
//
//   1. **Only the key id is ever rendered.** `deviceKeyStatus()` returns no secret at all, so there is
//      nothing here that could display one even by accident.
//   2. **No request is built here.** The panel stores a token; `api.tap` is the only thing that sends
//      one, and it composes the Authorization header itself. `api.ts` stays the single seam.
//   3. **The copy says what the key can do.** A box that silently grants a browser the power to write
//      attendance is worse than no box.

import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Link } from "react-router-dom";
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Link as MuiLink,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { clearDeviceKey, deviceKeyStatus, setDeviceKey } from "../deviceKey";
import type { DeviceKeyStatus } from "../deviceKey";

// What the panel says about itself. Long-form because the reader is being asked to paste a write
// credential into a web page, and the two things they need to know — that this is dev-only, and that
// taps will be recorded as that device — are exactly the two a terse label would leave out.
const WHAT_THIS_IS =
  "Development build only. A key pasted here lets this browser record real attendance as that " +
  "device, for as long as the tab is open — it is held in sessionStorage and is never part of any " +
  "build.";

// The advice that costs nothing now and a lot later. A revoked kiosk key is a door that stops
// working; a revoked simulator key is nobody's afternoon.
const USE_A_SIMULATOR_DEVICE =
  "Register a device dedicated to simulation rather than reusing a real reader's key, so it can be " +
  "revoked on its own without taking a door offline.";

/** The one place a token is ever shown to a person, and it shows the public half. */
const FIELD_LABEL = "Device key token";

const FIELD_HELP = "eams_dk_… — 85 characters, lower-case, pasted whole. Case is significant.";

/**
 * Stable ids, so the field can point at both the standing help and the refusal.
 *
 * MUI wires `aria-describedby` to the helper text on its own, but only to *that*; the refusal is a
 * separate `role="alert"` outside the control, and without an explicit list a user who tabs back to
 * the box after the announcement has gone hears `aria-invalid` and no reason. Both ids, in reading
 * order — `aria-describedby` takes a list.
 */
const FIELD_ID = "dev-device-key-token";
const FIELD_HELP_ID = `${FIELD_ID}-help`;
const FIELD_REFUSAL_ID = `${FIELD_ID}-refusal`;

/**
 * Which control gets focus after a transition that unmounts the one holding it.
 *
 * Saving removes the form while focus is on Save; clearing removes the Clear button while focus is on
 * Clear. Either way focus falls to `document.body` and a keyboard user is returned to the top of the
 * page with no idea the thing they pressed worked. Each transition names the surviving control that
 * continues the task: after a save that is Clear, after a clear it is the box to paste into.
 */
type FocusTarget = "clear" | "field";

export default function DevDeviceKeyPanel() {
  // Read once on mount rather than on every render: `sessionStorage` is synchronous and this sits
  // inside a card that re-renders on every attendance refresh. Every mutation below sets it from the
  // value the mutation itself returned, so it cannot drift from storage.
  const [status, setStatus] = useState<DeviceKeyStatus>(() => deviceKeyStatus());
  const [draft, setDraft] = useState("");
  /** The last refusal, held rather than announced-and-forgotten: it names which character was wrong. */
  const [failure, setFailure] = useState<string | undefined>(undefined);
  const [focusTarget, setFocusTarget] = useState<FocusTarget | undefined>(undefined);

  const fieldRef = useRef<HTMLInputElement>(null);
  const clearRef = useRef<HTMLButtonElement>(null);

  // Runs after the commit that mounted the replacement, which is the whole point: the control being
  // focused does not exist at the moment the transition is requested. Cleared immediately so a later
  // re-render — the card around this panel re-renders on every attendance refresh — cannot steal
  // focus back from wherever the user has since moved it.
  useEffect(() => {
    if (focusTarget === undefined) return;
    (focusTarget === "clear" ? clearRef.current : fieldRef.current)?.focus();
    setFocusTarget(undefined);
  }, [focusTarget]);

  const save = (e: FormEvent) => {
    e.preventDefault();
    const result = setDeviceKey(draft);
    if (!result.ok) {
      // The form survives, so focus is still on Save and belongs there — the refusal names what to
      // change and the box is one Shift+Tab away.
      setFailure(result.reason);
      return;
    }
    setFailure(undefined);
    // Dropped as soon as it is stored. Leaving a valid token in a React state variable — and in the
    // box — for the rest of the session is a second copy with no reason to exist.
    setDraft("");
    setStatus(deviceKeyStatus());
    setFocusTarget("clear");
  };

  const clear = () => {
    const result = clearDeviceKey();
    // Not a silent failure: a storage that refuses `removeItem` would otherwise leave the panel
    // claiming the key is gone while `api.tap` goes on using it.
    setFailure(result.ok ? undefined : result.reason);
    setStatus(deviceKeyStatus());
    // Only on success. A refused clear leaves the key set and this button mounted, so focus has not
    // moved and asking for it again would be a no-op at best.
    if (result.ok) setFocusTarget("field");
  };

  /** The refusal is rendered iff there is one to render — and the field points at it when there is. */
  const refusal = failure ?? (status.kind === "unreadable" ? status.reason : undefined);

  return (
    <Box sx={{ mt: 2, pt: 2, borderTop: "1px solid rgba(0,0,0,0.12)" }}>
      <Typography variant="subtitle2" fontWeight={700}>
        Device key (development only)
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ maxWidth: "68ch", mt: 0.5 }}>
        {WHAT_THIS_IS}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ maxWidth: "68ch", mt: 0.5 }}>
        {USE_A_SIMULATOR_DEVICE}{" "}
        <MuiLink component={Link} to="/devices" underline="hover">
          Register one and copy its key on the Devices page
        </MuiLink>{" "}
        — the token is shown once and never again.
      </Typography>

      {/* Announced politely: the user caused the change, so it is a confirmation rather than an
          interruption. Mounted in both states so the region exists in the DOM before anything is put
          into it — a live region inserted and populated in the same commit is announced unreliably. */}
      <Box role="status" sx={{ mt: 1.5 }}>
        {status.kind === "set" && (
          <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap">
            <Typography variant="body2">
              Taps are being recorded as <strong>{status.label}</strong>
            </Typography>
            <Button size="small" variant="outlined" onClick={clear} ref={clearRef}>
              Clear key
            </Button>
          </Stack>
        )}
        {/* Defence in depth, not a live branch. `EventDetail` renders this panel behind
            `import.meta.env.DEV`, so a production build contains neither the element nor this
            module — there is no path on which `deviceKeyStatus()` can answer `unsupported` here.
            Kept because the invariant it restates (nothing in production holds a capture credential)
            is worth asserting at both ends rather than trusting one caller, and because the day
            someone renders this panel from somewhere else, this is what they should see. */}
        {status.kind === "unsupported" && (
          <Typography variant="body2">
            This is not a development build, so no device key can be held here.
          </Typography>
        )}
      </Box>

      {status.kind !== "set" && status.kind !== "unsupported" && (
        // A form rather than a field beside a button, so Enter submits — the key is pasted, and the
        // hand that pasted it is already on the keyboard.
        <Box component="form" onSubmit={save} noValidate sx={{ mt: 1.5 }}>
          <Stack direction="row" spacing={2} alignItems="flex-start" flexWrap="wrap">
            <TextField
              id={FIELD_ID}
              inputRef={fieldRef}
              // `password` so a shared screen or a recording does not carry the secret. The reveal is
              // the browser's own, which is the control users already know.
              type="password"
              size="small"
              label={FIELD_LABEL}
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              // A credential, not a saved form value: nothing should offer to remember it, and a
              // spell-checker sending it anywhere is not a theoretical concern.
              //
              // `new-password` rather than `off`: Chrome has ignored `autoComplete="off"` on password
              // inputs for years and offers to save them anyway, which is precisely the outcome the
              // paragraph above is trying to prevent. `new-password` is the value browsers actually
              // honour as "do not fill this from the store".
              autoComplete="new-password"
              spellCheck={false}
              // `failure`, not `refusal`. `aria-invalid` is a claim about what is *in the box*, and an
              // `unreadable` status is a claim about what is in storage — the box is untouched and
              // empty. It still gets described by that message below, because it explains what will
              // happen to a paste; it does not get marked invalid for it.
              error={failure !== undefined}
              helperText={FIELD_HELP}
              slotProps={{
                formHelperText: { id: FIELD_HELP_ID },
                htmlInput: {
                  // Both, in reading order. MUI would wire the helper text alone; the refusal lives
                  // in a `role="alert"` outside the control, so a user who tabs back after the
                  // announcement has passed would otherwise meet `aria-invalid` with no reason.
                  "aria-describedby":
                    refusal === undefined
                      ? FIELD_HELP_ID
                      : `${FIELD_HELP_ID} ${FIELD_REFUSAL_ID}`,
                },
              }}
              sx={{ minWidth: 320 }}
            />
            <Button type="submit" variant="outlined" disabled={draft.trim() === ""} sx={{ mt: 0.5 }}>
              Save key
            </Button>
          </Stack>
        </Box>
      )}

      {/* `role="alert"` rather than the field's `helperText`: a validation refusal that only exists as
          helper text is not announced when it appears, and this one names which of the four shape
          rules the paste broke — the whole reason to validate here instead of learning it from a 401
          two minutes later. */}
      {refusal !== undefined && (
        <Alert severity="warning" role="alert" sx={{ mt: 1.5 }}>
          <AlertTitle>That key was not stored</AlertTitle>
          <Typography variant="body2" id={FIELD_REFUSAL_ID}>
            {refusal}
          </Typography>
        </Alert>
      )}
    </Box>
  );
}
