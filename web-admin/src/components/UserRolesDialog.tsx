// The role-assignment form: check the roles a user should hold and save the whole set. The write is
// owned by `Users.tsx`. `PUT /users/{id}/roles` is a replacement, so what is checked here is exactly
// what the user ends up with — an empty set is allowed and leaves them with no access.

import { useState } from "react";
import {
  Alert,
  Button,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  FormGroup,
  Stack,
  Typography,
} from "@mui/material";
import { describeApiError } from "../api";
import type { AdminUser, Role } from "../types";

const TITLE_ID = "user-roles-dialog-title";

interface Props {
  user: AdminUser;
  roles: readonly Role[];
  onClose: () => void;
  onSubmit: (roleIds: string[]) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function UserRolesDialog({ user, roles, onClose, onSubmit, running, failure }: Props) {
  const [selected, setSelected] = useState<ReadonlySet<string>>(
    new Set(user.roles.map((r) => r.id)),
  );

  const toggle = (roleId: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(roleId)) next.delete(roleId);
      else next.add(roleId);
      return next;
    });

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={TITLE_ID}>
      <DialogTitle id={TITLE_ID}>Roles for {user.fullName}</DialogTitle>
      <DialogContent>
        {failure !== undefined && (
          <Alert severity="error" role="alert" sx={{ mb: 2 }}>
            {describeApiError(failure.error)}
          </Alert>
        )}

        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
          A user’s access is the union of what their roles grant. Removing every role leaves them able to
          sign in but do nothing. Changes take effect the next time they sign in or their token refreshes.
        </Typography>

        <FormGroup>
          {roles.map((role) => (
            <FormControlLabel
              key={role.id}
              control={
                <Checkbox
                  checked={selected.has(role.id)}
                  onChange={() => toggle(role.id)}
                />
              }
              label={
                <Stack>
                  <Typography variant="body2">{role.name}</Typography>
                  {role.description && (
                    <Typography variant="caption" color="text.secondary">
                      {role.description}
                    </Typography>
                  )}
                </Stack>
              }
            />
          ))}
        </FormGroup>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={() => onSubmit([...selected])}
          disabled={running}
          startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {running ? "Saving…" : "Save roles"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
