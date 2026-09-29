// The edit-user form — full name and phone only (the e-mail is the login identity and is not editable
// here). The write is owned by `Users.tsx`.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import { draftFromUser, validateUserEdit } from "../userDraft";
import type { UserEditDraft } from "../userDraft";
import type { AdminUser, UserUpdateRequest } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const TITLE_ID = "edit-user-dialog-title";

interface Props {
  user: AdminUser;
  onClose: () => void;
  onSubmit: (request: UserUpdateRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function EditUserDialog({ user, onClose, onSubmit, running, failure }: Props) {
  const [draft, setDraft] = useState<UserEditDraft>(draftFromUser(user));
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateUserEdit(draft);
  const errors = checked.ok ? {} : checked.errors;
  const show = (field: "fullName" | "phone") => (submitAttempted ? errors[field] : undefined);

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSubmitAttempted(true);
    const now = validateUserEdit(draft);
    if (now.ok) onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={TITLE_ID}>Edit {user.fullName}</DialogTitle>
        <DialogContent>
          {failure !== undefined && (
            <WriteFailureAlert
              error={failure.error}
              notApplied="The change was not saved"
              mayHaveApplied="The change may not have been saved"
              resendWithheld="Save is disabled because this build cannot tell whether the change was applied. Close this dialog and check the list before trying again."
            />
          )}
          <Stack spacing={2} sx={{ mt: 1 }}>
            <Typography variant="body2" color="text.secondary">
              {user.email} — the e-mail is the login identifier and is not editable here.
            </Typography>
            <TextField
              label="Full name"
              value={draft.fullName}
              onChange={(e) => setDraft((d) => ({ ...d, fullName: e.target.value }))}
              error={show("fullName") !== undefined}
              helperText={show("fullName")}
              required
              autoFocus
              autoComplete="off"
              fullWidth
            />
            <TextField
              label="Phone"
              value={draft.phone}
              onChange={(e) => setDraft((d) => ({ ...d, phone: e.target.value }))}
              error={show("phone") !== undefined}
              helperText={show("phone") ?? "Optional."}
              autoComplete="off"
              fullWidth
            />
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
            {running ? "Saving…" : "Save changes"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
