// The create-event-classification form. The write itself is NOT here — `EventClassifications.tsx` owns
// it, for the reason `NewTermDialog` records: a dialog that owns its own write has to block its own
// dismissal while the request runs, which traps a keyboard user.

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  AlertTitle,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Typography,
} from "@mui/material";
import { describeApiError } from "../api";
import { isResendUnsafe } from "../apiGuidance";
import {
  EMPTY_EVENT_CLASSIFICATION_DRAFT,
  NAME_TAKEN,
  NO_EVENT_CLASSIFICATION_ERRORS,
  NO_SCHOOL_TO_FILE_UNDER,
  VALIDATED_EVENT_CLASSIFICATION_FIELDS,
  eventClassificationFieldIdsFor,
  isNameConflict,
  isNoSchoolResolved,
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

const FIELD_ID = eventClassificationFieldIdsFor("new-event-classification");
const DIALOG_TITLE_ID = "new-event-classification-dialog-title";

const HEADING_NOT_CREATED = "The event classification was not created";
const HEADING_MAYBE_CREATED = "The event classification may have been created";

const RESEND_WITHHELD =
  "Create is disabled because pressing it again could create the same classification twice. Close this " +
  "dialog and check the list, which is re-read after every failed attempt, before trying again.";

interface Props {
  classifications: readonly EventClassification[];
  onClose: () => void;
  onSubmit: (request: EventClassificationWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function NewEventClassificationDialog({
  classifications,
  onClose,
  onSubmit,
  running,
  failure,
}: Props) {
  const [draft, setDraft] = useState<EventClassificationDraft>(EMPTY_EVENT_CLASSIFICATION_DRAFT);
  const [touched, setTouched] = useState<ReadonlySet<ValidatedEventClassificationField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateEventClassification(draft, classifications);
  const errors: EventClassificationFieldErrors = checked.ok
    ? NO_EVENT_CLASSIFICATION_ERRORS
    : checked.errors;

  const serverNameRefusal = failure !== undefined && isNameConflict(failure.error);
  const noSchool = failure !== undefined && isNoSchoolResolved(failure.error);

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

    const now = validateEventClassification(draft, classifications);
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
        <DialogTitle id={DIALOG_TITLE_ID}>Create an event classification</DialogTitle>

        <DialogContent>
          {noSchool && (
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>
              <AlertTitle>{HEADING_NOT_CREATED}</AlertTitle>
              <Typography variant="body2">{describeApiError(failure?.error)}</Typography>
              <Typography variant="body2" sx={{ mt: 1 }}>
                {NO_SCHOOL_TO_FILE_UNDER}
              </Typography>
            </Alert>
          )}

          {failure !== undefined && !serverNameRefusal && !noSchool && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_CREATED}
              mayHaveApplied={HEADING_MAYBE_CREATED}
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
            {running ? "Creating…" : "Create classification"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
