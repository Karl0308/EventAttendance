// "Pre-registration" on the event screen: which pre-registration sessions are linked to this event, and
// the controls to link, create and unlink them.
//
// ---------------------------------------------------------------------------------------------
// The thing this section must not let a reader believe
// ---------------------------------------------------------------------------------------------
//
// **That the expected number is still the audience.** Linking a session REPLACES the event's expected
// set with the pre-registered students — so the figure on the stat cards and the sections card can drop
// the moment a session is linked, and an operator who is not told why will read it as lost data. When
// `expectedSource` is `"PreRegistration"` this card says so in words (a chip, and a sentence naming the
// number), and shows the pre-registered personnel as an advisory line beside it, never inside it.
//
// ---------------------------------------------------------------------------------------------
// Three kinds of state, kept apart
// ---------------------------------------------------------------------------------------------
//
// - Server state: the audience read (a prop from the page — it also feeds the sections panels and the
//   stat cards) and the list of sessions on offer (`useApiResource`, here, because only this card needs it).
// - Write state: one `useApiMutation` for all three writes. One rather than three for the reason the
//   page's `detach` records: a single "something is running" answer, and a second failure cannot
//   overwrite the first's alert while the first is still being read.
// - UI state: the picked session id and the draft name — **ids, not objects**, so a pick that stops being
//   on offer (linked from another tab) drops out at render with no effect to synchronise it.
//
// The write endpoints answer with nothing this screen reads, so each settled write asks the page to
// re-read the audience, which is the one source of truth for what is linked.

