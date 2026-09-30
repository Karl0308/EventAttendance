// Attendance analytics (Reports-Module-Enhancement.docx RPT-01, scoped) — recorded student attendance
// grouped by course, year level or section over a date range, with a Present/Late/Absent/Excused breakdown
// and a rate. Drill-down is Group → Event: clicking a group row narrows to that group's events; an event row
// links into the Event Module. Export is the server-authored CSV.

import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Alert,
  Box,
  Breadcrumbs,
  Button,
  Checkbox,
  FormControlLabel,
  Link as MuiLink,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import DownloadIcon from "@mui/icons-material/Download";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import { saveBlob } from "../sisImport";
import type {
  AttendanceAnalyticsQuery,
  AttendanceAnalyticsReport,
  AttendanceAnalyticsRow,
  AttendanceGroupBy,
} from "../types";

type TopGroup = "Course" | "YearLevel" | "Section";

const GROUP_LABELS: Record<TopGroup, string> = {
  Course: "Course",
  YearLevel: "Year level",
  Section: "Section",
};

const FILTER_FIELD: Record<TopGroup, "course" | "yearLevel" | "section"> = {
  Course: "course",
  YearLevel: "yearLevel",
  Section: "section",
};

/** A date input's `YYYY-MM-DD` to a UTC instant at the start or end of that day. */
const startOfDay = (d: string) => (d ? `${d}T00:00:00Z` : undefined);
const endOfDay = (d: string) => (d ? `${d}T23:59:59Z` : undefined);

