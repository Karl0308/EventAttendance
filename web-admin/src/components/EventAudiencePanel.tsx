// Who this event expects — the sections that were invited, the students invited individually, and the
// controls that change either.
//
// ---------------------------------------------------------------------------------------------
// The two things this panel must not let a reader believe
// ---------------------------------------------------------------------------------------------
//
// 1. **That an empty list means nobody was attached, on a frozen event.** ADR-003 D-13: reaching a
//    terminal status resolves the live audience once and writes it down as individual `EventGroups`
//    student rows, and those rows *are* the event's denominator. `GET /events/{id}/attendees` does not
//    republish them — the per-student frozen set is `GET /events/{id}/roster`. So a closed event shows
//    its sections and an empty student list beside a non-zero expected count, and without a sentence
//    saying which of the two is the whole story the page contradicts itself.
//
// 2. **That the controls are missing because of a permission.** A terminal event's audience is fixed
//    and cannot be reopened, which is a fact about the event rather than about the user. The reason is
//    printed rather than implied, which is this codebase's standing rule for a control that greys out.
//
// The writes are the page's, as everywhere in this slice. They are grouped into `attach` and `detach`
// props for the reason `StudentCardsDialog` records: a flat prop list of six booleans and four
// callbacks is a signature nobody can read at the call site.

import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Stack,
  Typography,
} from "@mui/material";
import GroupsIcon from "@mui/icons-material/Groups";
import PersonAddAltIcon from "@mui/icons-material/PersonAddAlt";
import { describeApiError } from "../api";
import { advise } from "../apiGuidance";
import {
  AUDIENCE_LOCKED_ELSEWHERE,
  FROZEN_STUDENTS_ELSEWHERE,
  audienceEditability,
  audienceListsState,
  isAudienceLocked,
  removeSectionLabel,
  removeStudentLabel,
} from "../eventAudience";
import { ErrorState, LoadingState } from "./ResourceStates";
import type { EventAudience, EventAudienceGroup, EventAudienceStudent } from "../types";

/** The audience read, as the states this panel renders. `audience: undefined` is a 404 on the event. */
export type AudienceRead =
  | { status: "loading" }
  | { status: "ready"; audience: EventAudience | undefined }
  | { status: "error"; error: unknown };

/** Which row a detach is running against, so exactly one button shows a spinner. */
export type DetachTarget =
  | { kind: "group"; id: string }
  | { kind: "student"; id: string };

const NO_SUCH_EVENT_AUDIENCE =
  "This event is not there any more, so it has no audience to show. It may have been deleted while " +
  "this page was open.";

const HEADING_DETACH_FAILED = "That was not removed";
const HEADING_DETACH_UNKNOWN = "It is not clear whether that was removed";

interface EventAudiencePanelProps {
  /**
   * The event's status as the page read it — the gate, and the source of the sentence explaining it.
   *
   * The event's own status rather than `EventAudience.isFrozen`, deliberately: the gate decides what
   * to *offer*, and it must agree with the buttons beside it on the same page, which are driven by
   * `eventStatus.ts`. `isFrozen` is used below for what it is actually for — explaining the shape of
   * the data this read returned.
   */
  eventStatus: string;
  read: AudienceRead;
  /** A re-read is running over data still on screen. Say so; do not blank the panel. */
  refreshing: boolean;
  onRetry: () => void;
  attach: {
    /** Opens the picker. Rendered even when it cannot be pressed, so it is never silently absent. */
    open: () => void;
    /**
     * What the last successful attach warned about — held by the page, because the dialog that
     * produced it is gone by the time this renders. A group from a non-current term warns rather than
     * refuses, and this array is the only evidence that happened.
     */
    warnings: readonly string[];
    dismissWarnings: () => void;
  };
  detach: {
    running: DetachTarget | undefined;
    failure: { error: unknown } | undefined;
    group: (group: EventAudienceGroup) => void;
    student: (student: EventAudienceStudent) => void;
  };
}

