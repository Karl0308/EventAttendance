// The Reports screen — Technical Plan §6.7, client QA Q15/Q16: a summary of one event and a summary
// of several, for administrators only.
//
// ---------------------------------------------------------------------------------------------
// ONE ROUTE, ALWAYS — WHY THIS PAGE NEVER CALLS THE SINGLE-EVENT ENDPOINT
// ---------------------------------------------------------------------------------------------
//
// The contract offers two reads: `GET /reports/event/{id}/summary` (one row) and
// `GET /reports/events/summary?eventId=…` (rows plus pooled totals). This page calls the second one
// unconditionally, for one selection or fifty, rather than switching endpoints on `selected.size`.
// `api.ts` does not even keep the single-event read wired up any more — see its own module note.
//
// The reason is the error contract, not convenience. The multi-event read is the only one of the two
// that carries `SelectionEmpty`/`SelectionTooLarge` (400) and `EventNotFound` with `missingEventIds`
// (404) — the single-event read just answers a bare 404. Branching on selection count would give this
// screen two different failure shapes to render depending on how many boxes were ticked, for the same
// underlying refusal ("that event doesn't exist"). One call site keeps one error path.
//
// The cost is a `totals` object the server computes even for a one-event selection, where it is
// arithmetically identical to that event's own row. `ReportResult` below simply does not render the
// totals row when there is only one — the extra computation is thrown away, never shown as a second
// opinion of a number already on screen.
//
// ---------------------------------------------------------------------------------------------
// A RESULT BELONGS TO THE SELECTION IT WAS RUN FOR, NOT THE ONE ON SCREEN NOW
// ---------------------------------------------------------------------------------------------
//
// `toggle` resets the mutation on every selection change — succeeded or failed, it does not matter —
// so a table or an error left over from a previous selection never sits on screen looking authoritative
// for a selection it no longer describes. `attempted` is the selection that was actually *sent*,
// snapshotted at the moment `Run` (or the 404 recovery below) fired: the failure alert's "Selected:"
// caption and its Retry both read `attempted`, never the live `selected` set, so that a caption naming
// what failed cannot silently start describing something else.

