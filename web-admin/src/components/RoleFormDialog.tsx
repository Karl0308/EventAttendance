// Create or edit a custom role (name + description). One dialog for both, because `POST /roles` and
// `PUT /roles/{id}` take the same body and are checked by the same server code. The write is owned by
// `Roles.tsx`.

import { useEffect, useState } from "react";
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
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import {
  EMPTY_ROLE_DRAFT,
  ROLE_NAME_TAKEN,
  draftFromRole,
  isRoleNameConflict,
  validateRole,
} from "../roleDraft";
import type { RoleDraft, RoleFieldErrors } from "../roleDraft";
import type { Role, RoleWriteRequest } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const NAME_ID = "role-form-name";

interface Props {
  /** The role being edited, or undefined to create a new one. */
  role: Role | undefined;
  /** Every role, for the duplicate-name check (the edited role excludes itself). */
  roles: readonly Role[];
  onClose: () => void;
  onSubmit: (request: RoleWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function RoleFormDialog({ role, roles, onClose, onSubmit, running, failure }: Props) {
  const editing = role !== undefined;
  const [draft, setDraft] = useState<RoleDraft>(role ? draftFromRole(role) : EMPTY_ROLE_DRAFT);
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const others = editing ? roles.filter((r) => r.id !== role.id) : roles;
  const checked = validateRole(draft, others);
  const errors: RoleFieldErrors = checked.ok ? {} : checked.errors;

  const serverNameRefusal = failure !== undefined && isRoleNameConflict(failure.error);

  const nameError = serverNameRefusal ? ROLE_NAME_TAKEN : submitAttempted ? errors.name : undefined;

  const refusedError = serverNameRefusal ? failure?.error : undefined;
  useEffect(() => {
    if (refusedError !== undefined) document.getElementById(NAME_ID)?.focus();
  }, [refusedError]);

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSubmitAttempted(true);
    const now = validateRole(draft, others);
    if (now.ok) onSubmit(now.request);
    else if (now.errors.name) document.getElementById(NAME_ID)?.focus();
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <form onSubmit={submit} noValidate>
        <DialogTitle>{editing ? `Edit ${role.name}` : "Create a role"}</DialogTitle>
        <DialogContent>
          {failure !== undefined && !serverNameRefusal && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={editing ? "The change was not saved" : "The role was not created"}
              mayHaveApplied={editing ? "The change may not have been saved" : "The role may have been created"}
              resendWithheld="Disabled because this build cannot tell whether it was applied. Close and check the list before trying again."
            />
          )}
          <Stack spacing={2} sx={{ mt: 1 }}>
            <TextField
              id={NAME_ID}
              label="Role name"
              value={draft.name}
              onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))}
              error={nameError !== undefined}
              helperText={nameError ?? "A short name, unique among roles — “Front Desk”, “Auditor”."}
              required
              autoFocus
              autoComplete="off"
              fullWidth
            />
            <TextField
              label="Description"
              value={draft.description}
              onChange={(e) => setDraft((d) => ({ ...d, description: e.target.value }))}
              error={submitAttempted && errors.description !== undefined}
              helperText={(submitAttempted ? errors.description : undefined) ?? "Optional."}
              multiline
              minRows={2}
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
            {running ? "Saving…" : editing ? "Save changes" : "Create role"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
