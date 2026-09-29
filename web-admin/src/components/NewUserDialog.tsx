// The create-user form. The write is owned by `Users.tsx`, as `NewTermDialog`'s is.

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import {
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
  EMPTY_USER_CREATE_DRAFT,
  EMAIL_TAKEN,
  VALIDATED_CREATE_FIELDS,
  createFieldIdsFor,
  isEmailInUse,
  validateUserCreate,
} from "../userDraft";
import type {
  CreateFieldErrors,
  UserCreateDraft,
  ValidatedCreateField,
} from "../userDraft";
import type { AdminUser, Role, UserCreateRequest } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = createFieldIdsFor("new-user");
const TITLE_ID = "new-user-dialog-title";

interface Props {
  roles: readonly Role[];
  users: readonly AdminUser[];
  onClose: () => void;
  onSubmit: (request: UserCreateRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function NewUserDialog({ roles, users, onClose, onSubmit, running, failure }: Props) {
  const [draft, setDraft] = useState<UserCreateDraft>(EMPTY_USER_CREATE_DRAFT);
  const [touched, setTouched] = useState<ReadonlySet<ValidatedCreateField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateUserCreate(draft, users);
  const errors: CreateFieldErrors = checked.ok ? {} : checked.errors;

  const serverEmailRefusal = failure !== undefined && isEmailInUse(failure.error);

  const errorFor = (field: ValidatedCreateField): string | undefined => {
    if (field === "email" && serverEmailRefusal) return EMAIL_TAKEN;
    return submitAttempted || touched.has(field) ? errors[field] : undefined;
  };

  const markTouched = (field: ValidatedCreateField) =>
    setTouched((current) => new Set(current).add(field));

  const refusedError = serverEmailRefusal ? failure?.error : undefined;
  useEffect(() => {
    if (refusedError !== undefined) document.getElementById(FIELD_ID.email)?.focus();
  }, [refusedError]);

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const set = (patch: Partial<UserCreateDraft>) => setDraft((d) => ({ ...d, ...patch }));

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSubmitAttempted(true);
    const now = validateUserCreate(draft, users);
    if (!now.ok) {
      const first = VALIDATED_CREATE_FIELDS.find((f) => now.errors[f] !== undefined);
      if (first !== undefined) document.getElementById(FIELD_ID[first])?.focus();
      return;
    }
    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={TITLE_ID}>Create a user</DialogTitle>
        <DialogContent>
          {failure !== undefined && !serverEmailRefusal && (
            <WriteFailureAlert
              error={failure.error}
              notApplied="The user was not created"
              mayHaveApplied="The user may have been created"
              resendWithheld="Create is disabled because pressing it again could create the same user twice. Close this dialog and check the list before trying again."
            />
          )}

          <Stack spacing={2} sx={{ mt: 1 }}>
            <TextField
              id={FIELD_ID.email}
              label="E-mail"
              type="email"
              value={draft.email}
              onChange={(e) => set({ email: e.target.value })}
              onBlur={() => markTouched("email")}
              error={errorFor("email") !== undefined}
              helperText={errorFor("email") ?? "The login identifier. Cannot be changed after creation."}
              required
              autoFocus
              autoComplete="off"
              fullWidth
            />
            <TextField
              id={FIELD_ID.fullName}
              label="Full name"
              value={draft.fullName}
              onChange={(e) => set({ fullName: e.target.value })}
              onBlur={() => markTouched("fullName")}
              error={errorFor("fullName") !== undefined}
              helperText={errorFor("fullName")}
              required
              autoComplete="off"
              fullWidth
            />
            <TextField
              id={FIELD_ID.phone}
              label="Phone"
              value={draft.phone}
              onChange={(e) => set({ phone: e.target.value })}
              onBlur={() => markTouched("phone")}
              error={errorFor("phone") !== undefined}
              helperText={errorFor("phone") ?? "Optional."}
              autoComplete="off"
              fullWidth
            />
            <TextField
              id={FIELD_ID.roleName}
              label="Initial role"
              select
              value={draft.roleName}
              onChange={(e) => set({ roleName: e.target.value })}
              onBlur={() => markTouched("roleName")}
              error={errorFor("roleName") !== undefined}
              helperText={errorFor("roleName") ?? "You can add or change roles after creating the user."}
              required
              fullWidth
            >
              {roles.map((r) => (
                <MenuItem key={r.id} value={r.name}>
                  {r.name}
                </MenuItem>
              ))}
            </TextField>
            <TextField
              id={FIELD_ID.password}
              label="Initial password"
              type="password"
              value={draft.password}
              onChange={(e) => set({ password: e.target.value })}
              onBlur={() => markTouched("password")}
              error={errorFor("password") !== undefined}
              helperText={errorFor("password") ?? "At least 12 characters."}
              required
              autoComplete="new-password"
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
            {running ? "Creating…" : "Create user"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
