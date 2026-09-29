// Configure which permissions a custom role grants — a matrix grouped by module. The write is owned by
// `Roles.tsx`. `PUT /roles/{id}/permissions` is a replacement, so what is checked here is exactly what
// the role ends up granting.

import { useState } from "react";
import {
  Alert,
  Box,
  Button,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControlLabel,
  FormGroup,
  Stack,
  Typography,
} from "@mui/material";
import { describeApiError } from "../api";
import { actionOf, groupPermissions } from "../roleDraft";
import type { Role } from "../types";

const TITLE_ID = "role-permissions-dialog-title";

interface Props {
  role: Role;
  /** Every grantable permission code — `GET /permissions`. */
  allPermissions: readonly string[];
  onClose: () => void;
  onSubmit: (permissionCodes: string[]) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function RolePermissionsDialog({
  role,
  allPermissions,
  onClose,
  onSubmit,
  running,
  failure,
}: Props) {
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set(role.permissionCodes));

  const toggle = (code: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(code)) next.delete(code);
      else next.add(code);
      return next;
    });

  const groups = groupPermissions(allPermissions);

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={TITLE_ID}>
      <DialogTitle id={TITLE_ID}>Permissions for {role.name}</DialogTitle>
      <DialogContent>
        {failure !== undefined && (
          <Alert severity="error" role="alert" sx={{ mb: 2 }}>
            {describeApiError(failure.error)}
          </Alert>
        )}

        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          A role’s access is the set of permissions checked here. Users holding this role get the change on
          their next sign-in or token refresh.
        </Typography>

        <Stack spacing={1.5}>
          {groups.map((group) => (
            <Box key={group.module}>
              <Typography variant="subtitle2" sx={{ textTransform: "capitalize" }}>
                {group.module}
              </Typography>
              <FormGroup row>
                {group.codes.map((code) => (
                  <FormControlLabel
                    key={code}
                    control={
                      <Checkbox size="small" checked={selected.has(code)} onChange={() => toggle(code)} />
                    }
                    // The action half, with the full code as the accessible/hover label so it is never
                    // ambiguous which permission a checkbox is.
                    label={actionOf(code)}
                    title={code}
                    aria-label={code}
                  />
                ))}
              </FormGroup>
              <Divider sx={{ mt: 1 }} />
            </Box>
          ))}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={() => onSubmit([...selected])}
          disabled={running}
          startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {running ? "Saving…" : "Save permissions"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
