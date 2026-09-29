// The users screen — §11 User Management (UserWithRBAC.docx). The page owns every write, as `Terms.tsx`
// does, so dismissing a dialog does not end the request it started.
//
// There is no delete: §11 deactivates rather than deletes a user, so historical rows (attendance they
// recorded, audit entries) are never orphaned. Deactivating is offered on every row; an administrator
// cannot deactivate or re-role their own account (the server refuses it, and the message says so).

import { useLayoutEffect, useRef, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Chip,
  IconButton,
  Snackbar,
  Stack,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import BadgeIcon from "@mui/icons-material/Badge";
import ToggleOnIcon from "@mui/icons-material/ToggleOn";
import ToggleOffIcon from "@mui/icons-material/ToggleOff";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import NewUserDialog from "../components/NewUserDialog";
import EditUserDialog from "../components/EditUserDialog";
import UserRolesDialog from "../components/UserRolesDialog";
import type { AdminUser, UserCreateRequest, UserUpdateRequest } from "../types";

const loadUsers = () => api.listUsers(true);
const loadRoles = () => api.listRoles();

const NO_USERS =
  "This school has no users yet. A user signs in to the admin app; their roles decide what they can do. " +
  "Create the first one here.";

const NO_VALUE = "—";

interface Notice {
  severity: "success" | "error";
  text: string;
}
const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function Users() {
  const users = useApiResource(loadUsers, []);
  const roles = useApiResource(loadRoles, []);

  const create = useApiMutation((request: UserCreateRequest) => api.createUser(request));
  const edit = useApiMutation((id: string, request: UserUpdateRequest) => api.updateUser(id, request));
  const setActive = useApiMutation((id: string, isActive: boolean) => api.setUserActive(id, isActive));
  const setRoles = useApiMutation((id: string, roleIds: string[]) => api.setUserRoles(id, roleIds));

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<AdminUser | undefined>(undefined);
  const [managing, setManaging] = useState<AdminUser | undefined>(undefined);

  const createOpen = useRef(creating);
  const editOpen = useRef(editing !== undefined);
  const manageOpen = useRef(managing !== undefined);
  useLayoutEffect(() => {
    createOpen.current = creating;
    editOpen.current = editing !== undefined;
    manageOpen.current = managing !== undefined;
  });

  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);
  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const openCreate = () => {
    create.reset();
    setCreating(true);
  };
  const openEdit = (u: AdminUser) => {
    edit.reset();
    setEditing(u);
  };
  const openManage = (u: AdminUser) => {
    setRoles.reset();
    setManaging(u);
  };

  const submitCreate = (request: UserCreateRequest) => {
    void create.run(request).then((settled) => {
      if (settled.outcome === "ignored") return;
      users.reload();
      if (settled.outcome === "succeeded") {
        setCreating(false);
        announce({ severity: "success", text: `${settled.data.email} was created.` });
        return;
      }
      if (!createOpen.current) announce({ severity: "error", text: describeApiError(settled.error) });
    });
  };

  const submitEdit = (id: string, request: UserUpdateRequest) => {
    void edit.run(id, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      users.reload();
      if (settled.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to ${settled.data.email}.` });
        return;
      }
      if (!editOpen.current) announce({ severity: "error", text: describeApiError(settled.error) });
    });
  };

  const submitRoles = (id: string, roleIds: string[]) => {
    void setRoles.run(id, roleIds).then((settled) => {
      if (settled.outcome === "ignored") return;
      users.reload();
      if (settled.outcome === "succeeded") {
        setManaging(undefined);
        announce({ severity: "success", text: `Updated roles for ${settled.data.email}.` });
        return;
      }
      if (!manageOpen.current) announce({ severity: "error", text: describeApiError(settled.error) });
    });
  };

  const toggleActive = (u: AdminUser) => {
    void setActive.run(u.id, !u.isActive).then((settled) => {
      if (settled.outcome === "ignored") return;
      users.reload();
      announce(
        settled.outcome === "succeeded"
          ? {
              severity: "success",
              text: u.isActive
                ? `${u.email} was deactivated — they can no longer sign in.`
                : `${u.email} was reactivated.`,
            }
          : { severity: "error", text: describeApiError(settled.error) },
      );
    });
  };

  const cols: GridColDef<AdminUser>[] = [
    { field: "email", headerName: "E-mail", flex: 1, minWidth: 200 },
    { field: "fullName", headerName: "Name", flex: 1, minWidth: 160 },
    {
      field: "roles",
      headerName: "Roles",
      flex: 1,
      minWidth: 200,
      sortable: false,
      valueGetter: (_v, row) => row.roles.map((r) => r.name).join(", "),
      renderCell: (p) =>
        p.row.roles.length === 0 ? (
          <Chip size="small" label="No roles" variant="outlined" color="warning" />
        ) : (
          <Stack direction="row" spacing={0.5} flexWrap="wrap" sx={{ py: 0.5 }}>
            {p.row.roles.map((r) => (
              <Chip key={r.id} size="small" label={r.name} variant="outlined" />
            ))}
          </Stack>
        ),
    },
    {
      field: "isActive",
      headerName: "Status",
      width: 120,
      valueGetter: (_v, row) => (row.isActive ? "Active" : "Deactivated"),
      renderCell: (p) =>
        p.row.isActive ? (
          <Chip size="small" label="Active" color="success" variant="outlined" />
        ) : (
          <Chip size="small" label="Deactivated" variant="outlined" />
        ),
    },
    {
      field: "lastLoginAt",
      headerName: "Last sign-in",
      width: 160,
      valueGetter: (_v, row) =>
        row.lastLoginAt ? new Date(row.lastLoginAt).toLocaleString() : NO_VALUE,
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
          user={p.row}
          onEdit={openEdit}
          onManageRoles={openManage}
          onToggleActive={toggleActive}
        />
      ),
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Users
        </Typography>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={openCreate}
          disabled={roles.status !== "ready"}
        >
          Create user
        </Button>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        A user signs in to the admin app. What they can do is decided entirely by their roles — a user
        with no role can sign in but do nothing. Deactivating a user stops them signing in while keeping
        everything they recorded.
      </Typography>

      {users.status === "loading" && <LoadingState label="Loading users…" />}
      {users.status === "error" && (
        <ErrorState subject="users" error={users.error} onRetry={users.reload} />
      )}

      {users.status === "ready" && (
        <>
          <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center", mb: 1 }}>
            {users.refreshing && (
              <Typography variant="body2" color="text.secondary">
                Refreshing…
              </Typography>
            )}
          </Box>

          {users.data.length === 0 ? (
            <EmptyState message={NO_USERS} />
          ) : (
            <div style={{ height: 560, width: "100%" }}>
              <DataGrid
                rows={users.data}
                columns={cols}
                getRowId={(r) => r.id}
                disableRowSelectionOnClick
                pageSizeOptions={[10, 25, 50]}
                initialState={{ pagination: { paginationModel: { pageSize: 25 } } }}
              />
            </div>
          )}
        </>
      )}

      {creating && (
        <NewUserDialog
          roles={roles.data ?? []}
          users={users.data ?? []}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <EditUserDialog
          user={editing}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {managing !== undefined && (
        <UserRolesDialog
          user={managing}
          roles={roles.data ?? []}
          onClose={() => setManaging(undefined)}
          onSubmit={(roleIds) => submitRoles(managing.id, roleIds)}
          running={setRoles.status === "running"}
          failure={setRoles.status === "failed" ? { error: setRoles.error } : undefined}
        />
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
  user,
  onEdit,
  onManageRoles,
  onToggleActive,
}: {
  user: AdminUser;
  onEdit: (u: AdminUser) => void;
  onManageRoles: (u: AdminUser) => void;
  onToggleActive: (u: AdminUser) => void;
}) {
  const toggleLabel = user.isActive ? `Deactivate ${user.email}` : `Reactivate ${user.email}`;
  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton sx={{ p: 1 }} onClick={() => onEdit(user)} aria-label={`Edit ${user.email}`} title={`Edit ${user.email}`}>
        <EditIcon fontSize="small" />
      </IconButton>
      <IconButton
        sx={{ p: 1 }}
        onClick={() => onManageRoles(user)}
        aria-label={`Manage roles for ${user.email}`}
        title={`Manage roles for ${user.email}`}
      >
        <BadgeIcon fontSize="small" />
      </IconButton>
      <IconButton
        sx={{ p: 1 }}
        color={user.isActive ? "warning" : "success"}
        onClick={() => onToggleActive(user)}
        aria-label={toggleLabel}
        title={toggleLabel}
      >
        {user.isActive ? <ToggleOffIcon fontSize="small" /> : <ToggleOnIcon fontSize="small" />}
      </IconButton>
    </Stack>
  );
}
