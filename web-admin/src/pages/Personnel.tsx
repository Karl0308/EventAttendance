// The Personnel screen — the Academic Community's Personnel tab (StudentsEmployees.docx). Faculty and
// employees master data. The page owns every write, as `Terms.tsx` does. Removal is a soft delete on the
// server; a removed record leaves the list but is kept so future attendance can still resolve.
//
// Filtering uses the DataGrid's built-in column filters and quick-search toolbar over the whole set — a
// school's staff list is small enough to hold client-side, which is why `api.listPersonnel` fetches all.

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
import { DataGrid, GridToolbar, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import DeleteIcon from "@mui/icons-material/Delete";
import DownloadIcon from "@mui/icons-material/Download";
import UploadFileIcon from "@mui/icons-material/UploadFile";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import PersonnelFormDialog from "../components/PersonnelFormDialog";
import PersonnelImportDialog from "../components/PersonnelImportDialog";
import { saveBlob } from "../sisImport";
import type { Personnel, PersonnelImportResult, PersonnelWriteRequest } from "../types";

const loadPersonnel = () => api.listPersonnel();
const loadOrganizations = () => api.listPersonnelOrganizations();

/** Stable empty list, so the dialog's `organizations` prop does not change identity while loading. */
const NO_ORGANIZATIONS: readonly string[] = [];

const NO_PERSONNEL =
  "This school has no personnel yet. Personnel are faculty and employees — add them here, or import them " +
  "from a CSV.";

const NO_VALUE = "—";

interface Notice {
  severity: "success" | "error";
  text: string;
}
const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function PersonnelPage() {
  const personnel = useApiResource(loadPersonnel, []);
  // Server state for the form's suggestions. Its failure must not block the page or the form (the field
  // takes typed input regardless), but it is shown below rather than swallowed.
  const organizations = useApiResource(loadOrganizations, []);
  const organizationOptions = organizations.status === "ready" ? organizations.data : NO_ORGANIZATIONS;

  const create = useApiMutation((request: PersonnelWriteRequest) => api.createPersonnel(request));
  const edit = useApiMutation((id: string, request: PersonnelWriteRequest) => api.updatePersonnel(id, request));
  const remove = useApiMutation((id: string) => api.deletePersonnel(id));
  const importMut = useApiMutation((rows: PersonnelWriteRequest[]) => api.importPersonnel(rows));
  const download = useApiMutation(() => api.downloadPersonnelCsv());

  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Personnel | undefined>(undefined);
  const [deleting, setDeleting] = useState<Personnel | undefined>(undefined);
  const [importing, setImporting] = useState(false);
  const [importResult, setImportResult] = useState<PersonnelImportResult | undefined>(undefined);

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

  const submitCreate = (request: PersonnelWriteRequest) => {
    void create.run(request).then((s) => {
      if (s.outcome === "ignored") return;
      personnel.reload();
      if (s.outcome === "succeeded") {
        organizations.reload();
        setCreating(false);
        announce({ severity: "success", text: `${s.data.fullName} was added.` });
      } else if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const openImport = () => {
    importMut.reset();
    setImportResult(undefined);
    setImporting(true);
  };

  const runImport = (rows: PersonnelWriteRequest[]) => {
    void importMut.run(rows).then((s) => {
      if (s.outcome === "ignored") return;
      if (s.outcome === "succeeded") {
        setImportResult(s.data);
        personnel.reload();
        organizations.reload();
      }
      // A failure (e.g. empty/oversized batch) surfaces in the dialog via importMut.error.
    });
  };

  const runExport = () => {
    void download.run().then((s) => {
      if (s.outcome === "succeeded") saveBlob(s.data.blob, s.data.filename);
      else if (s.outcome === "failed") announce({ severity: "error", text: describeApiError(s.error) });
    });
  };

  const submitEdit = (id: string, request: PersonnelWriteRequest) => {
    void edit.run(id, request).then((s) => {
      if (s.outcome === "ignored") return;
      personnel.reload();
      if (s.outcome === "succeeded") {
        organizations.reload();
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to ${s.data.fullName}.` });
      } else if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const confirmDelete = (p: Personnel) => {
    void remove.run(p.id).then((s) => {
      if (s.outcome === "ignored") return;
      personnel.reload();
      if (s.outcome === "succeeded") {
        setDeleting(undefined);
        announce({ severity: "success", text: `${p.fullName} was removed.` });
      } else if (!deleteOpen.current) {
        announce({ severity: "error", text: describeApiError(s.error) });
      }
    });
  };

  const cols: GridColDef<Personnel>[] = [
    { field: "personnelNumber", headerName: "ID", width: 120 },
    { field: "fullName", headerName: "Name", flex: 1, minWidth: 180 },
    { field: "classification", headerName: "Classification", width: 130, valueGetter: (_v, r) => r.classification ?? NO_VALUE },
    { field: "department", headerName: "Department", width: 130, valueGetter: (_v, r) => r.department ?? NO_VALUE },
    { field: "position", headerName: "Position", width: 140, valueGetter: (_v, r) => r.position ?? NO_VALUE },
    { field: "rfidUid", headerName: "RFID UID", width: 140, valueGetter: (_v, r) => r.rfidUid ?? NO_VALUE },
    {
      field: "status",
      headerName: "Status",
      width: 120,
      renderCell: (p) =>
        p.row.status === "Active" ? (
          <Chip size="small" label="Active" color="success" variant="outlined" />
        ) : (
          <Chip size="small" label={p.row.status} variant="outlined" />
        ),
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 110,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
          <IconButton
            sx={{ p: 1 }}
            onClick={() => {
              edit.reset();
              setEditing(p.row);
            }}
            aria-label={`Edit ${p.row.fullName}`}
            title={`Edit ${p.row.fullName}`}
          >
            <EditIcon fontSize="small" />
          </IconButton>
          <IconButton
            sx={{ p: 1 }}
            color="error"
            onClick={() => {
              remove.reset();
              setDeleting(p.row);
            }}
            aria-label={`Remove ${p.row.fullName}`}
            title={`Remove ${p.row.fullName}`}
          >
            <DeleteIcon fontSize="small" />
          </IconButton>
        </Stack>
      ),
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Personnel
        </Typography>
        <Stack direction="row" spacing={1}>
          <Button
            startIcon={<DownloadIcon />}
            onClick={runExport}
            disabled={download.status === "running"}
          >
            {download.status === "running" ? "Exporting…" : "Export CSV"}
          </Button>
          <Button startIcon={<UploadFileIcon />} onClick={openImport}>
            Import CSV
          </Button>
          <Button
            variant="contained"
            startIcon={<AddIcon />}
            onClick={() => {
              create.reset();
              setCreating(true);
            }}
          >
            Add personnel
          </Button>
        </Stack>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Personnel are the school’s faculty and employees. Use the column filters and the search box to
        narrow the list. Removing a record keeps it for historical attendance and frees its ID and card
        for reuse.
      </Typography>

      {organizations.status === "error" && (
        <Alert
          severity="warning"
          role="alert"
          sx={{ mb: 2 }}
          action={
            <Button color="inherit" size="small" onClick={organizations.reload}>
              Retry
            </Button>
          }
        >
          Organization suggestions could not be loaded ({describeApiError(organizations.error)}). You can still
          type an organization when adding or editing personnel.
        </Alert>
      )}

      {personnel.status === "loading" && <LoadingState label="Loading personnel…" />}
      {personnel.status === "error" && (
        <ErrorState subject="personnel" error={personnel.error} onRetry={personnel.reload} />
      )}

      {personnel.status === "ready" && (
        <>
          <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center", mb: 1 }}>
            {personnel.refreshing && (
              <Typography variant="body2" color="text.secondary">
                Refreshing…
              </Typography>
            )}
          </Box>
          {personnel.data.length === 0 ? (
            <EmptyState message={NO_PERSONNEL} />
          ) : (
            <div style={{ height: 600, width: "100%" }}>
              <DataGrid
                rows={personnel.data}
                columns={cols}
                getRowId={(r) => r.id}
                disableRowSelectionOnClick
                slots={{ toolbar: GridToolbar }}
                slotProps={{ toolbar: { showQuickFilter: true } }}
                pageSizeOptions={[25, 50, 100]}
                initialState={{ pagination: { paginationModel: { pageSize: 25 } } }}
              />
            </div>
          )}
        </>
      )}

      {creating && (
        <PersonnelFormDialog
          personnel={undefined}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
          organizations={organizationOptions}
        />
      )}

      {editing !== undefined && (
        <PersonnelFormDialog
          personnel={editing}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
          organizations={organizationOptions}
        />
      )}

      {deleting !== undefined && (
        <Dialog open onClose={() => setDeleting(undefined)} fullWidth maxWidth="sm">
          <DialogTitle>Remove {deleting.fullName}?</DialogTitle>
          <DialogContent>
            <DialogContentText>
              This removes the personnel record from the list. It is kept internally so any past attendance
              still resolves, and its ID and card become available again.
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
              {remove.status === "running" ? "Removing…" : "Remove"}
            </Button>
          </DialogActions>
        </Dialog>
      )}

      {importing && (
        <PersonnelImportDialog
          onClose={() => setImporting(false)}
          onImport={runImport}
          running={importMut.status === "running"}
          result={importResult}
          error={importMut.status === "failed" ? importMut.error : undefined}
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
