import { useState } from "react";
import { useParams, Link } from "react-router-dom";
import {
  Box,
  Typography,
  Card,
  CardContent,
  Chip,
  Button,
  CircularProgress,
  Stack,
  MenuItem,
  TextField,
  Snackbar,
  Alert,
  AlertTitle,
  Breadcrumbs,
  Link as MuiLink,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import Grid from "@mui/material/Grid2";
import SensorsIcon from "@mui/icons-material/Sensors";
import { api, describeApiError } from "../api";
import { advise } from "../apiGuidance";
import { useApiResource } from "../useApiResource";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import type { AttendanceRecord } from "../types";

// A deep link to a deleted or mistyped event is an answer, not a failure: `api.eventDetail` maps that
// 404 to `event: undefined`, and this says so plainly instead of leaving the screen loading.
const NO_SUCH_EVENT = "No event with this id. It may have been deleted, or the link may be wrong.";

// Drives a decision — whether taps can still be recorded — rather than picking a colour.
const EVENT_STATUS_OPEN = "Open";

// The two things an empty picker can mean, kept apart in words as well as in the type. Everyone
// having tapped in is good news; a roster that would not load is not, and must never be read as it.
const EVERYONE_TAPPED_IN = "Everyone has tapped in 🎉";
const ROSTER_UNAVAILABLE =
  "The student roster did not load, so there is nobody to pick from. This is not the same as everyone " +
  "having tapped in — attendance below is unaffected.";

// What is left to do when the failure is one Retry cannot clear, so no button is offered. A warning
// with no action reads as a dead end unless it says where the action actually is.
//
// It says what this screen *is* rather than what the reader is doing. The tempting sentence — "real
// taps still record normally" — is one this bundle has no evidence for: it never calls the capture
// endpoint, and the reader is a separately-versioned client it cannot see. Worse, one of the two kinds
// that reach this copy is `malformed`, which means the admin build and the API disagree on shapes —
// precisely when the capture contract may have moved too. An admin who reads "capture is fine" during
// a version skew stops escalating an outage. Topology is safe to assert because it is true by
// construction: this picker is a simulation aid, and real taps go to `POST /attendance/tap` with a
// DeviceKey this SPA deliberately does not hold.
const ROSTER_UNRECOVERABLE =
  "Real card taps do not go through this screen — only the simulator is affected. Nothing here will " +
  "bring the picker back; report this to whoever maintains EAMS.";

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

/**
 * The picker's input list did not load. Polite rather than `role="alert"`: the event, its summary and
 * its attendance all loaded and only the picker is missing, so interrupting a user who is reading a
 * healthy screen overstates what went wrong.
 *
 * Retry is offered on exactly the terms `ErrorState` offers it, decided by the same `advise()`, since
 * it is the same question. It used to be offered unconditionally here, which put a button on
 * `too-large` that provably could not succeed: `MAX_LIST_ROWS` is a client-side ceiling, so the row
 * count that refused the read is the row count the retry reads again. Where Retry *can* help it is
 * still worth pressing even though it re-reads the whole screen — a picker dead until a full page
 * refresh is a dead end with nothing to press.
 */
function RosterUnavailable({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const { message, retryable } = advise(error);
  // `=== "safe"` rather than truthiness, for the reason `ErrorState` records: `"may-duplicate"` is
  // truthy and must never be offered a one-click retry. The roster is a list read, so it cannot
  // produce that arm and this renders exactly as it did; the strict test is what keeps that true.
  const offerRetry = retryable === "safe";
  return (
    <Alert
      severity="warning"
      role="status"
      action={
        offerRetry ? (
          <Button color="inherit" size="small" onClick={onRetry}>
            Retry
          </Button>
        ) : undefined
      }
    >
      <AlertTitle>Simulate RFID tap is unavailable</AlertTitle>
      <Typography variant="body2">{ROSTER_UNAVAILABLE}</Typography>
      <Typography variant="body2" sx={{ mt: 1 }}>
        {describeApiError(error)}
      </Typography>
      <Typography variant="body2" sx={{ mt: 1 }}>
        {offerRetry ? message : `${message} ${ROSTER_UNRECOVERABLE}`}
      </Typography>
    </Alert>
  );
}

const statusChipColor = (s: string) =>
  s === "Present" ? "success" : s === "Late" ? "warning" : s === "Excused" ? "info" : "error";

export default function EventDetail() {
  const { id = "" } = useParams();

  // One read for the whole screen, parameterised by the route id: the load is an inline arrow and
  // `[id]` is what re-runs it. Its failure becomes the rendered error state below. This used to be a
  // bare `refresh()` inside an effect with no rejection handler, so an unreachable API left the page
  // saying "Loading…" for as long as anyone was willing to wait for it.
  const detail = useApiResource(() => api.eventDetail(id), [id]);

  const [pickUid, setPickUid] = useState("");
  const [toast, setToast] = useState<{ msg: string; ok: boolean } | null>(null);

  // Defined only once the read has settled, which is what the render branches on below.
  const data = detail.status === "ready" ? detail.data : undefined;
  // An empty list here means "the roster loaded and nobody is left"; `roster.status` carries the other
  // reading, and the picker below renders the two differently.
  const cards = data?.roster.status === "ready" ? data.roster.cards : [];

  // Derived at render rather than synced into state by an effect: the user's choice stands for as
  // long as that card is still on offer, and otherwise falls back to the first one a reload left.
  const pick = cards.some((c) => c.uid === pickUid) ? pickUid : cards[0]?.uid ?? "";

  // `onClick` drops the promise this returns, so nothing outside can catch it: whatever it does with a
  // failure, it has to do here. The try/catch is that guarantee held in this file. It used to be held
  // by `api.tap` happening to resolve its refusal rather than throw — correctness parked in another
  // module's implementation, where wiring tap to a real call would have quietly removed it and left a
  // network failure showing the user nothing at all.
  const simulateTap = async () => {
    if (!pick) return;
    try {
      const res = await api.tap(id, pick);
      setToast({ msg: res.message, ok: res.ok });
    } catch (cause) {
      setToast({ msg: describeApiError(cause), ok: false });
    }
    // Re-read either way. A tap that failed on the way back may still have been recorded, so the
    // screen should not be left asserting the state from before it. Since `reload` now keeps the
    // rendered data in place while it runs, this costs a refresh rather than the whole screen.
    detail.reload();
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
      renderCell: (p) => <Chip size="small" label={p.value} color={statusChipColor(p.value)} />,
    },
    { field: "captureMethod", headerName: "Method", width: 100 },
  ];

  return (
    <Box>
      {detail.status === "loading" && <LoadingState label="Loading the event…" />}

      {/* Nothing below renders on failure: a header above four zeroed stat cards reads as a quiet
          event rather than a broken one, and that is the reading a user is most likely to believe. */}
      {detail.status === "error" && (
        <ErrorState subject="this event" error={detail.error} onRetry={detail.reload} />
      )}

      {data !== undefined &&
        (data.event === undefined ? (
          <EmptyState message={NO_SUCH_EVENT} />
        ) : (
          <>
            <Breadcrumbs sx={{ mb: 1 }}>
              <MuiLink component={Link} to="/events" underline="hover">
                Events
              </MuiLink>
              <Typography color="text.primary">{data.event.name}</Typography>
            </Breadcrumbs>

            <Stack
              direction="row"
              justifyContent="space-between"
              alignItems="center"
              sx={{ mb: 2 }}
            >
              <Box>
                <Typography variant="h5" fontWeight={700}>
                  {data.event.name}
                </Typography>
                <Typography color="text.secondary">
                  {data.event.location} · {new Date(data.event.startAt).toLocaleString()} · grace{" "}
                  {data.event.graceMinutes}m
                </Typography>
              </Box>
              <Chip
                label={data.event.status}
                color={data.event.status === EVENT_STATUS_OPEN ? "success" : "default"}
              />
            </Stack>

            <Grid container spacing={2} sx={{ mb: 3 }}>
              <Grid size={{ xs: 6, md: 3 }}>
                <StatCard label="Present" value={data.summary?.present ?? 0} color="#2e7d32" />
              </Grid>
              <Grid size={{ xs: 6, md: 3 }}>
                <StatCard label="Late" value={data.summary?.late ?? 0} color="#ed6c02" />
              </Grid>
              <Grid size={{ xs: 6, md: 3 }}>
                <StatCard label="Excused" value={data.summary?.excused ?? 0} color="#0288d1" />
              </Grid>
              <Grid size={{ xs: 6, md: 3 }}>
                <StatCard
                  label="Attendance Rate"
                  value={data.summary?.attendanceRate ?? 0}
                  color="#8B1A1A"
                />
              </Grid>
            </Grid>

            {data.event.status === EVENT_STATUS_OPEN && (
              <Card sx={{ mb: 3, bgcolor: "#fff8e1" }}>
                <CardContent>
                  {data.roster.status === "unavailable" ? (
                    <RosterUnavailable error={data.roster.error} onRetry={detail.reload} />
                  ) : (
                    <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap">
                      <SensorsIcon color="primary" />
                      <Typography fontWeight={600}>Simulate RFID tap</Typography>
                      <TextField
                        select
                        size="small"
                        label="Card / student"
                        value={pick}
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
                      <Button
                        variant="contained"
                        onClick={simulateTap}
                        disabled={cards.length === 0}
                      >
                        Tap
                      </Button>
                      {cards.length === 0 && (
                        <Typography color="text.secondary">{EVERYONE_TAPPED_IN}</Typography>
                      )}
                    </Stack>
                  )}
                </CardContent>
              </Card>
            )}

            <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 1 }}>
              <Typography variant="h6">Live attendance ({data.records.length})</Typography>
              {/* Mounted whether or not a re-read is running, so the live region exists in the DOM
                  before anything is put into it — a region inserted and populated in the same commit
                  is announced unreliably. Fixed height so the grid below does not shift when the
                  message appears. */}
              <Box
                role="status"
                sx={{ display: "flex", alignItems: "center", gap: 1, minHeight: 24 }}
              >
                {detail.refreshing && (
                  <>
                    <CircularProgress size={16} aria-hidden />
                    <Typography variant="body2" color="text.secondary">
                      Refreshing…
                    </Typography>
                  </>
                )}
              </Box>
            </Stack>
            <div style={{ height: 420, width: "100%" }}>
              <DataGrid
                rows={data.records}
                columns={cols}
                getRowId={(r) => r.id}
                disableRowSelectionOnClick
                pageSizeOptions={[10, 25]}
                initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
              />
            </div>
          </>
        ))}

      {/* Outside the branches on purpose: a re-read after a tap keeps the ready branch mounted, but a
          re-read that *fails* replaces it with the error state, and a Snackbar living inside that
          branch would take the message it was just given down with it — at exactly the moment the
          message is a failure the user needs to read. */}
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
