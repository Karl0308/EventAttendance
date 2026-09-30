// The Clearance Checker (Clearance-Checker-Module.docx). Search a student by ID, name or RFID, then show
// the events they were eligible for and how they attended each — Attended, Missed, Excused, Late. No
// clearance status is computed; the administrator reads the table. Export is the server's CSV; the browser
// Print button is the PDF path.
//
// Search reuses GET /students?search=, which already matches student number, name AND card serial, so one
// box covers all three of the spec's search modes.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  List,
  ListItemButton,
  ListItemText,
  Snackbar,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import SearchIcon from "@mui/icons-material/Search";
import DownloadIcon from "@mui/icons-material/Download";
import PrintIcon from "@mui/icons-material/Print";
import { api, describeApiError } from "../api";
import { useApiMutation } from "../useApiMutation";
import { saveBlob } from "../sisImport";
import type { ClearanceEvent, ClearanceReport, Student } from "../types";

/** A calendar date box to the instant bounds the API filters on. */
const fromInstant = (date: string): string | undefined =>
  date.length === 0 ? undefined : `${date}T00:00:00Z`;
const toInstant = (date: string): string | undefined =>
  date.length === 0 ? undefined : `${date}T23:59:59Z`;

const attendanceColor = (a: string): "success" | "error" | "warning" | "default" => {
  if (a === "Attended") return "success";
  if (a === "Late") return "warning";
  if (a === "Missed") return "error";
  return "default"; // Excused
};