import { useState, type ReactNode } from "react";
import {
  Alert,
  Box,
  Button,
  Checkbox,
  FormControlLabel,
  Paper,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import AssessmentIcon from "@mui/icons-material/Assessment";
import { api, ApiError, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import { advise } from "../apiGuidance";
import { MAX_REPORT_EVENTS } from "../types";
import type { EventItem, MultiEventReport } from "../types";

const loadEvents = () => api.listEvents();

const NO_EVENTS = "No events have been created yet, so there is nothing to report on.";

const runReport = (eventIds: readonly string[]) => api.multiEventReportSummary(eventIds);

/**
 * Off the visible page but still reachable by a screen reader — the `<table>`'s `<caption>` and
 * nothing else on this screen needs it, so it is kept local rather than promoted to a shared style.
 */
const VISUALLY_HIDDEN = {
  position: "absolute",
  width: 1,
  height: 1,
  padding: 0,
  margin: -1,
  overflow: "hidden",
  clip: "rect(0, 0, 0, 0)",
  whiteSpace: "nowrap",
  border: 0,
} as const;

export default function Reports() {
  // No deps: the picker's own list runs once per mount and again only on Retry — same shape as
  // `Events.tsx`'s own read.
  const events = useApiResource(loadEvents, []);

  const [filter, setFilter] = useState("");
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [capNotice, setCapNotice] = useState(false);
  // What was actually sent on the last (or in-flight) run — see the file banner. Starts empty; it is
  // never read before a run has happened, because nothing renders `attempted` until `report.status`
  // is `failed`, and that cannot be true before `run` has been called at least once.
  const [attempted, setAttempted] = useState<readonly string[]>([]);

  // A read triggered by a button press rather than by mount or by a changed subject — exactly what
  // `useApiMutation` models (see its own note: "a write happens because someone pressed a button").
  // Nothing here is a write, but the shape — idle until asked, at most one in flight, an outcome to
  // render rather than an effect to schedule — is the same one, and reusing it avoids a second,
  // slightly different state machine for the one screen on this seam whose read is opt-in.
  const report = useApiMutation(runReport);

  const toggle = (eventId: string, checked: boolean) => {
    if (checked && selected.size >= MAX_REPORT_EVENTS) {
      setCapNotice(true);
      return;
    }
    setCapNotice(false);
    // The selection is changing, so whatever is on screen — a table or a failure — was computed for
    // the selection as it stood before this click and must not keep looking current. Not a `useEffect`
    // watching `selected`: this line runs in the same event handler that changes it, in the same commit
    // React batches for this click, rather than a render later.
    report.reset();
    setSelected((prev) => {
      const next = new Set(prev);
      if (checked) next.add(eventId);
      else next.delete(eventId);
      return next;
    });
  };

  const runFor = (ids: readonly string[]) => {
    setAttempted(ids);
    void report.run(ids);
  };

  const onRun = () => runFor(Array.from(selected));

  /**
   * The 404 recovery: drop the ids the server could not find, refresh the event list against which
   * they will show as gone, and run again with what is left — rather than making the operator start
   * the whole selection over.
   */
  const onRemoveMissing = (missingIds: readonly string[]) => {
    const next = new Set(selected);
    for (const id of missingIds) next.delete(id);
    setCapNotice(false);
    setSelected(next);
    events.reload();
    runFor(Array.from(next));
  };

  const rows = events.data ?? [];
  const visible = filter.trim()
    ? rows.filter((e) => e.name.toLowerCase().includes(filter.trim().toLowerCase()))
    : rows;

  return (
    <Box>
      <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
        <AssessmentIcon color="primary" />
        <Typography variant="h5" fontWeight={700}>
          Reports
        </Typography>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Pick one or more events for an attendance summary. With several selected, the totals row
        below the events is pooled across all of them by the server — not averaged, and not
        recomputed here.
      </Typography>

      {events.status === "loading" && <LoadingState label="Loading events…" />}

      {events.status === "error" && (
        <ErrorState subject="events" error={events.error} onRetry={events.reload} />
      )}

      {events.status === "ready" && rows.length === 0 && <EmptyState message={NO_EVENTS} />}

      {events.status === "ready" && rows.length > 0 && (
        <Stack spacing={2}>
          <TextField
            label="Filter events by name"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            size="small"
            sx={{ maxWidth: 360 }}
          />

          {/* Above the list, deliberately: with a few hundred events a keyboard user tabbing from
              the filter field to Run must not cross every checkbox first. The filter narrows which
              rows are OFFERED for selection; it never narrows the selection already made — see
              `visible` below, which is used only for what renders, never for what counts or what a
              run sends. */}
          <Stack
            direction="row"
            justifyContent="space-between"
            alignItems="center"
            flexWrap="wrap"
            spacing={1}
          >
            <Typography variant="subtitle2">
              {selected.size} of {MAX_REPORT_EVENTS} selected
            </Typography>
            <Button
              variant="contained"
              disabled={selected.size === 0 || report.status === "running"}
              onClick={onRun}
            >
              Run report
            </Button>
          </Stack>

          {/* Announced rather than merely painted: it is telling the operator why the checkbox they
              just clicked did not check. */}
          {capNotice && (
            <Alert severity="warning" role="status">
              At most {MAX_REPORT_EVENTS} events can be reported on at once. Unselect one before
              adding another.
            </Alert>
          )}

          <Paper
            variant="outlined"
            sx={{ maxHeight: 320, overflow: "auto", p: 1, minWidth: 0 }}
            component="fieldset"
          >
            <Typography component="legend" variant="subtitle2" sx={{ px: 1 }}>
              Events
            </Typography>
            {visible.length === 0 ? (
              <Typography variant="body2" color="text.secondary" sx={{ px: 1, py: 2 }}>
                No event name matches “{filter}”.
              </Typography>
            ) : (
              visible.map((e) => (
                <Box key={e.id} sx={{ px: 1 }}>
                  <FormControlLabel
                    control={
                      <Checkbox
                        checked={selected.has(e.id)}
                        onChange={(_e, checked) => toggle(e.id, checked)}
                      />
                    }
                    label={eventLabel(e)}
                  />
                </Box>
              ))
            )}
          </Paper>

          {report.status === "running" && <LoadingState label="Running the report…" />}

          {report.status === "failed" && (
            <ReportError
              error={report.error}
              attempted={attempted}
              rows={rows}
              onRetry={() => runFor(attempted)}
              onRemoveMissing={onRemoveMissing}
            />
          )}

          {report.status === "succeeded" && <ReportResult report={report.data} />}
        </Stack>
      )}
    </Box>
  );
}

const eventLabel = (e: EventItem) => `${e.name} — ${new Date(e.startAt).toLocaleString()} (${e.status})`;

/**
 * The refusal, worded for what actually happened rather than generic advice.
 *
 * A 404 `EventNotFound` names the events that were not found — by **name**, resolved against the
 * already-loaded event list, falling back to the raw id only for one this client never had a name
 * for (deleted before this page ever loaded it) — and offers a way out of it: drop them from the
 * selection and run again, rather than leaving reloading-and-restarting the only escape.
 *
 * Every other refusal — 400 `SelectionTooLarge`/`SelectionEmpty`, 403, a network failure — falls
 * through to the server's own `detail`/`title` via `describeApiError`, exactly as `ErrorState`
 * renders every other failure on this seam. `advise()` alone decides whether Retry is offered: a 403
 * answers `retryable: false` (the account lacks the permission; pressing the same button again
 * cannot change that), so no Retry is rendered for it, and a network failure or a 5xx does get one.
 */
function ReportError({
  error,
  attempted,
  rows,
  onRetry,
  onRemoveMissing,
}: {
  error: unknown;
  attempted: readonly string[];
  rows: readonly EventItem[];
  onRetry: () => void;
  onRemoveMissing: (missingIds: readonly string[]) => void;
}) {
  const missing =
    error instanceof ApiError && error.status === 404 ? error.problem?.missingEventIds : undefined;

  if (missing !== undefined && missing.length > 0) {
    const names = missing.map((id) => rows.find((e) => e.id === id)?.name ?? id);
    return (
      <Alert
        severity="error"
        role="alert"
        action={
          <Button color="inherit" size="small" onClick={() => onRemoveMissing(missing)}>
            Remove the missing events and run again
          </Button>
        }
      >
        <Typography variant="body2">
          {missing.length === 1 ? "One selected event" : `${missing.length} of the selected events`}{" "}
          could not be found — it may have been deleted since this list was loaded:{" "}
          {names.join(", ")}.
        </Typography>
      </Alert>
    );
  }

  const { message, retryable } = advise(error);
  return (
    <Alert
      severity="error"
      role="alert"
      action={
        retryable === "safe" ? (
          <Button color="inherit" size="small" onClick={onRetry}>
            Retry
          </Button>
        ) : undefined
      }
    >
      <Typography variant="body2">{describeApiError(error)}</Typography>
      <Typography variant="body2" sx={{ mt: 1 }}>
        {message}
      </Typography>
      {/* Kept out of the rendered sentence above and only here, so the ids never appear inside prose
          that might get re-worded independently of them. Reads `attempted`, the selection this
          failure is actually about — never the live selection, which may since have moved on. */}
      <Typography variant="caption" color="text.secondary" component="p" sx={{ mt: 1 }}>
        Selected: {attempted.join(", ")}
      </Typography>
    </Alert>
  );
}

/**
 * The report itself: one row per event, and — only past one event — the server's pooled totals.
 *
 * Rates are rendered exactly as the server sent them (`.toFixed(1)` on a number the server already
 * rounded to one decimal place, never a client-side division): `EventReportTotalsDto.attendanceRate`
 * is pooled, not averaged, and is not reproducible by re-deriving it from the rows a second way.
 */
function ReportResult({ report }: { report: MultiEventReport }) {
  return (
    <Paper variant="outlined" sx={{ overflow: "auto" }}>
      <Box component="table" sx={{ width: "100%", borderCollapse: "collapse" }}>
        <Box component="caption" sx={VISUALLY_HIDDEN}>
          Attendance summary for{" "}
          {report.events.length === 1
            ? "the selected event"
            : `the ${report.events.length} selected events`}
        </Box>
        <Box component="thead">
          <Box component="tr">
            {HEADERS.map((h) => (
              <Box
                component="th"
                key={h}
                scope="col"
                sx={{ textAlign: "left", p: 1, borderBottom: "1px solid", borderColor: "divider" }}
              >
                {h}
              </Box>
            ))}
          </Box>
        </Box>
        <Box component="tbody">
          {report.events.map((row) => (
            <Box component="tr" key={row.eventId}>
              <Cell>{row.eventName}</Cell>
              <Cell>{new Date(row.startAt).toLocaleString()}</Cell>
              <Cell>{row.status}</Cell>
              <Cell align="right">{row.expected}</Cell>
              <Cell align="right">{row.attended}</Cell>
              <Cell align="right">{row.present}</Cell>
              <Cell align="right">{row.late}</Cell>
              <Cell align="right">{row.absent}</Cell>
              <Cell align="right">{row.excused}</Cell>
              <Cell align="right">{row.unexpected}</Cell>
              <Cell align="right">{row.attendanceRate.toFixed(1)}%</Cell>
            </Box>
          ))}
          {/* Only past one event: for a single selection the totals are the row above, arithmetically
              — showing them again would be a second, redundant opinion of the same number rather
              than new information. See the module note on why this page always fetches them anyway. */}
          {report.events.length > 1 && (
            <Box
              component="tr"
              sx={{ fontWeight: 700, borderTop: "2px solid", borderColor: "divider" }}
            >
              <Cell header colSpan={3}>
                Combined across {report.totals.eventCount} selected events
              </Cell>
              <Cell align="right">{report.totals.expected}</Cell>
              <Cell align="right">{report.totals.attended}</Cell>
              <Cell align="right">{report.totals.present}</Cell>
              <Cell align="right">{report.totals.late}</Cell>
              <Cell align="right">{report.totals.absent}</Cell>
              <Cell align="right">{report.totals.excused}</Cell>
              <Cell align="right">{report.totals.unexpected}</Cell>
              <Cell align="right">{report.totals.attendanceRate.toFixed(1)}%</Cell>
            </Box>
          )}
        </Box>
      </Box>
    </Paper>
  );
}

const HEADERS = [
  "Event",
  "Starts",
  "Status",
  "Expected",
  "Attended",
  "Present",
  "Late",
  "Absent",
  "Excused",
  "Unexpected",
  "Rate",
];

/**
 * One cell. `header` renders a `<th scope="row">` — used once, for the "Combined across…" label —
 * rather than a `<td>`: it is the row's heading the same way each column's own `<th scope="col">` is
 * that column's, and a screen reader stepping through the totals row announces it as such.
 */
function Cell({
  children,
  align,
  colSpan,
  header,
}: {
  children: ReactNode;
  align?: "left" | "right";
  colSpan?: number;
  header?: boolean;
}) {
  return (
    <Box
      component={header ? "th" : "td"}
      scope={header ? "row" : undefined}
      colSpan={colSpan}
      sx={{
        p: 1,
        textAlign: align ?? "left",
        borderBottom: "1px solid",
        borderColor: "divider",
        whiteSpace: "nowrap",
      }}
    >
      {children}
    </Box>
  );
}
