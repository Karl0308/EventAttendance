import { useCallback, useEffect, useState } from "react";
import { useParams, Link } from "react-router-dom";
import {
  Box,
  Typography,
  Card,
  CardContent,
  Chip,
  Button,
  Stack,
  MenuItem,
  TextField,
  Snackbar,
  Alert,
  Breadcrumbs,
  Link as MuiLink,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import Grid from "@mui/material/Grid2";
import SensorsIcon from "@mui/icons-material/Sensors";
import { api } from "../api";
import type { AttendanceRecord, EventItem, EventSummary } from "../types";

function StatCard({ label, value, color }: { label: string; value: number; color: string }) {
  return (
    <Card sx={{ borderTop: `4px solid ${color}` }}>
      <CardContent sx={{ py: 2 }}>
        <Typography variant="h4" fontWeight={700}>
          {value}
        </Typography>
        <Typography color="text.secondary">{label}</Typography>
      </CardContent>
    </Card>
  );
}

const statusChipColor = (s: string) =>
  s === "Present" ? "success" : s === "Late" ? "warning" : s === "Excused" ? "info" : "error";

export default function EventDetail() {
  const { id = "" } = useParams();
  const [ev, setEv] = useState<EventItem>();
  const [summary, setSummary] = useState<EventSummary>();
  const [records, setRecords] = useState<AttendanceRecord[]>([]);
  const [cards, setCards] = useState<{ uid: string; name: string }[]>([]);
  const [pickUid, setPickUid] = useState("");
  const [toast, setToast] = useState<{ msg: string; ok: boolean } | null>(null);

  const refresh = useCallback(async () => {
    const [e, s, r, c] = await Promise.all([
      api.getEvent(id),
      api.eventSummary(id),
      api.listAttendance(id),
      api.untappedCards(id),
    ]);
    setEv(e);
    setSummary(s);
    setRecords(r);
    setCards(c);
    setPickUid((prev) => (c.some((x) => x.uid === prev) ? prev : c[0]?.uid ?? ""));
  }, [id]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  const simulateTap = async () => {
    if (!pickUid) return;
    const res = await api.tap(id, pickUid);
    setToast({ msg: res.message, ok: res.ok });
    await refresh();
  };

  const cols: GridColDef<AttendanceRecord>[] = [
    { field: "studentNumber", headerName: "Student No.", width: 130 },
    { field: "studentName", headerName: "Name", flex: 1, minWidth: 180 },
    {
      field: "checkInAt",
      headerName: "Check-in",
      width: 110,
      valueFormatter: (v) => (v ? new Date(v as string).toLocaleTimeString() : "—"),
    },
    {
      field: "status",
      headerName: "Status",
      width: 120,
      renderCell: (p) => (
        <Chip size="small" label={p.value} color={statusChipColor(p.value) as never} />
      ),
    },
    { field: "captureMethod", headerName: "Method", width: 100 },
  ];

  if (!ev) return <Typography>Loading…</Typography>;

  return (
    <Box>
      <Breadcrumbs sx={{ mb: 1 }}>
        <MuiLink component={Link} to="/events" underline="hover">
          Events
        </MuiLink>
        <Typography color="text.primary">{ev.name}</Typography>
      </Breadcrumbs>

      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 2 }}>
        <Box>
          <Typography variant="h5" fontWeight={700}>
            {ev.name}
          </Typography>
          <Typography color="text.secondary">
            {ev.location} · {new Date(ev.startAt).toLocaleString()} · grace {ev.graceMinutes}m
          </Typography>
        </Box>
        <Chip label={ev.status} color={ev.status === "Open" ? "success" : "default"} />
      </Stack>

      <Grid container spacing={2} sx={{ mb: 3 }}>
        <Grid size={{ xs: 6, md: 3 }}>
          <StatCard label="Present" value={summary?.present ?? 0} color="#2e7d32" />
        </Grid>
        <Grid size={{ xs: 6, md: 3 }}>
          <StatCard label="Late" value={summary?.late ?? 0} color="#ed6c02" />
        </Grid>
        <Grid size={{ xs: 6, md: 3 }}>
          <StatCard label="Excused" value={summary?.excused ?? 0} color="#0288d1" />
        </Grid>
        <Grid size={{ xs: 6, md: 3 }}>
          <StatCard label="Attendance Rate" value={summary?.attendanceRate ?? 0} color="#8B1A1A" />
        </Grid>
      </Grid>

      {ev.status === "Open" && (
        <Card sx={{ mb: 3, bgcolor: "#fff8e1" }}>
          <CardContent>
            <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap">
              <SensorsIcon color="primary" />
              <Typography fontWeight={600}>Simulate RFID tap</Typography>
              <TextField
                select
                size="small"
                label="Card / student"
                value={pickUid}
                onChange={(e) => setPickUid(e.target.value)}
                sx={{ minWidth: 280 }}
                disabled={cards.length === 0}
              >
                {cards.map((c) => (
                  <MenuItem key={c.uid} value={c.uid}>
                    {c.uid} — {c.name}
                  </MenuItem>
                ))}
              </TextField>
              <Button variant="contained" onClick={simulateTap} disabled={cards.length === 0}>
                Tap
              </Button>
              {cards.length === 0 && (
                <Typography color="text.secondary">Everyone has tapped in 🎉</Typography>
              )}
            </Stack>
          </CardContent>
        </Card>
      )}

      <Typography variant="h6" gutterBottom>
        Live attendance ({records.length})
      </Typography>
      <div style={{ height: 420, width: "100%" }}>
        <DataGrid
          rows={records}
          columns={cols}
          getRowId={(r) => r.id}
          disableRowSelectionOnClick
          pageSizeOptions={[10, 25]}
          initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
        />
      </div>

      <Snackbar
        open={!!toast}
        autoHideDuration={2500}
        onClose={() => setToast(null)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {toast ? (
          <Alert severity={toast.ok ? "success" : "error"} onClose={() => setToast(null)}>
            {toast.msg}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Box>
  );
}