export default function Clearance() {
  const [query, setQuery] = useState("");
  const [candidates, setCandidates] = useState<Student[] | undefined>(undefined);
  const [report, setReport] = useState<ClearanceReport | undefined>(undefined);
  const [dateFrom, setDateFrom] = useState("");
  const [dateTo, setDateTo] = useState("");
  const [notice, setNotice] = useState<string | undefined>(undefined);

  const search = useApiMutation((q: string) => api.listStudentsPage({ search: q }, 1, 25));
  const load = useApiMutation((id: string, from?: string, to?: string) =>
    api.clearanceReport(id, from, to),
  );
  const exportCsv = useApiMutation((id: string, from?: string, to?: string) =>
    api.downloadClearanceCsv(id, from, to),
  );

  const runSearch = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (query.trim().length === 0) return;
    setReport(undefined);
    void search.run(query.trim()).then((s) => {
      if (s.outcome === "succeeded") setCandidates(s.data.students);
    });
  };

  const pick = (student: Student) => {
    setCandidates(undefined);
    void load.run(student.id, fromInstant(dateFrom), toInstant(dateTo)).then((s) => {
      if (s.outcome === "succeeded") setReport(s.data);
      else if (s.outcome === "failed") setNotice(describeApiError(s.error));
    });
  };

  const reloadWithDates = () => {
    if (report === undefined) return;
    void load.run(report.student.studentId, fromInstant(dateFrom), toInstant(dateTo)).then((s) => {
      if (s.outcome === "succeeded") setReport(s.data);
      else if (s.outcome === "failed") setNotice(describeApiError(s.error));
    });
  };

  const doExport = () => {
    if (report === undefined) return;
    void exportCsv
      .run(report.student.studentId, fromInstant(dateFrom), toInstant(dateTo))
      .then((s) => {
        if (s.outcome === "succeeded") saveBlob(s.data.blob, s.data.filename);
        else if (s.outcome === "failed") setNotice(describeApiError(s.error));
      });
  };

  const cols: GridColDef<ClearanceEvent>[] = [
    { field: "eventName", headerName: "Event", flex: 1, minWidth: 200 },
    {
      field: "eventDate",
      headerName: "Date",
      width: 120,
      valueGetter: (_v, r) => r.eventDate.slice(0, 10),
    },
    {
      field: "attendance",
      headerName: "Attendance",
      width: 140,
      renderCell: (p) => (
        <Chip size="small" label={p.row.attendance} color={attendanceColor(p.row.attendance)} variant="outlined" />
      ),
    },
    {
      field: "checkInAt",
      headerName: "Check-in",
      width: 160,
      valueGetter: (_v, r) => (r.checkInAt ? new Date(r.checkInAt).toLocaleString() : "—"),
    },
    {
      field: "checkOutAt",
      headerName: "Check-out",
      width: 160,
      valueGetter: (_v, r) => (r.checkOutAt ? new Date(r.checkOutAt).toLocaleString() : "—"),
    },
  ];

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} sx={{ mb: 1 }}>
        Clearance Checker
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Search a student by ID, name or RFID card, then review the events they were eligible for and how
        they attended each. No clearance status is computed — the results are shown for you to interpret.
      </Typography>

      <form onSubmit={runSearch}>
        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} sx={{ mb: 2 }}>
          <TextField
            label="Student ID, name, or RFID"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            size="small"
            sx={{ flex: 1 }}
            autoFocus
          />
          <Button type="submit" variant="contained" startIcon={<SearchIcon />} disabled={search.status === "running"}>
            {search.status === "running" ? "Searching…" : "Search"}
          </Button>
        </Stack>
      </form>

      {search.status === "failed" && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {describeApiError(search.error)}
        </Alert>
      )}

      {candidates !== undefined && (
        candidates.length === 0 ? (
          <Alert severity="info" sx={{ mb: 2 }}>
            No students matched “{query}”.
          </Alert>
        ) : (
          <Card variant="outlined" sx={{ mb: 2 }}>
            <List dense>
              {candidates.map((s) => (
                <ListItemButton key={s.id} onClick={() => pick(s)}>
                  <ListItemText primary={s.fullName} secondary={s.studentNumber} />
                </ListItemButton>
              ))}
            </List>
          </Card>
        )
      )}

      {load.status === "running" && <Typography color="text.secondary">Loading report…</Typography>}

      {report !== undefined && (
        <Box>
          <Card variant="outlined" sx={{ mb: 2 }}>
            <CardContent>
              <Typography variant="h6">{report.student.fullName}</Typography>
              <Stack direction="row" spacing={3} flexWrap="wrap" sx={{ mt: 1 }}>
                <Field label="Student ID" value={report.student.studentNumber} />
                <Field label="Department" value={report.student.department} />
                <Field label="Program" value={report.student.program} />
                <Field label="College" value={report.student.college} />
                <Field label="Year level" value={report.student.yearLevel} />
                <Field label="Section" value={report.student.section} />
              </Stack>
            </CardContent>
          </Card>

          <Stack direction={{ xs: "column", sm: "row" }} spacing={1} alignItems="center" sx={{ mb: 1 }}>
            <TextField
              label="From"
              type="date"
              size="small"
              value={dateFrom}
              onChange={(e) => setDateFrom(e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
            />
            <TextField
              label="To"
              type="date"
              size="small"
              value={dateTo}
              onChange={(e) => setDateTo(e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
            />
            <Button onClick={reloadWithDates} disabled={load.status === "running"}>
              Apply dates
            </Button>
            <Box sx={{ flex: 1 }} />
            <Button startIcon={<DownloadIcon />} onClick={doExport} disabled={exportCsv.status === "running"}>
              Export CSV
            </Button>
            <Button startIcon={<PrintIcon />} onClick={() => window.print()}>
              Print / PDF
            </Button>
          </Stack>

          <div style={{ height: 460, width: "100%" }}>
            <DataGrid
              rows={report.events}
              columns={cols}
              getRowId={(r) => r.eventId}
              disableRowSelectionOnClick
              pageSizeOptions={[10, 25, 50]}
              initialState={{ pagination: { paginationModel: { pageSize: 25 } } }}
            />
          </div>
        </Box>
      )}

      <Snackbar
        open={notice !== undefined}
        autoHideDuration={6000}
        onClose={() => setNotice(undefined)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {notice !== undefined ? (
          <Alert severity="error" role="alert" onClose={() => setNotice(undefined)}>
            {notice}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Box>
  );
}

function Field({ label, value }: { label: string; value?: string }) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" display="block">
        {label}
      </Typography>
      <Typography variant="body2">{value && value.length > 0 ? value : "—"}</Typography>
    </Box>
  );
}
