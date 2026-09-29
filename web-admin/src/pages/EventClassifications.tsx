// The event-classifications screen — the master vocabulary an event's classification is chosen from
// (Institutional / Departmental / Organizational, extensible). The shape follows `Terms.tsx`: the page
// owns every write, so dismissing a dialog does not end the request that dialog started.
//
// Deactivating is the safe half of "delete" and is offered on every row; the hard delete is offered too
// but the server refuses it for a classification an event uses (`InUse`) or one of the seeded three
// (`SeedProtected`), and the confirmation says so when it happens.

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
import DeleteIcon from "@mui/icons-material/Delete";
import ToggleOnIcon from "@mui/icons-material/ToggleOn";
import ToggleOffIcon from "@mui/icons-material/ToggleOff";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import NewEventClassificationDialog from "../components/NewEventClassificationDialog";
import EditEventClassificationDialog from "../components/EditEventClassificationDialog";
import type { EventClassification, EventClassificationWriteRequest } from "../types";

const loadClassifications = () => api.listEventClassifications(true);

const NO_CLASSIFICATIONS =
  "This school has no event classifications yet. A classification — the kind of an event, such as " +
  "“Institutional Events” — is chosen when an event is created. Create the first one here.";

const NO_VALUE = "—";

interface Notice {
  severity: "success" | "error";
  text: string;
}

const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function EventClassifications() {
  const classifications = useApiResource(loadClassifications, []);

  const create = useApiMutation((request: EventClassificationWriteRequest) =>
    api.createEventClassification(request),
  );
  const edit = useApiMutation((id: string, request: EventClassificationWriteRequest) =>
    api.updateEventClassification(id, request),
  );
  const setActive = useApiMutation((id: string, isActive: boolean) =>
    api.setEventClassificationActive(id, isActive),
  );
  const remove = useApiMutation((id: string) => api.deleteEventClassification(id));

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<EventClassification | undefined>(undefined);
  const [deleting, setDeleting] = useState<EventClassification | undefined>(undefined);

  const rows = classifications.data;

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

  // ------------------------------------------------------------------------------------- opening

  const openCreate = () => {
    create.reset();
    setCreating(true);
  };

  const openEdit = (c: EventClassification) => {
    edit.reset();
    setEditing(c);
  };

  const openDelete = (c: EventClassification) => {
    remove.reset();
    setDeleting(c);
  };

  // ------------------------------------------------------------------------------------ settling

  const submitCreate = (request: EventClassificationWriteRequest) => {
    void create.run(request).then((settled) => {
      if (settled.outcome === "ignored") return;
      classifications.reload();

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

  const submitEdit = (id: string, request: EventClassificationWriteRequest) => {
    void edit.run(id, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      classifications.reload();

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

  const toggleActive = (c: EventClassification) => {
    void setActive.run(c.id, !c.isActive).then((settled) => {
      if (settled.outcome === "ignored") return;
      classifications.reload();

      announce(
        settled.outcome === "succeeded"
          ? {
              severity: "success",
              text: c.isActive
                ? `“${c.name}” was deactivated — it is no longer offered for new events.`
                : `“${c.name}” was reactivated.`,
            }
          : { severity: "error", text: describeApiError(settled.error) },
      );
    });
  };

  const confirmDelete = (c: EventClassification) => {
    void remove.run(c.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      classifications.reload();

      if (settled.outcome === "succeeded") {
        setDeleting(undefined);
        announce({ severity: "success", text: `“${c.name}” was deleted.` });
        return;
      }

      // The refusals (InUse / SeedProtected) stay in the dialog so the operator reads the reason where
      // they pressed. If the dialog was dismissed first, announce it instead.
      if (!deleteOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  // --------------------------------------------------------------------------------------- grid

  const cols: GridColDef<EventClassification>[] = [
    { field: "name", headerName: "Name", flex: 1, minWidth: 200 },
    {
      field: "description",
      headerName: "Description",
      flex: 2,
      minWidth: 260,
      valueGetter: (_v, row) => row.description ?? NO_VALUE,
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
      width: 160,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <RowActions
          classification={p.row}
          onEdit={openEdit}
          onToggleActive={toggleActive}
          onDelete={openDelete}
        />
      ),
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Event classifications
        </Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate}>
          Create classification
        </Button>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        An event classification is the kind of an event — Institutional, Departmental, Organizational, or
        one of your own. It is chosen when an event is created and decides which audiences the event can
        target. Deactivating one withdraws it from the picker without touching the events already
        recorded under it.
      </Typography>

      {classifications.status === "loading" && <LoadingState label="Loading event classifications…" />}

      {classifications.status === "error" && (
        <ErrorState subject="event classifications" error={classifications.error} onRetry={classifications.reload} />
      )}

      {classifications.status === "ready" && (
        <>
          <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center", mb: 1 }}>
            {classifications.refreshing && (
              <Typography variant="body2" color="text.secondary">
                Refreshing…
              </Typography>
            )}
          </Box>

          {classifications.data.length === 0 ? (
            <EmptyState message={NO_CLASSIFICATIONS} />
          ) : (
            <div style={{ height: 520, width: "100%" }}>
              <DataGrid
                rows={classifications.data}
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
        <NewEventClassificationDialog
          classifications={rows ?? []}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <EditEventClassificationDialog
          classification={editing}
          classifications={rows ?? []}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {deleting !== undefined && (
        <Dialog open onClose={() => setDeleting(undefined)} fullWidth maxWidth="sm">
          <DialogTitle>Delete “{deleting.name}”?</DialogTitle>
          <DialogContent>
            <DialogContentText>
              This removes the classification entirely. It is refused if an event is classified as it, or
              if it is one of the seeded classifications — in those cases deactivate it instead, which
              keeps every event’s recorded classification.
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
  classification,
  onEdit,
  onToggleActive,
  onDelete,
}: {
  classification: EventClassification;
  onEdit: (c: EventClassification) => void;
  onToggleActive: (c: EventClassification) => void;
  onDelete: (c: EventClassification) => void;
}) {
  const toggleLabel = classification.isActive
    ? `Deactivate ${classification.name}`
    : `Reactivate ${classification.name}`;

  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton
        sx={{ p: 1.25 }}
        onClick={() => onEdit(classification)}
        aria-label={`Edit ${classification.name}`}
        title={`Edit ${classification.name}`}
      >
        <EditIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color={classification.isActive ? "warning" : "success"}
        onClick={() => onToggleActive(classification)}
        aria-label={toggleLabel}
        title={toggleLabel}
      >
        {classification.isActive ? (
          <ToggleOffIcon fontSize="small" />
        ) : (
          <ToggleOnIcon fontSize="small" />
        )}
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color="error"
        onClick={() => onDelete(classification)}
        aria-label={`Delete ${classification.name}`}
        title={`Delete ${classification.name}`}
      >
        <DeleteIcon fontSize="small" />
      </IconButton>
    </Stack>
  );
}