export default function EventAudiencePanel({
  eventStatus,
  read,
  refreshing,
  onRetry,
  attach,
  detach,
}: EventAudiencePanelProps) {
  const editable = audienceEditability(eventStatus);
  const audience = read.status === "ready" ? read.audience : undefined;
  const locked = detach.failure !== undefined && isAudienceLocked(detach.failure.error);

  return (
    <Card sx={{ mb: 3 }}>
      <CardContent>
        <Stack
          direction="row"
          justifyContent="space-between"
          alignItems="center"
          flexWrap="wrap"
          gap={1}
          sx={{ mb: 1 }}
        >
          <Stack direction="row" spacing={1} alignItems="center">
            <GroupsIcon color="primary" aria-hidden />
            <Typography variant="h6" component="h2">
              Audience
            </Typography>
            {audience !== undefined && (
              // The invited population (ADR-003 D-19) — not a count of rows in the lists below, which
              // is why it is stated rather than derived here: a section of 40 is one row and forty
              // expected students.
              <Chip
                size="small"
                label={`${audience.expected} expected`}
                color={audience.expected === 0 ? "default" : "primary"}
              />
            )}
            {/* Mounted whether or not a re-read is running, so the live region exists in the DOM
                before anything is put into it. */}
            <Box role="status" sx={{ display: "flex", alignItems: "center", gap: 1, minHeight: 24 }}>
              {refreshing && (
                <>
                  <CircularProgress size={16} aria-hidden />
                  <Typography variant="body2" color="text.secondary">
                    Refreshing…
                  </Typography>
                </>
              )}
            </Box>
          </Stack>

          <Button
            size="small"
            variant="outlined"
            startIcon={<PersonAddAltIcon />}
            onClick={attach.open}
            disabled={!editable.can || read.status !== "ready" || audience === undefined}
          >
            Add sections
          </Button>
        </Stack>

        {/* Why the button above cannot be pressed. Printed for a terminal event whether or not the
            read has settled, because the reason is a fact about the event rather than about the
            data — and a user who cannot see it is left hunting for a permission they do not lack. */}
        {!editable.can && (
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: "68ch" }}>
            {editable.reason}
          </Typography>
        )}

        {attach.warnings.length > 0 && (
          // `warning`, not `error`: the attach succeeded. A term mismatch warns rather than refuses
          // because `Events` carries no `TermId`, so "another term" can only be measured against a
          // flag that moves under the event's feet — refusing would make an identical request start
          // failing for an event nobody touched.
          <Alert
            severity="warning"
            role="status"
            onClose={attach.dismissWarnings}
            sx={{ mb: 2 }}
          >
            <AlertTitle>Attached, with something worth knowing</AlertTitle>
            <Stack component="ul" spacing={0.5} sx={{ pl: 2, m: 0 }}>
              {/* Keyed by index, which is right here and wrong almost everywhere else: this list is
                  rendered once from one response, never reordered and never edited in place, and two
                  identical warning strings in one reply are possible — which the text as a key would
                  turn into duplicate keys and a dropped line. */}
              {attach.warnings.map((warning, i) => (
                <Typography component="li" variant="body2" key={i}>
                  {warning}
                </Typography>
              ))}
            </Stack>
          </Alert>
        )}

        {detach.failure !== undefined &&
          (locked ? (
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>
              <AlertTitle>This event’s audience is now fixed</AlertTitle>
              <Typography variant="body2">{AUDIENCE_LOCKED_ELSEWHERE}</Typography>
            </Alert>
          ) : (
            // Not `WriteFailureAlert`: that component's withheld-resend line describes a submit button
            // to withhold, and a detach has no form — the control is the row's own Remove, which the
            // re-read below either removes or leaves in place. The heading is still derived from what
            // the seam knows the server did rather than asserted.
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>
              <AlertTitle>
                {isDetachOutcomeUnknown(detach.failure.error)
                  ? HEADING_DETACH_UNKNOWN
                  : HEADING_DETACH_FAILED}
              </AlertTitle>
              <Typography variant="body2">{describeApiError(detach.failure.error)}</Typography>
              <Typography variant="body2" sx={{ mt: 1 }}>
                The list below is re-read after every attempt, so it says what the audience actually
                holds. Removing the same section again is safe either way — the API answers a section
                that is not attached the same way it answers one it just removed.
              </Typography>
            </Alert>
          ))}

        {read.status === "loading" && <LoadingState label="Loading the audience…" />}

        {read.status === "error" && (
          <ErrorState subject="this event’s audience" error={read.error} onRetry={onRetry} />
        )}

        {read.status === "ready" && audience === undefined && (
          <Typography variant="body2" color="text.secondary" role="status">
            {NO_SUCH_EVENT_AUDIENCE}
          </Typography>
        )}

        {audience !== undefined && (
          <AudienceLists audience={audience} canEdit={editable.can} detach={detach} />
        )}
      </CardContent>
    </Card>
  );
}