export default function AttendanceAnalytics() {
  const navigate = useNavigate();

  const [group, setGroup] = useState<TopGroup>("Course");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [includeCancelled, setIncludeCancelled] = useState(false);
  const [drillKey, setDrillKey] = useState<string | undefined>(undefined);

  const query: AttendanceAnalyticsQuery = useMemo(() => {
    const base: AttendanceAnalyticsQuery = {
      groupBy: (drillKey === undefined ? group : "Event") as AttendanceGroupBy,
      from: startOfDay(from),
      to: endOfDay(to),
      includeCancelled,
    };
    if (drillKey !== undefined) base[FILTER_FIELD[group]] = drillKey;
    return base;
  }, [group, from, to, includeCancelled, drillKey]);

  const report = useApiResource<AttendanceAnalyticsReport>(
    () => api.getAttendanceAnalytics(query),
    [query],
  );

  const download = useApiMutation(() => api.downloadAttendanceAnalyticsCsv(query));
  const [downloadError, setDownloadError] = useState<string | undefined>(undefined);

  const runDownload = () => {
    setDownloadError(undefined);
    void download.run().then((settled) => {
      if (settled.outcome === "succeeded") saveBlob(settled.data.blob, settled.data.filename);
      else if (settled.outcome === "failed") setDownloadError(describeApiError(settled.error));
    });
  };

  const atEventLevel = drillKey !== undefined;

  const rows = report.data?.rows ?? [];
  const totals = report.data?.totals;

  const cols: GridColDef<AttendanceAnalyticsRow>[] = [
    {
      field: "key",
      headerName: atEventLevel ? "Event" : GROUP_LABELS[group],
      flex: 1,
      minWidth: 200,
      renderCell: (p) =>
        atEventLevel ? (
          p.row.key
        ) : (
          <MuiLink component="button" type="button" onClick={() => setDrillKey(p.row.key)} underline="hover">
            {p.row.key}
          </MuiLink>
        ),
    },
    ...(atEventLevel
      ? ([
          {
            field: "eventDate",
            headerName: "Date",
            width: 120,
            valueGetter: (_v, row) => (row.eventDate ? row.eventDate.slice(0, 10) : "—"),
          },
        ] as GridColDef<AttendanceAnalyticsRow>[])
      : ([
          { field: "totalEvents", headerName: "Events", width: 100, type: "number" },
        ] as GridColDef<AttendanceAnalyticsRow>[])),
    { field: "people", headerName: "People", width: 100, type: "number" },
    { field: "present", headerName: "Present", width: 100, type: "number" },
    { field: "late", headerName: "Late", width: 90, type: "number" },
    { field: "absent", headerName: "Absent", width: 90, type: "number" },
    { field: "excused", headerName: "Excused", width: 100, type: "number" },
    { field: "total", headerName: "Total", width: 90, type: "number" },
    {
      field: "attendanceRate",
      headerName: "Rate %",
      width: 100,
      type: "number",
      valueGetter: (_v, row) => row.attendanceRate,
      renderCell: (p) => `${p.row.attendanceRate.toFixed(1)}%`,
    },
    ...(atEventLevel
      ? ([
          {
            field: "actions",
            headerName: "",
            width: 140,
            sortable: false,
            filterable: false,
            disableColumnMenu: true,
            renderCell: (p) =>
              p.row.eventId ? (
                <Button size="small" onClick={() => navigate(`/events/${p.row.eventId}`)}>
                  View event
                </Button>
              ) : null,
          },
        ] as GridColDef<AttendanceAnalyticsRow>[])
      : []),
  ];

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} sx={{ mb: 1 }}>
        Attendance analytics
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Recorded student attendance grouped by course, year level or section. Click a group to drill down to
        its events, then open an event in the Event Module. (Attendance is recorded for students; personnel
        attendance, eligibility/registration rates and academic-year grouping are not yet part of the data.)
      </Typography>

      <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap" sx={{ mb: 2, rowGap: 1.5 }}>
        <ToggleButtonGroup
          exclusive
          size="small"
          value={group}
          onChange={(_e, next) => {
            if (next) {
              setGroup(next);
              setDrillKey(undefined);
            }
          }}
          disabled={atEventLevel}
        >
          <ToggleButton value="Course">Course</ToggleButton>
          <ToggleButton value="YearLevel">Year level</ToggleButton>
          <ToggleButton value="Section">Section</ToggleButton>
        </ToggleButtonGroup>

        <TextField
          size="small"
          label="From"
          type="date"
          value={from}
          onChange={(e) => setFrom(e.target.value)}
          InputLabelProps={{ shrink: true }}
        />
        <TextField
          size="small"
          label="To"
          type="date"
          value={to}
          onChange={(e) => setTo(e.target.value)}
          InputLabelProps={{ shrink: true }}
        />
        <FormControlLabel
          control={
            <Checkbox
              checked={includeCancelled}
              onChange={(e) => setIncludeCancelled(e.target.checked)}
            />
          }
          label="Include cancelled"
        />
        <Box sx={{ flexGrow: 1 }} />
        <Button
          startIcon={<DownloadIcon />}
          onClick={runDownload}
          disabled={download.status === "running" || report.status !== "ready"}
        >
          {download.status === "running" ? "Exporting…" : "Export CSV"}
        </Button>
      </Stack>

      <Breadcrumbs sx={{ mb: 2 }}>
        <MuiLink
          component="button"
          type="button"
          onClick={() => setDrillKey(undefined)}
          underline={atEventLevel ? "hover" : "none"}
          color={atEventLevel ? "primary" : "text.primary"}
        >
          All {GROUP_LABELS[group].toLowerCase()}s
        </MuiLink>
        {atEventLevel && <Typography color="text.primary">{drillKey} — events</Typography>}
      </Breadcrumbs>

      {downloadError !== undefined && (
        <Alert severity="error" role="alert" sx={{ mb: 2 }} onClose={() => setDownloadError(undefined)}>
          {downloadError}
        </Alert>
      )}

      {report.status === "loading" && <LoadingState label="Loading report…" />}
      {report.status === "error" && (
        <ErrorState subject="attendance analytics" error={report.error} onRetry={report.reload} />
      )}

      {report.status === "ready" &&
        (rows.length === 0 ? (
          <EmptyState message="No attendance matches these filters." />
        ) : (
          <>
            <div style={{ height: 480, width: "100%" }}>
              <DataGrid
                rows={rows}
                columns={cols}
                getRowId={(r) => r.eventId ?? r.key}
                disableRowSelectionOnClick
                pageSizeOptions={[10, 25, 50]}
                initialState={{ pagination: { paginationModel: { pageSize: 25 } } }}
              />
            </div>
            {totals && (
              <Typography variant="body2" sx={{ mt: 1 }} color="text.secondary">
                <strong>Totals:</strong> {totals.totalEvents} event(s), {totals.people} student(s) — Present{" "}
                {totals.present}, Late {totals.late}, Absent {totals.absent}, Excused {totals.excused} of{" "}
                {totals.total} ({totals.attendanceRate.toFixed(1)}% present or late).
              </Typography>
            )}
          </>
        ))}
    </Box>
  );
}
