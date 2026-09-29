// The edit-event-classification form — the create dialog's sibling, starting from an existing row. The
// write is owned by `EventClassifications.tsx`, as the create dialog's is.

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import {
  NAME_TAKEN,
  NO_EVENT_CLASSIFICATION_ERRORS,
  VALIDATED_EVENT_CLASSIFICATION_FIELDS,
  draftFromEventClassification,
  eventClassificationFieldIdsFor,
  isNameConflict,
  validateEventClassification,
} from "../eventClassificationDraft";
import type {
  EventClassificationDraft,
  EventClassificationFieldErrors,
  ValidatedEventClassificationField,
} from "../eventClassificationDraft";
import type { EventClassification, EventClassificationWriteRequest } from "../types";
import { EventClassificationFormFields } from "./EventClassificationFormFields";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = eventClassificationFieldIdsFor("edit-event-classification");
const DIALOG_TITLE_ID = "edit-event-classification-dialog-title";

const HEADING_NOT_SAVED = "The change was not saved";
const HEADING_MAYBE_SAVED = "The change may not have been saved";

const RESEND_WITHHELD =
  "Save is disabled because this build cannot tell whether the change was applied. Close this dialog " +
  "and check the list, which is re-read after every failed attempt, before trying again.";

interface Props {
  classification: EventClassification;
  /** Every classification, minus the one being edited — a name is not a duplicate of itself. */
  classifications: readonly EventClassification[];
  onClose: () => void;
  onSubmit: (request: EventClassificationWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function EditEventClassificationDialog({
  classification,
  classifications,
  onClose,
  onSubmit,
  running,
  failure,
}: Props) {
  const [draft, setDraft] = useState<EventClassificationDraft>(
    draftFromEventClassification(classification),
  );
  const [touched, setTouched] = useState<ReadonlySet<ValidatedEventClassificationField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const others = classifications.filter((c) => c.id !== classification.id);

  const checked = validateEventClassification(draft, others);
  const errors: EventClassificationFieldErrors = checked.ok
    ? NO_EVENT_CLASSIFICATION_ERRORS
    : checked.errors;

  const serverNameRefusal = failure !== undefined && isNameConflict(failure.error);

  const errorFor = (field: ValidatedEventClassificationField): string | undefined => {
    if (field === "name" && serverNameRefusal) return NAME_TAKEN;
    return submitAttempted || touched.has(field) ? errors[field] : undefined;
  };

  const markTouched = (field: ValidatedEventClassificationField) =>
    setTouched((current) => new Set(current).add(field));

  const refusedError = serverNameRefusal ? failure?.error : undefined;
  useEffect(() => {
    if (refusedError !== undefined) document.getElementById(FIELD_ID.name)?.focus();
  }, [refusedError]);

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validateEventClassification(draft, others);
    if (!now.ok) {
      const firstInvalid = VALIDATED_EVENT_CLASSIFICATION_FIELDS.find(
        (field) => now.errors[field] !== undefined,
      );
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>Edit {classification.name}</DialogTitle>

        <DialogContent>
          {failure !== undefined && !serverNameRefusal && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_SAVED}
              mayHaveApplied={HEADING_MAYBE_SAVED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <EventClassificationFormFields
            draft={draft}
            onChange={(patch) => setDraft((d) => ({ ...d, ...patch }))}
            ids={FIELD_ID}
            errorFor={errorFor}
            onBlur={markTouched}
          />
        </DialogContent>

        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Saving…" : "Save changes"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