/**
 * Whether a failed detach may still have been applied — the same question `WriteFailureAlert` derives
 * its heading from, asked directly because this alert is not that component.
 *
 * A network failure or a 500 on a `DELETE` leaves the outcome genuinely unknown; a 404 or a 409 is the
 * server having decided. Deliberately not "was it a 2xx": the seam already answers this on the error.
 */
function isDetachOutcomeUnknown(error: unknown): boolean {
  // Read through `advise` rather than re-deriving the taxonomy here — one taxonomy, read from one
  // place, is the rule `apiGuidance.ts` exists to keep.
  return advise(error).serverEffect === "unknown";
}

/**
 * The two lists, and the sentence that stops the second one being misread.
 *
 * Split out so the panel above reads as its states and this reads as its content.
 */
function AudienceLists({
  audience,
  canEdit,
  detach,
}: {
  audience: EventAudience;
  canEdit: boolean;
  detach: EventAudiencePanelProps["detach"];
}) {
  // The decision is `eventAudience.ts`'s, not this component's. It was two conditions inline here and
  // one of them was wrong in a way no test in this repo could reach: on a frozen event `students` is
  // empty by contract, so a list-length test called an individuals-only closed event "expected
  // nobody" underneath its own non-zero expected chip. Moving it to a pure function put it where the
  // suite already reaches — see `audienceListsState` for the rule and why the two branches differ.
  const body = audienceListsState(audience, canEdit);

  if (body.kind === "nothing-invited") {
    // `role="status"`: an empty audience usually arrives after a loading state, and it is the one
    // state on this panel a user has to act on.
    return (
      <Typography variant="body2" color="text.secondary" role="status" sx={{ maxWidth: "68ch" }}>
        {body.message}
      </Typography>
    );
  }

  return (
    <Stack spacing={2}>
      <Box>
        <Typography variant="subtitle2" component="h3" gutterBottom>
          Sections ({audience.groups.length})
        </Typography>
        {audience.groups.length === 0 ? (
          <Typography variant="body2" color="text.secondary">
            No sections are attached — everyone expected here was invited individually.
          </Typography>
        ) : (
          <Stack component="ul" spacing={1} sx={{ listStyle: "none", p: 0, m: 0 }}>
            {audience.groups.map((group) => (
              <SectionRow
                key={group.studentGroupId}
                group={group}
                isFrozen={audience.isFrozen}
                canEdit={canEdit}
                running={
                  detach.running?.kind === "group" && detach.running.id === group.studentGroupId
                }
                onRemove={() => detach.group(group)}
              />
            ))}
          </Stack>
        )}
      </Box>

      <Box>
        <Typography variant="subtitle2" component="h3" gutterBottom>
          Individually attached ({audience.students.length})
        </Typography>

        {/* The whole reason this panel needs prose. On a frozen event the empty list is the expected
            answer and not an absence — see the header of this file and ADR-003 D-13. Which of the
            three this is was decided by `audienceListsState`, so the rule is testable. */}
        {body.students === "elsewhere" ? (
          <Typography variant="body2" color="text.secondary" sx={{ maxWidth: "68ch" }}>
            {FROZEN_STUDENTS_ELSEWHERE}
          </Typography>
        ) : body.students === "none-attached" ? (
          <Typography variant="body2" color="text.secondary">
            Nobody is attached individually — everyone expected here comes from the sections above.
          </Typography>
        ) : (
          <Stack component="ul" spacing={1} sx={{ listStyle: "none", p: 0, m: 0 }}>
            {audience.students.map((student) => (
              <StudentRow
                key={student.studentId}
                student={student}
                canEdit={canEdit}
                running={
                  detach.running?.kind === "student" && detach.running.id === student.studentId
                }
                onRemove={() => detach.student(student)}
              />
            ))}
          </Stack>
        )}
      </Box>
    </Stack>
  );
}

