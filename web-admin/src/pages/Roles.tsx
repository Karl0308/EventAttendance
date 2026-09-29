// The roles screen — §11 Role Management (UserWithRBAC.docx). The page owns every write, as `Terms.tsx`
// does. The four built-in roles are read-only: their edit/permissions/delete actions are disabled, and
// the server refuses them anyway. A custom role can be created, renamed, re-permissioned, and deleted
// once no user holds it.

import { useLayoutEffect, useRef, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  Snackbar,
  Stack,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import TuneIcon from "@mui/icons-material/Tune";
import DeleteIcon from "@mui/icons-material/Delete";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import RoleFormDialog from "../components/RoleFormDialog";
import RolePermissionsDialog from "../components/RolePermissionsDialog";
import type { Role, RoleWriteRequest } from "../types";

const loadRoles = () => api.listRoles();
const loadPermissions = () => api.listPermissions();

const NO_ROLES = "This school has no roles. That should not happen — the four built-in roles are seeded.";

interface Notice {
  severity: "success" | "error";
  text: string;
}
const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function Roles() {
  const roles = useApiResource(loadRoles, []);
  const permissions = useApiResource(loadPermissions, []);

  const create = useApiMutation((request: RoleWriteRequest) => api.createRole(request));
  const edit = useApiMutation((id: string, request: RoleWriteRequest) => api.updateRole(id, request));
  const setPerms = useApiMutation((id: string, codes: string[]) => api.setRolePermissions(id, codes));
  const remove = useApiMutation((id: string) => api.deleteRole(id));

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Role | undefined>(undefined);
  const [permingRole, setPermingRole] = useState<Role | undefined>(undefined);
  const [deleting, setDeleting] = useState<Role | undefined>(undefined);

  const createOpen = useRef(creating);
  const editOpen = useRef(editing !== undefined);
  const permOpen = useRef(permingRole !== undefined);
  const deleteOpen = useRef(deleting !== undefined);
  useLayoutEffect(() => {
    createOpen.current = creating;
    editOpen.current = editing !== undefined;
    permOpen.current = permingRole !== undefined;
    deleteOpen.current = deleting !== undefined;
  });

  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);
  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const submitCreate = (request: RoleWriteRequest) => {
    void create.run(request).then((s) => {
      if (s.outcome === "ignored") return;
      roles.reload();
      if (s.outcome === "succeeded") {
        setCreating(false);
        announce({ severity: "success", text: `Role “${s.data.name}” was created. Set its permissions next.` });
      } else if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const submitEdit = (id: string, request: RoleWriteRequest) => {
    void edit.run(id, request).then((s) => {
      if (s.outcome === "ignored") return;
      roles.reload();
      if (s.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to “${s.data.name}”.` });
      } else if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const submitPerms = (id: string, codes: string[]) => {
    void setPerms.run(id, codes).then((s) => {
      if (s.outcome === "ignored") return;
      roles.reload();
      if (s.outcome === "succeeded") {
        setPermingRole(undefined);
        announce({ severity: "success", text: `Updated permissions for “${s.data.name}”.` });
      } else if (!permOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const confirmDelete = (role: Role) => {
    void remove.run(role.id).then((s) => {
      if (s.outcome === "ignored") return;
      roles.reload();
      if (s.outcome === "succeeded") {
        setDeleting(undefined);
        announce({ severity: "success", text: `Role “${role.name}” was deleted.` });
      } else if (!deleteOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const cols: GridColDef<Role>[] = [
    { field: "name", headerName: "Role", flex: 1, minWidth: 160 },
    {
      field: "description",
      headerName: "Description",
      flex: 2,
      minWidth: 240,
      valueGetter: (_v, row) => row.description ?? "—",
    },
    {
      field: "permissionCodes",
      headerName: "Permissions",
      width: 130,
      valueGetter: (_v, row) => row.permissionCodes.length,
      renderCell: (p) => `${p.row.permissionCodes.length} granted`,
    },
    { field: "userCount", headerName: "Users", width: 90 },
    {
      field: "isSystem",
      headerName: "Kind",
      width: 120,
      renderCell: (p) =>
        p.row.isSystem ? (
          <Chip size="small" label="Built-in" variant="outlined" />
        ) : (
          <Chip size="small" label="Custom" color="primary" variant="outlined" />
        ),
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 160,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <RowActions
          role={p.row}
          onEdit={(r) => {
            edit.reset();
            setEditing(r);
          }}
          onPerms={(r) => {
            setPerms.reset();
            setPermingRole(r);
          }}
          onDelete={(r) => {
            remove.reset();
            setDeleting(r);
          }}
        />
      ),
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Roles
        </Typography>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={() => {
            create.reset();
            setCreating(true);
          }}
        >
          Create role
        </Button>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        A role is a named set of permissions. Users are given roles; what a user can do is the union of
        their roles’ permissions. The four built-in roles are read-only — create a custom role for anything
        else.
      </Typography>

      {roles.status === "loading" && <LoadingState label="Loading roles…" />}
      {roles.status === "error" && <ErrorState subject="roles" error={roles.error} onRetry={roles.reload} />}

      {roles.status === "ready" && (
        <>
          <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center", mb: 1 }}>
            {roles.refreshing && (
              <Typography variant="body2" color="text.secondary">
                Refreshing…
              </Typography>
            )}
          </Box>
          {roles.data.length === 0 ? (
            <EmptyState message={NO_ROLES} />
          ) : (
            <div style={{ height: 520, width: "100%" }}>
              <DataGrid
                rows={roles.data}
                columns={cols}
                getRowId={(r) => r.id}
                disableRowSelectionOnClick
                pageSizeOptions={[10, 25]}
                initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
              />
            </div>
          )}
        </>
      )}

      {creating && (
        <RoleFormDialog
          role={undefined}
          roles={roles.data ?? []}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <RoleFormDialog
          role={editing}
          roles={roles.data ?? []}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {permingRole !== undefined && (
        <RolePermissionsDialog
          role={permingRole}
          allPermissions={permissions.data ?? []}
          onClose={() => setPermingRole(undefined)}
          onSubmit={(codes) => submitPerms(permingRole.id, codes)}
          running={setPerms.status === "running"}
          failure={setPerms.status === "failed" ? { error: setPerms.error } : undefined}
        />
      )}

      {deleting !== undefined && (
        <Dialog open onClose={() => setDeleting(undefined)} fullWidth maxWidth="sm">
          <DialogTitle>Delete “{deleting.name}”?</DialogTitle>
          <DialogContent>
            <DialogContentText>
              This removes the role. It is refused if any user still holds it — reassign them first.
            </DialogContentText>
            {remove.status === "failed" && (
              <Alert severity="error" role="alert" sx={{ mt: 2 }}>
                {describeApiError(remove.error)}
              </Alert>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setDeleting(undefined)}>Cancel</Button>
            <Button
              color="error"
              variant="contained"
              onClick={() => confirmDelete(deleting)}
              disabled={remove.status === "running"}
            >
              {remove.status === "running" ? "Deleting…" : "Delete"}
            </Button>
          </DialogActions>
        </Dialog>
      )}

      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {notice ? (
          <Alert
            severity={notice.severity}
            role={notice.severity === "error" ? "alert" : "status"}
            onClose={() => setAnnouncing(false)}
          >
            {notice.text}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Box>
  );
}

function RowActions({
  role,
  onEdit,
  onPerms,
  onDelete,
}: {
  role: Role;
  onEdit: (r: Role) => void;
  onPerms: (r: Role) => void;
  onDelete: (r: Role) => void;
}) {
  // Built-in roles are read-only. The actions are shown disabled rather than hidden, so the reason (a
  // tooltip) is discoverable rather than the buttons silently missing on some rows.
  const builtIn = role.isSystem;
  const why = builtIn ? " — built-in roles cannot be changed" : "";
  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton
        sx={{ p: 1 }}
        disabled={builtIn}
        onClick={() => onEdit(role)}
        aria-label={`Edit ${role.name}${why}`}
        title={`Edit ${role.name}${why}`}
      >
        <EditIcon fontSize="small" />
      </IconButton>
      <IconButton
        sx={{ p: 1 }}
        disabled={builtIn}
        onClick={() => onPerms(role)}
        aria-label={`Configure permissions for ${role.name}${why}`}
        title={`Configure permissions for ${role.name}${why}`}
      >
        <TuneIcon fontSize="small" />
      </IconButton>
      <IconButton
        sx={{ p: 1 }}
        color="error"
        disabled={builtIn}
        onClick={() => onDelete(role)}
        aria-label={`Delete ${role.name}${why}`}
        title={`Delete ${role.name}${why}`}
      >
        <DeleteIcon fontSize="small" />
      </IconButton>
    </Stack>
  );
}
