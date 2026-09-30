// The Event Audiences screen — the reusable audience definitions an event's audience is chosen from, each
// filed under an event classification and resolving to eligible attendees from the live roster. The shape
// follows `EventClassifications.tsx`: the page owns every write, so dismissing a dialog does not end the
// request that dialog started.
//
// Deactivating is the safe half of "delete" and is offered on every row; the hard delete is offered too
// (no event references a definition yet, so it is unguarded beyond a confirmation). "View attendees"
// resolves the definition against the current roster on demand.

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
  List,
  ListItem,
  ListItemText,
  Snackbar,
  Stack,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import DeleteIcon from "@mui/icons-material/Delete";
import GroupsIcon from "@mui/icons-material/Groups";
import ToggleOnIcon from "@mui/icons-material/ToggleOn";
import ToggleOffIcon from "@mui/icons-material/ToggleOff";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import EventAudienceFormDialog from "../components/EventAudienceFormDialog";
import { labelForAudienceType } from "../eventAudienceDraft";
import type {
  AudienceDefinition,
  AudienceDefinitionWriteRequest,
  ResolvedAudience,
} from "../types";

const loadAudiences = () => api.listAudienceDefinitions({ includeInactive: true });
const loadClassifications = () => api.listEventClassifications();

const NO_AUDIENCES =
  "This school has no event audiences yet. An audience — such as “All BSIT Students” or " +
  "“Faculty and Staff” — is a reusable definition of who is eligible for an event. Create the first one " +
  "here.";

interface Notice {
  severity: "success" | "error";
  text: string;
}

