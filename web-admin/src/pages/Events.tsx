import { useLayoutEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Box,
  Button,
  Typography,
  Card,
  CardContent,
  Chip,
  Snackbar,
  Alert,
  Stack,
} from "@mui/material";
import AddIcon from "@mui/icons-material/Add";
import LocationOnIcon from "@mui/icons-material/LocationOn";
import ScheduleIcon from "@mui/icons-material/Schedule";
import { api, describeApiError } from "../api";
import type { EventWriteRequest } from "../types";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import NewEventDialog from "../components/NewEventDialog";

const loadEvents = () => api.listEvents();

const NO_EVENTS = "No events have been created yet.";

/** What the Snackbar is currently saying, and how loudly. */
interface Notice {
  severity: "success" | "error";
  text: string;
}

/** How long the "created" confirmation stays up. Long enough to be read, not long enough to nag. */
const CREATED_NOTICE_MS = 6000;

/**
 * A failure does not time out. The success notice is a courtesy — the new row is on the list either
 * way — but the failure below is only ever shown when the dialog that would have carried it is gone,
 * so it is the only copy the user gets. MUI reads `null` as "stay until dismissed"; the Alert has its
 * own close button.
 */
const NO_AUTO_HIDE = null;

const statusColor = (s: string) =>
  s === "Open" ? "success" : s === "Closed" ? "default" : s === "Cancelled" ? "error" : "warning";

export default function Events() {
  // No deps: the read takes nothing, so it runs once per mount and again only on Retry.
  const events = useApiResource(loadEvents, []);
  const nav = useNavigate();

  /**
   * The create lives here, not inside `NewEventDialog`, and that is the whole point of the shape.
   *
   * When the hook was in the dialog, the dialog's own unmount ended the write: Cancel, Escape and the
   * backdrop had to be blocked for as long as the request ran, or the outcome went to `console.debug`
   * and this list was never re-read. Owning it a level up means the dialog can be dismissed at any
   * moment and there is still a mounted component to say what happened.
   *
   * Not a complete cure, and worth being precise about: navigating away from `/events` unmounts this
   * page too, and that outcome is still lost. Closing that would take state above the router, which
   * is a design decision rather than a fix — flagged, not smuggled in.
   */
  const create = useApiMutation((request: EventWriteRequest) => api.createEvent(request));

  // Mounting the dialog only while it is open is what keeps the form honest: every open starts from
  // an empty draft with no leftover text. The cost is MUI's closing fade, which an unmount skips — a
  // fair trade against a dialog that reopens still showing what someone typed and abandoned.
  const [creating, setCreating] = useState(false);

  /**
   * Whether the dialog is on screen, read at the moment a write *settles* rather than from the
   * closure that started it. `creating` captured there is a render old, and the interesting case is
   * exactly the one where it changed in between — the user dismissed the dialog while the create was
   * in flight, so there is no longer an alert for the failure to appear in.
   */
  const dialogOpen = useRef(creating);
  useLayoutEffect(() => {
    dialogOpen.current = creating;
  });

  // Two pieces rather than one `Notice | undefined` driving both. MUI keeps a Snackbar's children
  // mounted through its closing fade, so clearing the notice to close it would blank the text mid-fade
  // and leave an empty bar on screen for the length of the transition. The text outlives the notice it
  // was for, which costs one stale object and no flicker.
  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);

  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const openDialog = () => {
    // A failure from an attempt the user walked away from is still in the mutation, and without this
    // it would greet them as though it were about the draft they have not typed yet. Refused while a
    // write is in flight — see `useApiMutation.reset` — in which case the reopened form correctly
    // shows that one still running.
    create.reset();
    setCreating(true);
  };

  const submitDraft = (request: EventWriteRequest) => {
    // `run` never rejects; it answers with an outcome. The floating promise is deliberate and marked.
    void create.run(request).then((settled) => {
      // `ignored` — a create was already in flight and this submit sent nothing. Nothing settled, so
      // there is nothing to report and no reason to re-read: the first one is still coming.
      if (settled.outcome === "ignored") return;

      // Either way, and the failure case is the one that matters: a write that failed on the way back
      // may still have been carried out, and a list that goes on asserting the state from before it
      // is the one thing that would have someone create the event a second time. `reload` keeps the
      // rows already rendered and raises `refreshing` rather than dropping back to `loading`, so the
      // page does not blank behind the dialog or the notice.
      events.reload();

      if (settled.outcome === "succeeded") {
        setCreating(false);
        announce({ severity: "success", text: `Created “${settled.data.name}” as a Draft.` });
        return;
      }

      // The dialog renders the failure in context when it is still open — beside the draft that
      // caused it, with the heading derived from what the server may have done. Announcing it here as
      // well would double it, and a Snackbar sits above the modal. When the dialog is gone, this is
      // the only place left for it.
      if (!dialogOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Events
        </Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={openDialog}>
          New event
        </Button>
      </Stack>

      {events.status === "loading" && <LoadingState label="Loading events…" />}

      {events.status === "error" && (
        <ErrorState subject="events" error={events.error} onRetry={events.reload} />
      )}

      {events.status === "ready" &&
        (events.data.length === 0 ? (
          <EmptyState message={NO_EVENTS} />
        ) : (
          <Stack spacing={2}>
            {events.data.map((e) => (
              <Card
                key={e.id}
                sx={{ cursor: "pointer", "&:hover": { boxShadow: 4 } }}
                onClick={() => nav(`/events/${e.id}`)}
              >
                <CardContent>
                  <Stack direction="row" justifyContent="space-between" alignItems="flex-start">
                    <Box>
                      <Typography variant="h6">{e.name}</Typography>
                      <Stack direction="row" spacing={2} sx={{ mt: 1, color: "text.secondary" }}>
                        <Stack direction="row" spacing={0.5} alignItems="center">
                          <LocationOnIcon fontSize="small" />
                          <Typography variant="body2">{e.location ?? "—"}</Typography>
                        </Stack>
                        <Stack direction="row" spacing={0.5} alignItems="center">
                          <ScheduleIcon fontSize="small" />
                          <Typography variant="body2">
                            {new Date(e.startAt).toLocaleString()}
                          </Typography>
                        </Stack>
                      </Stack>
                    </Box>
                    <Stack spacing={1} alignItems="flex-end">
                      <Chip size="small" label={e.status} color={statusColor(e.status)} />
                      <Chip size="small" variant="outlined" label={e.attendanceMode} />
                    </Stack>
                  </Stack>
                </CardContent>
              </Card>
            ))}
          </Stack>
        ))}

      {creating && (
        // Closed on demand and on success only. A failure leaves it open so its message can be read
        // beside the draft that caused it and the draft corrected.
        <NewEventDialog
          onClose={() => setCreating(false)}
          onSubmit={submitDraft}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {/* The dialog closing and a row appearing somewhere in a list sorted by start date is not, on
          its own, an announcement — the new event may well land below the fold. */}
      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : CREATED_NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {/* Rendered from the notice rather than defaulted from it: a fallback severity would paint a
            failure green for one render if the two ever came apart. `status` for the success —
            something the user caused by pressing Create is not an interruption, and `assertive` would
            cut across whatever they moved on to — and `alert` for the failure, which is the one case
            where interrupting is the point, because the dialog that would have shown it is gone. */}
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