import { useState } from "react";
import {
  Alert,
  AlertTitle,
  Autocomplete,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import HowToRegIcon from "@mui/icons-material/HowToReg";
import { api, describeApiError } from "../api";
import { advise } from "../apiGuidance";
import { audienceEditability, isAudienceLocked } from "../eventAudience";
import { knownStatus } from "../eventStatus";
import { useApiMutation } from "../useApiMutation";
import { useApiResource } from "../useApiResource";
import { EXPECTED_SOURCE } from "../types";
import type { AudienceRead } from "./EventAudiencePanel";
import type { EventPreRegistrationSession, PreRegistrationSession } from "../types";

interface EventPreRegistrationSectionProps {
  eventId: string;
  /** The event's status as the page read it — the gate. */
  eventStatus: string;
  read: AudienceRead;
  /** A write settled, successfully or not — re-read the audience (and whatever else shows `expected`). */
  onChanged: () => void;
  /** A confirmation, for the page's Snackbar. Failures are rendered inline instead. */
  onAnnounce: (text: string) => void;
}

/** What one write was about — also what titles its failure, since the mutation itself only knows "failed". */
type PreRegAction =
  | { kind: "link"; sessionId: string; name: string }
  | { kind: "unlink"; sessionId: string; name: string }
  | { kind: "create"; name: string };

const NO_SESSIONS: readonly PreRegistrationSession[] = [];
const PICKER_LABEL = "Pre-registration session";
const NAME_LABEL = "New session name (optional)";

const EXPECTED_FROM_PRE_REGISTRATION = "Expected from pre-registration";
const EXPECTED_FROM_AUDIENCE = "Expected from audience";

const NOTHING_LINKED =
  "No pre-registration session is linked, so the expected number comes from the audience sections and " +
  "definitions. Linking a session replaces it with the students pre-registered in that session.";

const NO_SESSIONS_LEFT =
  "No pre-registration sessions are left to link — they are all linked, or none exist yet.";

/** A server 409: on this surface it is the event having reached a terminal status since the page read it. */
const REFUSED_BY_STATUS =
  "This event may have been closed or cancelled after this screen loaded, in which case its " +
  "pre-registration can no longer be changed. Nothing was applied. Reload the page to see the status it " +
  "actually holds.";

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

const unlinkLabel = (name: string) => `Unlink pre-registration session ${name} from this event`;

/** The personnel advisory, in its own sentence: personnel are never part of the expected number. */
const advisoryText = (count: number) => `${count} personnel pre-registered — not counted in expected`;

function failureTitle(action: PreRegAction): string {
  switch (action.kind) {
    case "link":
      return `“${action.name}” was not linked`;
    case "unlink":
      return `“${action.name}” was not unlinked`;
    case "create":
      return "A session was not created from this event";
  }
}

export default function EventPreRegistrationSection({
  eventId,
  eventStatus,
  read,
  onChanged,
  onAnnounce,
}: EventPreRegistrationSectionProps) {
  const editable = audienceEditability(eventStatus);
  const audience = read.status === "ready" ? read.audience : undefined;
  const preRegistration = audience?.preRegistration ?? null;
  const linked: readonly EventPreRegistrationSession[] = preRegistration?.linkedSessions ?? [];

  // Nothing to offer on a terminal event, so nothing is fetched for it — the same "no read for a control
  // that cannot be used" the definitions section applies to an unclassified event.
  const sessions = useApiResource<readonly PreRegistrationSession[]>(
    () => (editable.can ? api.listPreRegistrationSessions() : Promise.resolve(NO_SESSIONS)),
    [editable.can],
  );

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draftName, setDraftName] = useState("");
  const [attempted, setAttempted] = useState<PreRegAction | undefined>(undefined);

  const write = useApiMutation((action: PreRegAction) => {
    switch (action.kind) {
      case "link":
        return api.linkPreRegistrationSession(eventId, action.sessionId);
      case "unlink":
        return api.unlinkPreRegistrationSession(eventId, action.sessionId);
      case "create":
        return api.createPreRegistrationFromEvent(eventId, action.name);
    }
  });

  const running = write.status === "running";
  const linkedIds = new Set(linked.map((s) => s.preRegistrationSessionId));
  const offered = (sessions.status === "ready" ? sessions.data : NO_SESSIONS).filter(
    (s) => !linkedIds.has(s.id),
  );
  const selected = offered.find((s) => s.id === selectedId) ?? null;

  const controlsDisabled = !editable.can || audience === undefined || running;

  const hint = editable.can
    ? undefined
    : knownStatus(eventStatus) === undefined
      ? editable.reason
      : `This event is ${eventStatus}, so pre-registration sessions can no longer be linked or unlinked.`;

  const perform = (action: PreRegAction, settledText: string, afterSuccess?: () => void) => {
    setAttempted(action);
    void write.run(action).then((settled) => {
      if (settled.outcome === "ignored") return;
      // Either way: a failed write may still have landed, and the re-read is how the card settles on
      // what is actually linked.
      onChanged();
      if (settled.outcome === "succeeded") {
        afterSuccess?.();
        onAnnounce(settledText);
      }
      // A failure renders inline below, beside the controls it is about.
    });
  };

  const link = () => {
    if (selected === null) return;
    perform(
      { kind: "link", sessionId: selected.id, name: selected.name },
      `“${selected.name}” is linked. This event now expects its pre-registered students.`,
      () => setSelectedId(null),
    );
  };

  const unlink = (session: EventPreRegistrationSession) =>
    perform(
      { kind: "unlink", sessionId: session.preRegistrationSessionId, name: session.name },
      `“${session.name}” is no longer linked to this event.`,
    );

  const create = () =>
    perform(
      { kind: "create", name: draftName },
      "A pre-registration session was created from this event.",
      () => setDraftName(""),
    );

  const fromPreRegistration = audience?.expectedSource === EXPECTED_SOURCE.PreRegistration;

  return (
    <Card sx={{ mb: 3 }}>
      <CardContent>
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap sx={{ mb: 1 }}>
          <HowToRegIcon color="primary" aria-hidden />
          <Typography variant="h6" component="h2">
            Pre-registration
          </Typography>
          {preRegistration !== null && (
            <Chip
              size="small"
              color="primary"
              label={`${preRegistration.totalPreRegisteredStudentCount} pre-registered`}
            />
          )}
          {audience !== undefined && (
            <Chip
              size="small"
              variant={fromPreRegistration ? "filled" : "outlined"}
              color={fromPreRegistration ? "info" : "default"}
              label={fromPreRegistration ? EXPECTED_FROM_PRE_REGISTRATION : EXPECTED_FROM_AUDIENCE}
            />
          )}
        </Stack>

        {read.status === "loading" && (
          <Box role="status" sx={{ display: "flex", alignItems: "center", gap: 1 }}>
            <CircularProgress size={16} aria-hidden />
            <Typography variant="body2" color="text.secondary">
              Loading pre-registration…
            </Typography>
          </Box>
        )}

        {/* Polite, not an alert: the sections card above already announces a failed audience read, and
            a second interruption for the same failed request adds noise rather than information. */}
        {read.status === "error" && (
          <Alert severity="warning" role="status">
            <AlertTitle>Pre-registration could not be loaded</AlertTitle>
            <Typography variant="body2">{describeApiError(read.error)}</Typography>
          </Alert>
        )}

        {audience !== undefined && (
          <>
            {fromPreRegistration ? (
              <Typography variant="body2" sx={{ mb: 1, maxWidth: "68ch" }}>
                {`This event expects ${plural(audience.expected, "student", "students")}: the ones pre-registered in the linked ${
                  linked.length === 1 ? "session" : "sessions"
                }. That replaces the audience sections and definitions, so the expected number can be lower than the audience alone would give. Unlink every session to go back to the audience.`}
              </Typography>
            ) : (
              linked.length === 0 && (
                <Typography variant="body2" color="text.secondary" sx={{ mb: 1, maxWidth: "68ch" }}>
                  {NOTHING_LINKED}
                </Typography>
              )
            )}

            {preRegistration !== null && (
              <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
                {advisoryText(preRegistration.totalAdvisoryPersonnelCount)}
              </Typography>
            )}

            {linked.length > 0 && (
              <Box sx={{ mb: 2 }}>
                <Typography variant="subtitle2" component="h3" gutterBottom>
                  Linked sessions ({linked.length})
                </Typography>
                <Stack component="ul" spacing={1} sx={{ listStyle: "none", p: 0, m: 0 }}>
                  {linked.map((session) => (
                    <Stack
                      key={session.preRegistrationSessionId}
                      component="li"
                      direction="row"
                      spacing={2}
                      alignItems="center"
                      justifyContent="space-between"
                      sx={{ border: 1, borderColor: "divider", borderRadius: 1, px: 2, py: 1 }}
                    >
                      <Box sx={{ minWidth: 0 }}>
                        <Typography sx={{ wordBreak: "break-word" }}>{session.name}</Typography>
                        <Typography variant="body2" color="text.secondary">
                          {plural(session.preRegisteredStudentCount, "student", "students")} ·{" "}
                          {session.advisoryPersonnelCount} personnel
                        </Typography>
                      </Box>
                      <Button
                        size="small"
                        color="error"
                        onClick={() => unlink(session)}
                        disabled={controlsDisabled}
                        aria-label={unlinkLabel(session.name)}
                        startIcon={
                          running &&
                          attempted?.kind === "unlink" &&
                          attempted.sessionId === session.preRegistrationSessionId ? (
                            <CircularProgress size={16} color="inherit" aria-hidden />
                          ) : undefined
                        }
                      >
                        Unlink
                      </Button>
                    </Stack>
                  ))}
                </Stack>
              </Box>
            )}
          </>
        )}

        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} alignItems="flex-start">
          <Autocomplete<PreRegistrationSession, false, false, false>
            fullWidth
            size="small"
            options={offered}
            value={selected}
            onChange={(_event, next) => {
              // A failure from the last attempt is about the last pick, not this one.
              write.reset();
              setSelectedId(next === null ? null : next.id);
            }}
            getOptionLabel={(session) => session.name}
            isOptionEqualToValue={(a, b) => a.id === b.id}
            disabled={controlsDisabled}
            loading={editable.can && sessions.status === "loading"}
            noOptionsText={
              sessions.status === "error" ? "The sessions could not be loaded." : NO_SESSIONS_LEFT
            }
            renderOption={(props, session) => {
              // `key` is spread by MUI inside `props`; pulled out so it is not spread into the JSX.
              const { key, ...rest } = props;
              return (
                <li key={key} {...rest}>
                  <Box>
                    <Typography>{session.name}</Typography>
                    <Typography variant="body2" color="text.secondary">
                      {session.audienceName} · {session.registeredCount} registered
                      {session.isClosed ? " · closed" : ""}
                    </Typography>
                  </Box>
                </li>
              );
            }}
            renderInput={(params) => (
              <TextField {...params} label={PICKER_LABEL} placeholder="Type to search" />
            )}
          />
          <Button
            variant="contained"
            onClick={link}
            disabled={controlsDisabled || selected === null}
            startIcon={
              running && attempted?.kind === "link" ? (
                <CircularProgress size={16} color="inherit" aria-hidden />
              ) : undefined
            }
            sx={{ whiteSpace: "nowrap", flexShrink: 0 }}
          >
            Link Pre-Registration
          </Button>
        </Stack>

        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} alignItems="flex-start" sx={{ mt: 2 }}>
          <TextField
            fullWidth
            size="small"
            label={NAME_LABEL}
            value={draftName}
            onChange={(e) => setDraftName(e.target.value)}
            disabled={controlsDisabled}
          />
          <Button
            variant="outlined"
            onClick={create}
            disabled={controlsDisabled}
            startIcon={
              running && attempted?.kind === "create" ? (
                <CircularProgress size={16} color="inherit" aria-hidden />
              ) : undefined
            }
            sx={{ whiteSpace: "nowrap", flexShrink: 0 }}
          >
            Create from event
          </Button>
        </Stack>

        {hint !== undefined && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1.5, maxWidth: "68ch" }}>
            {hint}
          </Typography>
        )}

        {editable.can && sessions.status === "error" && (
          <Alert
            severity="warning"
            role="status"
            sx={{ mt: 2 }}
            action={
              advise(sessions.error).retryable === "safe" ? (
                // Named for what it retries: this screen already carries other Retry buttons (the
                // roster, the attendance grid), and an unlabelled fourth is ambiguous to a screen reader.
                <Button
                  color="inherit"
                  size="small"
                  onClick={sessions.reload}
                  aria-label="Retry loading pre-registration sessions"
                >
                  Retry
                </Button>
              ) : undefined
            }
          >
            <AlertTitle>The pre-registration sessions could not be loaded</AlertTitle>
            <Typography variant="body2">{describeApiError(sessions.error)}</Typography>
          </Alert>
        )}

        {write.status === "failed" && attempted !== undefined && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            <AlertTitle>
              {isAudienceLocked(write.error) ? "This event’s pre-registration is now fixed" : failureTitle(attempted)}
            </AlertTitle>
            <Typography variant="body2">
              {isAudienceLocked(write.error)
                ? `${REFUSED_BY_STATUS} The server said: ${describeApiError(write.error)}`
                : describeApiError(write.error)}
            </Typography>
          </Alert>
        )}
      </CardContent>
    </Card>
  );
}