function SectionRow({
  group,
  isFrozen,
  canEdit,
  running,
  onRemove,
}: {
  group: EventAudienceGroup;
  isFrozen: boolean;
  canEdit: boolean;
  running: boolean;
  onRemove: () => void;
}) {
  const members = `${group.memberCount} ${group.memberCount === 1 ? "student" : "students"}`;
  return (
    <RemovableRow
      primary={group.name}
      secondary={
        // Two qualifications, both load-bearing.
        //
        // The term is part of the identity rather than decoration: the same section name exists in
        // every semester against a different cohort, and a row that does not name its term cannot be
        // checked against the one the organizer meant.
        //
        // And on a frozen event `memberCount` is the section's membership **now**, not the membership
        // that was written down — the contract is explicit that it describes the group as it currently
        // stands, while the frozen population is the `expected` figure in the header. Those two
        // legitimately differ, and printing a bare "41 students" beside "38 expected" on a closed
        // event is a page appearing to contradict itself.
        `${isFrozen ? `${members} today` : members}${
          group.termCode === undefined ? "" : ` · ${group.termCode}`
        }`
      }
      removeLabel={removeSectionLabel(group.name)}
      canEdit={canEdit}
      running={running}
      onRemove={onRemove}
    />
  );
}

function StudentRow({
  student,
  canEdit,
  running,
  onRemove,
}: {
  student: EventAudienceStudent;
  canEdit: boolean;
  running: boolean;
  onRemove: () => void;
}) {
  return (
    <RemovableRow
      primary={student.fullName}
      // `section` is ADR-002 D-9's display cache and is read for display only. On a student in more
      // than one section it can name one other than the section that invited them, which is why it is
      // shown after the student number rather than as the row's subject.
      secondary={
        student.section === undefined
          ? student.studentNumber
          : `${student.studentNumber} · ${student.section}`
      }
      removeLabel={removeStudentLabel(student.fullName, student.studentNumber)}
      canEdit={canEdit}
      running={running}
      onRemove={onRemove}
    />
  );
}

/**
 * One audience row.
 *
 * The remove control is a real `<button>` with an accessible name that says **which** section or
 * student it removes: a screen reader reads a button's label out of context, and a panel with six of
 * them would otherwise announce "Remove, Remove, Remove". The visible label stays the bare verb, which
 * is what the eye needs beside a named row.
 *
 * No confirmation, and that is a decision rather than an omission. Detaching is reversible — the
 * section can be attached again from the same picker, and the post is idempotent — so it is not in the
 * class of one-way writes (`Close`, `Cancel`, `Delete`) that this app confirms. It is refused outright
 * on a terminal event, which is where the irreversible version of this would live.
 */
function RemovableRow({
  primary,
  secondary,
  removeLabel,
  canEdit,
  running,
  onRemove,
}: {
  primary: string;
  secondary: string;
  removeLabel: string;
  canEdit: boolean;
  running: boolean;
  onRemove: () => void;
}) {
  return (
    <Stack
      component="li"
      direction="row"
      spacing={2}
      alignItems="center"
      justifyContent="space-between"
      sx={{ border: 1, borderColor: "divider", borderRadius: 1, px: 2, py: 1 }}
    >
      <Box sx={{ minWidth: 0 }}>
        <Typography sx={{ wordBreak: "break-word" }}>{primary}</Typography>
        <Typography variant="body2" color="text.secondary">
          {secondary}
        </Typography>
      </Box>

      {canEdit && (
        <Button
          size="small"
          color="error"
          onClick={onRemove}
          disabled={running}
          aria-label={removeLabel}
          startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {running ? "Removing…" : "Remove"}
        </Button>
      )}
    </Stack>
  );
}
