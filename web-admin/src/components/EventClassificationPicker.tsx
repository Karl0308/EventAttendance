// The event form's classification select: "None", the school's ACTIVE classifications, and the event's
// own classification if it has since been deactivated.
//
// It reads its options itself (`useApiResource`) rather than being handed them, because the form it
// sits in is shared by the create and edit dialogs and neither owns a server read — doing the read
// here keeps both of them, and `EventFormFields`, free of it.
//
// Why a deactivated classification is still offered: the server lets an event keep the classification
// it recorded after that classification is withdrawn (re-sending it is accepted; only *changing to* a
// non-active one is refused). A select that dropped it would show the event as "None" and the next
// save would clear it — the silent loss this picker exists to stop. So it stays as an option, labelled
// as deactivated so nobody mistakes it for a current choice, and it is the only deactivated one shown.

import { useEffect, useState } from "react";
import { Button, CircularProgress, MenuItem, Stack, TextField, Typography } from "@mui/material";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";

/** The select's value for "no classification" — a select cannot hold `null`. */
const NONE = "";

const loadActiveClassifications = () => api.listEventClassifications();

/** The classification an edited event already carries, as the event page read it. */
export interface StoredClassification {
  id: string;
  name: string;
}

interface EventClassificationPickerProps {
  /** The select's DOM id, from `fieldIdsFor` — MUI builds `<label for>` out of it. */
  id: string;
  /** The chosen classification's id, or `""` for none. */
  value: string;
  onChange: (id: string) => void;
  /** Set for an edit of a classified event; see the file note for why it is kept as an option. */
  stored?: StoredClassification;
  disabled?: boolean;
}

const HINT =
  "Optional. Reusable audience definitions are filtered by classification, so an event needs one " +
  "before a definition can be attached.";

export function EventClassificationPicker({
  id,
  value,
  onChange,
  stored,
  disabled = false,
}: EventClassificationPickerProps) {
  const classifications = useApiResource(loadActiveClassifications, []);

  const active = classifications.status === "ready" ? classifications.data : [];
  const storedIsActive = stored !== undefined && active.some((c) => c.id === stored.id);
  // Offered whenever it is not among the active ones — also while the list is still loading or failed,
  // when "active" cannot be known yet: the select must hold the value it was given or MUI warns about
  // an out-of-range value and the box renders blank. "(deactivated)" is only claimed once it is known.
  const keepStored = stored !== undefined && !storedIsActive;
  const storedLabel =
    classifications.status === "ready" ? `${stored?.name ?? ""} (deactivated)` : (stored?.name ?? "");

  const loading = classifications.status === "loading";
  const failed = classifications.status === "error";

  // Retry is kept mounted (disabled) for the re-read it starts. `reload()` after a failure goes
  // error -> loading, and unmounting the button the user just pressed would drop focus to <body> inside
  // a focus-trapped dialog. Set on press, never cleared by an effect: it only matters while `loading`,
  // and the one `loading` that is not a retry is the first, before any press.
  const [retrying, setRetrying] = useState(false);
  const retry = () => {
    setRetrying(true);
    classifications.reload();
  };
  const showRetry = failed || (loading && retrying);

  // Focus was on the Retry button, which is disabled while the re-read runs. When it lands, put focus
  // back somewhere real: the select on success (the button is gone), the button again on a repeat
  // failure. Imperative focus is the one thing an effect is for here.
  const retryId = `${id}-retry`;
  useEffect(() => {
    if (!retrying) return;
    if (classifications.status === "ready") {
      setRetrying(false);
      document.getElementById(id)?.focus();
    } else if (classifications.status === "error") {
      document.getElementById(retryId)?.focus();
    }
  }, [retrying, classifications.status, id, retryId]);

  return (
    <Stack spacing={0.5}>
      <Stack direction="row" spacing={1} alignItems="flex-start">
        <TextField
          id={id}
          label="Event classification"
          select
          value={value}
          onChange={(e) => onChange(e.target.value)}
          error={failed}
          helperText={loading ? "Loading classifications…" : HINT}
          disabled={disabled || loading}
          fullWidth
        >
          <MenuItem value={NONE}>None</MenuItem>
          {active.map((c) => (
            <MenuItem key={c.id} value={c.id}>
              {c.name}
            </MenuItem>
          ))}
          {keepStored && <MenuItem value={stored.id}>{storedLabel}</MenuItem>}
        </TextField>
        {loading && <CircularProgress size={20} aria-hidden sx={{ mt: 2 }} />}
        {showRetry && (
          <Button
            id={retryId}
            onClick={retry}
            disabled={loading}
            aria-label="Retry loading classifications"
            sx={{ mt: 1 }}
          >
            Retry
          </Button>
        )}
      </Stack>
      {/* Its own element, mounted when the load fails, so assistive tech announces it: a live region
          that is already in the tree is not reliably spoken when its text changes, and the standing
          hint must not become an alert. */}
      {failed && (
        <Typography variant="caption" color="error" role="alert">
          {`Classifications could not be loaded. ${describeApiError(classifications.error)}`}
        </Typography>
      )}
    </Stack>
  );
}