const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function EventAudiences() {
  const audiences = useApiResource(loadAudiences, []);
  const classifications = useApiResource(loadClassifications, []);

  const create = useApiMutation((request: AudienceDefinitionWriteRequest) =>
    api.createAudienceDefinition(request),
  );
  const edit = useApiMutation((id: string, request: AudienceDefinitionWriteRequest) =>
    api.updateAudienceDefinition(id, request),
  );
  const setActive = useApiMutation((id: string, isActive: boolean) =>
    api.setAudienceDefinitionActive(id, isActive),
  );
  const remove = useApiMutation((id: string) => api.deleteAudienceDefinition(id));
  const resolve = useApiMutation((id: string) => api.resolveAudience(id));

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<AudienceDefinition | undefined>(undefined);
  const [deleting, setDeleting] = useState<AudienceDefinition | undefined>(undefined);
  const [viewing, setViewing] = useState<AudienceDefinition | undefined>(undefined);
  const [resolved, setResolved] = useState<ResolvedAudience | undefined>(undefined);

  const createOpen = useRef(creating);
  const editOpen = useRef(editing !== undefined);
  const deleteOpen = useRef(deleting !== undefined);
  useLayoutEffect(() => {
    createOpen.current = creating;
    editOpen.current = editing !== undefined;
    deleteOpen.current = deleting !== undefined;
  });

  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);

  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  // The form's classification picker offers only active classifications; a deactivated one an existing
  // audience already holds is kept by the server, so editing that audience does not need it in the list.
  const activeClassifications = (classifications.data ?? []).filter((c) => c.isActive);

  // ------------------------------------------------------------------------------------- opening

  const openCreate = () => {
    create.reset();
    setCreating(true);
  };

  const openEdit = (a: AudienceDefinition) => {
    edit.reset();
    setEditing(a);
  };

  const openDelete = (a: AudienceDefinition) => {
    remove.reset();
    setDeleting(a);
  };

  const openView = (a: AudienceDefinition) => {
    resolve.reset();
    setResolved(undefined);
    setViewing(a);
    void resolve.run(a.id).then((settled) => {
      if (settled.outcome === "succeeded") setResolved(settled.data);
    });
  };

  // ------------------------------------------------------------------------------------ settling

  const submitCreate = (request: AudienceDefinitionWriteRequest) => {
    void create.run(request).then((settled) => {
      if (settled.outcome === "ignored") return;
      audiences.reload();

      if (settled.outcome === "succeeded") {
        setCreating(false);
        announce({ severity: "success", text: `“${settled.data.name}” was created.` });
        return;
      }

      if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const submitEdit = (id: string, request: AudienceDefinitionWriteRequest) => {
    void edit.run(id, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      audiences.reload();

      if (settled.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to “${settled.data.name}”.` });
        return;
      }

      if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const toggleActive = (a: AudienceDefinition) => {
    void setActive.run(a.id, !a.isActive).then((settled) => {
      if (settled.outcome === "ignored") return;
      audiences.reload();

      announce(
        settled.outcome === "succeeded"
          ? {
              severity: "success",
              text: a.isActive
                ? `“${a.name}” was deactivated — it is no longer offered for new events.`
                : `“${a.name}” was reactivated.`,
            }
          : { severity: "error", text: describeApiError(settled.error) },
      );
    });
  };

  const confirmDelete = (a: AudienceDefinition) => {
    void remove.run(a.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      audiences.reload();

      if (settled.outcome === "succeeded") {
        setDeleting(undefined);
        announce({ severity: "success", text: `“${a.name}” was deleted.` });
        return;
      }

      if (!deleteOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  // --------------------------------------------------------------------------------------- grid

  const cols: GridColDef<AudienceDefinition>[] = [
    { field: "name", headerName: "Name", flex: 1, minWidth: 200 },
    {
      field: "audienceType",
      headerName: "Type",
      width: 190,
      valueGetter: (_v, row) => labelForAudienceType(row.audienceType),
    },
    {
      field: "eventClassificationName",
      headerName: "Classification",
      flex: 1,
      minWidth: 180,
    },
    {
      field: "isActive",
      headerName: "Status",
      width: 130,
      valueGetter: (_v, row) => (row.isActive ? "Active" : "Deactivated"),
      renderCell: (p) =>
        p.row.isActive ? (
          <Chip size="small" label="Active" color="success" variant="outlined" />
        ) : (
          <Chip size="small" label="Deactivated" variant="outlined" />
        ),
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 200,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <RowActions
          audience={p.row}
          onView={openView}
          onEdit={openEdit}
          onToggleActive={toggleActive}
          onDelete={openDelete}
        />
      ),
    },
  ];

  const formError = classifications.status === "error";

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Event audiences
        </Typography>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={openCreate}
          disabled={activeClassifications.length === 0}
        >
          Create audience
        </Button>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        An event audience is a reusable definition of who is eligible for an event — everyone, a department,
        a program, a year level, a list of named people, or a custom combination. It is filed under an event
        classification and resolves to attendees from the current roster each time. Deactivating one
        withdraws it from the picker without touching anything recorded against it.
      </Typography>

      {formError && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          The event classifications could not be loaded, so creating and editing are disabled. {" "}
          {describeApiError(classifications.error)}
        </Alert>
      )}

      {!formError && activeClassifications.length === 0 && classifications.status === "ready" && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Create an active event classification first — an audience is filed under one.
        </Alert>
      )}

      {audiences.status === "loading" && <LoadingState label="Loading event audiences…" />}

      {audiences.status === "error" && (
        <ErrorState subject="event audiences" error={audiences.error} onRetry={audiences.reload} />
      )}

      {audiences.status === "ready" && (
        <>
          <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center", mb: 1 }}>
            {audiences.refreshing && (
              <Typography variant="body2" color="text.secondary">
                Refreshing…
              </Typography>
            )}
          </Box>

          {audiences.data.length === 0 ? (
            <EmptyState message={NO_AUDIENCES} />
          ) : (
            <div style={{ height: 520, width: "100%" }}>
              <DataGrid
                rows={audiences.data}
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
        <EventAudienceFormDialog
          classifications={activeClassifications}
          others={audiences.data ?? []}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <EventAudienceFormDialog
          definition={editing}
          classifications={activeClassifications}
          others={(audiences.data ?? []).filter((a) => a.id !== editing.id)}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {viewing !== undefined && (
        <AttendeesDialog
          audience={viewing}
          resolved={resolved}
          loading={resolve.status === "running"}
          error={resolve.status === "failed" ? resolve.error : undefined}
          onClose={() => setViewing(undefined)}
        />
      )}

      {deleting !== undefined && (
        <Dialog open onClose={() => setDeleting(undefined)} fullWidth maxWidth="sm">
          <DialogTitle>Delete “{deleting.name}”?</DialogTitle>
          <DialogContent>
            <DialogContentText>
              This removes the audience definition entirely. Deactivating it instead keeps it for reference
              and only withdraws it from the event-creation picker.
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

function AttendeesDialog({
  audience,
  resolved,
  loading,
  error,
  onClose,
}: {
  audience: AudienceDefinition;
  resolved: ResolvedAudience | undefined;
  loading: boolean;
  error: unknown;
  onClose: () => void;
}) {
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Attendees of “{audience.name}”</DialogTitle>
      <DialogContent>
        {loading && <LoadingState label="Resolving the current roster…" />}

        {error !== undefined && (
          <Alert severity="error" role="alert">
            {describeApiError(error)}
          </Alert>
        )}

        {resolved !== undefined && (
          <>
            <Stack direction="row" spacing={1} sx={{ mb: 2 }}>
              <Chip label={`${resolved.studentCount} student(s)`} color="primary" variant="outlined" />
              <Chip label={`${resolved.personnelCount} employee(s)`} color="secondary" variant="outlined" />
            </Stack>

            {resolved.attendees.length === 0 ? (
              <Typography variant="body2" color="text.secondary">
                No one in the current roster matches this audience.
              </Typography>
            ) : (
              <>
                <List dense>
                  {resolved.attendees.map((a) => (
                    <ListItem key={`${a.type}-${a.id}`} disableGutters>
                      <ListItemText
                        primary={`${a.fullName} (${a.number})`}
                        secondary={`${a.type}${a.departmentOrProgram ? ` · ${a.departmentOrProgram}` : ""}`}
                      />
                    </ListItem>
                  ))}
                </List>
                {resolved.attendees.length < resolved.studentCount + resolved.personnelCount && (
                  <Typography variant="caption" color="text.secondary">
                    Showing the first {resolved.attendees.length} of{" "}
                    {resolved.studentCount + resolved.personnelCount}.
                  </Typography>
                )}
              </>
            )}
          </>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
      </DialogActions>
    </Dialog>
  );
}

function RowActions({
  audience,
  onView,
  onEdit,
  onToggleActive,
  onDelete,
}: {
  audience: AudienceDefinition;
  onView: (a: AudienceDefinition) => void;
  onEdit: (a: AudienceDefinition) => void;
  onToggleActive: (a: AudienceDefinition) => void;
  onDelete: (a: AudienceDefinition) => void;
}) {
  const toggleLabel = audience.isActive
    ? `Deactivate ${audience.name}`
    : `Reactivate ${audience.name}`;

  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton
        sx={{ p: 1.25 }}
        onClick={() => onView(audience)}
        aria-label={`View attendees of ${audience.name}`}
        title={`View attendees of ${audience.name}`}
      >
        <GroupsIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        onClick={() => onEdit(audience)}
        aria-label={`Edit ${audience.name}`}
        title={`Edit ${audience.name}`}
      >
        <EditIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color={audience.isActive ? "warning" : "success"}
        onClick={() => onToggleActive(audience)}
        aria-label={toggleLabel}
        title={toggleLabel}
      >
        {audience.isActive ? <ToggleOffIcon fontSize="small" /> : <ToggleOnIcon fontSize="small" />}
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color="error"
        onClick={() => onDelete(audience)}
        aria-label={`Delete ${audience.name}`}
        title={`Delete ${audience.name}`}
      >
        <DeleteIcon fontSize="small" />
      </IconButton>
    </Stack>
  );
}
