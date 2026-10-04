// Create or edit a personnel record. One dialog for both, because POST and PUT take the same body. The
// write is owned by `Personnel.tsx`.

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
  Autocomplete,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Stack,
  TextField,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import {
  EMPTY_PERSONNEL_DRAFT,
  NUMBER_TAKEN,
  RFID_TAKEN,
  draftFromPersonnel,
  isDuplicateNumber,
  isDuplicateRfid,
  validatePersonnel,
} from "../personnelDraft";
import type { PersonnelDraft, PersonnelFieldErrors } from "../personnelDraft";
import type { Personnel, PersonnelStatusName, PersonnelWriteRequest } from "../types";
import { PERSONNEL_STATUSES } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const NUMBER_ID = "personnel-form-number";
const RFID_ID = "personnel-form-rfid";

interface Props {
  personnel: Personnel | undefined;
  onClose: () => void;
  onSubmit: (request: PersonnelWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
  /** Organizations already on file, offered as suggestions. The field still accepts any new value. */
  organizations: readonly string[];
}

export default function PersonnelFormDialog({ personnel, onClose, onSubmit, running, failure, organizations }: Props) {
  const editing = personnel !== undefined;
  const [draft, setDraft] = useState<PersonnelDraft>(
    personnel ? draftFromPersonnel(personnel) : EMPTY_PERSONNEL_DRAFT,
  );
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validatePersonnel(draft);
  const errors: PersonnelFieldErrors = checked.ok ? {} : checked.errors;

  const numberRefusal = failure !== undefined && isDuplicateNumber(failure.error);
  const rfidRefusal = failure !== undefined && isDuplicateRfid(failure.error);

  const set = (patch: Partial<PersonnelDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const show = (field: keyof PersonnelFieldErrors) => (submitAttempted ? errors[field] : undefined);

  const refused = numberRefusal ? "number" : rfidRefusal ? "rfid" : undefined;
  useEffect(() => {
    if (refused === "number") document.getElementById(NUMBER_ID)?.focus();
    else if (refused === "rfid") document.getElementById(RFID_ID)?.focus();
  }, [refused, failure]);

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSubmitAttempted(true);
    const now = validatePersonnel(draft);
    if (now.ok) onSubmit(now.request);
  };

  const numberError = numberRefusal ? NUMBER_TAKEN : show("personnelNumber");
  const rfidError = rfidRefusal ? RFID_TAKEN : undefined;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <form onSubmit={submit} noValidate>
        <DialogTitle>{editing ? `Edit ${personnel.fullName}` : "Add personnel"}</DialogTitle>
        <DialogContent>
          {failure !== undefined && !numberRefusal && !rfidRefusal && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={editing ? "The change was not saved" : "The record was not created"}
              mayHaveApplied={editing ? "The change may not have been saved" : "The record may have been created"}
              resendWithheld="Disabled because this build cannot tell whether it was applied. Close and check the list before trying again."
            />
          )}
          <Stack spacing={2} sx={{ mt: 1 }}>
            <TextField
              id={NUMBER_ID}
              label="Personnel ID"
              value={draft.personnelNumber}
              onChange={(e) => set({ personnelNumber: e.target.value })}
              error={numberError !== undefined}
              helperText={numberError}
              required
              autoFocus
              autoComplete="off"
              fullWidth
            />
            <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
              <TextField
                label="First name"
                value={draft.firstName}
                onChange={(e) => set({ firstName: e.target.value })}
                error={show("firstName") !== undefined}
                helperText={show("firstName")}
                required
                autoComplete="off"
                fullWidth
              />
              <TextField
                label="Middle name"
                value={draft.middleName}
                onChange={(e) => set({ middleName: e.target.value })}
                autoComplete="off"
                fullWidth
              />
              <TextField
                label="Last name"
                value={draft.lastName}
                onChange={(e) => set({ lastName: e.target.value })}
                error={show("lastName") !== undefined}
                helperText={show("lastName")}
                required
                autoComplete="off"
                fullWidth
              />
            </Stack>
            <TextField
              label="E-mail"
              type="email"
              value={draft.email}
              onChange={(e) => set({ email: e.target.value })}
              autoComplete="off"
              fullWidth
            />
            <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
              <TextField
                label="Classification"
                value={draft.classification}
                onChange={(e) => set({ classification: e.target.value })}
                helperText="e.g. ACAD, NAP"
                autoComplete="off"
                fullWidth
              />
              <TextField
                label="Position"
                value={draft.position}
                onChange={(e) => set({ position: e.target.value })}
                autoComplete="off"
                fullWidth
              />
            </Stack>
            <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
              <TextField
                label="Department"
                value={draft.department}
                onChange={(e) => set({ department: e.target.value })}
                autoComplete="off"
                fullWidth
              />
              {/* Free-solo: picking a suggestion and typing a new name are the same edit. The typed text
                  is the value, so `value` is the draft and `onInputChange` writes it back — it fires for
                  typing, for picking an option and for clearing. */}
              <Autocomplete
                freeSolo
                fullWidth
                options={organizations}
                value={draft.organization}
                onInputChange={(_event, text) => set({ organization: text })}
                renderInput={(params) => <TextField {...params} label="Organization" autoComplete="off" />}
              />
            </Stack>
            <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
              <TextField
                id={RFID_ID}
                label="RFID UID"
                value={draft.rfidUid}
                onChange={(e) => set({ rfidUid: e.target.value })}
                error={rfidError !== undefined}
                helperText={rfidError ?? "Optional. Separators are stripped and it is upper-cased."}
                autoComplete="off"
                fullWidth
              />
              <TextField
                label="Status"
                select
                value={draft.status}
                onChange={(e) => set({ status: e.target.value as PersonnelStatusName })}
                fullWidth
              >
                {PERSONNEL_STATUSES.map((s) => (
                  <MenuItem key={s} value={s}>
                    {s}
                  </MenuItem>
                ))}
              </TextField>
            </Stack>
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Saving…" : editing ? "Save changes" : "Add personnel"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
