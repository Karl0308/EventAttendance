import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useNavigate, useParams, Link } from "react-router-dom";
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
import EditIcon from "@mui/icons-material/Edit";
import DeleteOutlineIcon from "@mui/icons-material/DeleteOutline";
import { api, describeApiError } from "../api";
import { advise } from "../apiGuidance";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import EditEventDialog from "../components/EditEventDialog";
import DeleteEventDialog from "../components/DeleteEventDialog";
import ChangeEventStatusDialog from "../components/ChangeEventStatusDialog";
import EventAudiencePanel from "../components/EventAudiencePanel";
import UnresolvedScansPanel from "../components/UnresolvedScansPanel";
import type { ScanLogRead } from "../components/UnresolvedScansPanel";
import type { AudienceRead, DetachTarget } from "../components/EventAudiencePanel";
import AudiencePickerDialog from "../components/AudiencePickerDialog";
// Rendered only behind `import.meta.env.DEV` below. That flag is a compile-time literal, so the
// production build drops the element, this import, the panel and `deviceKey.ts` along with it — which
// is what keeps a capture credential out of the published artefact.
import DevDeviceKeyPanel from "../components/DevDeviceKeyPanel";
import { knownMode, localFrom } from "../eventDraft";
import type { EditScope } from "../eventDraft";
import { attachSettledText } from "../eventAudience";
import { statusActionsFor, statusSettledText } from "../eventStatus";
import type { StatusChange } from "../eventStatus";
import { EVENT_STATUS } from "../types";
import type {
  AttendanceRecord,
  EventAudienceGroup,
  EventAudienceRequest,
  EventAudienceStudent,
  EventItem,
  EventStatusName,
  EventWriteRequest,
} from "../types";

// A deep link to a deleted or mistyped event is an answer, not a failure: `api.eventDetail` maps that
// 404 to `event: undefined`, and this says so plainly instead of leaving the screen loading.
const NO_SUCH_EVENT = "No event with this id. It may have been deleted, or the link may be wrong.";

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
// DeviceKey this SPA deliberately does not hold — in a *development* build it may hold one the
// developer pasted, which changes who the simulator can tap as and changes nothing about this
// sentence, since a published build has neither the key nor the code that could hold one.
const ROSTER_UNRECOVERABLE =
  "Real card taps do not go through this screen — only the simulator is affected. Nothing here will " +
  "bring the picker back; report this to whoever maintains EAMS.";

// ---------------------------------------------------------------------------------------------
// Who may edit this event, and how much of it
// ---------------------------------------------------------------------------------------------
//
// `PUT /events/{id}` has three modes, and a form that knew about two of them would produce 409s the
// user cannot act on. Two of the three are the server's (`EventStatusTransition.AcceptsEdits` and
// `AcceptsAttendanceRuleEdits`); the third refusal below is this build's own, and is the more
// interesting one.
//
// A `PUT` is a **full replacement**. Every field goes back, including the ones the user did not
// touch, so this client can only offer the form for an event it can reproduce exactly. Two things
// stop it: an `attendanceMode` outside the two `EventWriteRequest` can carry — the form would send
// `Single` and silently rewrite it — and a `startAt`/`endAt` this build cannot parse, which would go
// into an empty datetime box and come back out as a validation error on a field the user never went
// near. Both are refusals rather than fallbacks, because both fallbacks change data on a save the
// user made for an unrelated reason.

type Editability =
  | { can: true; scope: EditScope }
  | { can: false; reason: string };

const CLOSED_NO_EDITS =
  "This event is Closed, so nothing about it can be edited. Its recorded attendance is final, and " +
  "the start time and grace period that decided Present versus Late for each row cannot be moved out " +
  "from under them. It can still be deleted.";

function editabilityOf(event: EventItem): Editability {
  if (event.status === EVENT_STATUS.Closed) return { can: false, reason: CLOSED_NO_EDITS };

  if (knownMode(event.attendanceMode) === undefined) {
    return {
      can: false,
      reason:
        `This event's attendance mode (“${event.attendanceMode}”) is not one this admin build ` +
        "recognises, so the edit form cannot send it back unchanged — saving would rewrite it. This " +
        "build and the API are probably different versions.",
    };
  }

  if (localFrom(event.startAt) === "" || localFrom(event.endAt) === "") {
    return {
      can: false,
      reason:
        "This event's start or end date is not one this admin build can read, so the edit form " +
        "cannot send it back unchanged. This build and the API are probably different versions.",
    };
  }

  // Draft and Open take every field; everything else takes the descriptive ones only. Written as
  // "these two, else descriptive" rather than "Cancelled, else everything" so that a fifth status the
  // server adds tomorrow lands on the cautious side — which is also the side the server itself lands
  // on, since `AcceptsAttendanceRuleEdits` is a two-value allow-list rather than a Cancelled check.
  const everything =
    event.status === EVENT_STATUS.Draft || event.status === EVENT_STATUS.Open;
  return { can: true, scope: everything ? "everything" : "descriptive" };
}

// ---------------------------------------------------------------------------------------------

/** What the Snackbar is currently saying, and how loudly. As `Events.tsx`, for the same reasons. */
interface Notice {
  severity: "success" | "error";
  text: string;
}

/** How long a confirmation stays up. Long enough to be read, not long enough to nag. */
const NOTICE_MS = 6000;

/**
 * A failure does not time out. It is only ever announced here when the dialog that would have carried
 * it is gone, so it is the only copy the user gets. MUI reads `null` as "stay until dismissed"; the
 * Alert has its own close button.
 */
const NO_AUTO_HIDE = null;

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
 * What is left of the screen after a successful delete.
 *
 * Not a redirect to `/events`. The list would show the event's absence, which is evidence rather than
 * confirmation, and the one thing an admin most needs to hear at this moment — that the attendance
 * was kept — would have nowhere to appear. This says it, then offers the way back.
 *
 * It takes focus on mount, because the button that raised it and the dialog that held that button are
 * both gone: focus has fallen to `document.body`, so a keyboard user is at the top of a document
 * whose content just changed completely and a screen-reader user has lost their place (WCAG 2.4.3).
 */
function EventDeleted({ name, onBack }: { name: string; onBack: () => void }) {
  const panel = useRef<HTMLDivElement>(null);

  useEffect(() => {
    panel.current?.focus();
  }, []);

  return (
    <Alert
      ref={panel}
      // -1 keeps it out of the Tab order — it is a focus *destination*, not a stop on the way.
      tabIndex={-1}
      severity="success"
      role="status"
      sx={{ my: 2 }}
      action={
        <Button color="inherit" size="small" onClick={onBack}>
          Back to events
        </Button>
      }
    >
      <AlertTitle>“{name}” was deleted</AlertTitle>
      <Typography variant="body2">
        It is gone from every list, summary and report. The attendance recorded against it is kept.
      </Typography>
    </Alert>
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
  const nav = useNavigate();

  // One read for the whole screen, parameterised by the route id: the load is an inline arrow and
  // `[id]` is what re-runs it. Its failure becomes the rendered error state below. This used to be a
  // bare `refresh()` inside an effect with no rejection handler, so an unreachable API left the page
  // saying "Loading…" for as long as anyone was willing to wait for it.
  const detail = useApiResource(() => api.eventDetail(id), [id]);

  /**
   * The audience, read **beside** `eventDetail` rather than inside it, and the separation is
   * deliberate on two counts.
   *
   * It must not be able to take the screen down. `eventDetail` composes the three reads that are three
   * views of one fact and fails them together, precisely so the page cannot render half a truth; the
   * audience is not one of those views, any more than the tap roster is. An event whose attendance
   * grid is perfectly healthy must not become unviewable because this endpoint is unavailable — and it
   * is the newest surface in the API, so "unavailable" is the case to design for rather than the
   * exotic one.
   *
   * And it re-reads on its own. Attaching or removing a section changes the audience and nothing else
   * on this page except the denominator; re-walking the whole attendance list after every removal
   * would cost pages of rows to refresh two lines.
   */
  const audience = useApiResource(() => api.getEventAudience(id), [id]);

  /**
   * The unresolved scans, read separately from the roster and deliberately so.
   *
   * It is the one call in this app that requires being signed in - every other read is still open -
   * so a failure here is its own failure and must not be able to blank the roster beside it. A
   * combined read would let an expired session hide attendance that loaded perfectly well.
   */
  const scans = useApiResource(() => api.getEventScans(id), [id]);

  /**
   * Both writes live here rather than inside the dialogs that start them, which is D1a's shape and
   * the reason for it: a dialog that owns its own write ends that write when it unmounts, so Cancel,
   * Escape and the backdrop all have to be blocked for up to `REQUEST_TIMEOUT_MS` — fifteen seconds
   * of a keyboard user trapped in a modal — and even then browser Back unmounts it anyway.
   *
   * Not a complete cure, and worth being precise about: navigating away from this route unmounts the
   * page too, and that outcome is still lost to `console.debug`. Closing that would take state above
   * the router, which is a design decision rather than a fix — carried, not smuggled in.
   */
  const edit = useApiMutation((request: EventWriteRequest) => api.updateEvent(id, request));
  const remove = useApiMutation(() => api.deleteEvent(id));
  const move = useApiMutation((target: EventStatusName) => api.setEventStatus(id, target));

  const attach = useApiMutation((request: EventAudienceRequest) =>
    api.attachEventAudience(id, request),
  );
  /**
   * One mutation for both detaches, keyed by the target's `kind`.
   *
   * Two hooks would be the other shape and would be worse here: the panel disables exactly one row's
   * button while a removal runs, so it needs a single "which row is running" answer, and two
   * independent `running` flags would let a section and a student be removed at once with the second
   * failure overwriting the first's alert.
   */
  const detach = useApiMutation((target: DetachTarget) =>
    target.kind === "group"
      ? api.detachEventGroup(id, target.id)
      : api.detachEventStudent(id, target.id),
  );

  /**
   * The event each dialog is about, held rather than read from `detail` while it is open.
   *
   * `undefined` doubles as "closed", which is the same collapse the hooks forbid — except that here
   * the two really are one state: there is no dialog without an event for it to be about. Holding the
   * event also means the dialogs render outside the `ready` branch below, so a re-read that turns the
   * event into a 404 mid-save cannot pull the failure alert off the screen with it.
   */
  const [editing, setEditing] = useState<{ event: EventItem; scope: EditScope } | undefined>(
    undefined,
  );
  const [deleting, setDeleting] = useState<EventItem | undefined>(undefined);

  /**
   * The move being confirmed, captured when the dialog opens rather than re-derived from `detail`
   * while it is on screen — the same reason `editing` holds its event. A re-read that lands mid-write
   * must not be able to change which transition the open confirmation is about: the sentence the user
   * read before pressing has to be the sentence describing what gets sent.
   */
  const [changing, setChanging] = useState<{ event: EventItem; change: StatusChange } | undefined>(
    undefined,
  );

  /** Survives a successful delete, which is the one outcome that leaves no event to render. */
  const [deleted, setDeleted] = useState<string | undefined>(undefined);

  /** Whether the audience picker is on screen. It is about the event as a whole, so it holds nothing. */
  const [picking, setPicking] = useState(false);

  /**
   * What the last successful attach warned about — held **here**, above the dialog that produced it.
   *
   * The dialog closes on success, so a warning left inside it would be rendered for the length of one
   * commit and then thrown away. A group from a non-current term warns rather than refuses, and the
   * warning is the only evidence that happened: a 200 with an unread `warnings` array is
   * indistinguishable from an ordinary attach, which is the exact failure the field exists to prevent.
   * It is dismissed by the user rather than by a timer, for the same reason.
   */
  const [warnings, setWarnings] = useState<readonly string[]>([]);

  /**
   * Which row a removal is running against. Held beside the mutation rather than derived from it,
   * because `useApiMutation` knows that something is running and not what it is about — and the panel
   * disables one row's button, not all of them.
   */
  const [detaching, setDetaching] = useState<DetachTarget | undefined>(undefined);

  /**
   * Whether each dialog is on screen, read at the moment a write *settles* rather than from the
   * closure that started it. The state captured there is a render old, and the interesting case is
   * exactly the one where it changed in between — the user dismissed the dialog while the write was
   * in flight, so there is no longer an alert for the failure to appear in.
   */
  const editOpen = useRef(editing !== undefined);
  const deleteOpen = useRef(deleting !== undefined);
  const changeOpen = useRef(changing !== undefined);
  /**
   * Whether the audience panel is on screen — the same question the three refs above ask about their
   * dialogs, asked about a panel that lives inside the `ready` branch. A removal that fails while a
   * background re-read has replaced that branch with the error state has nowhere to be rendered, and
   * the Snackbar is the only place left for it.
   */
  const panelMounted = useRef(false);
  const pickerOpen = useRef(picking);
  useLayoutEffect(() => {
    editOpen.current = editing !== undefined;
    deleteOpen.current = deleting !== undefined;
    changeOpen.current = changing !== undefined;
    pickerOpen.current = picking;
    panelMounted.current = detail.status === "ready" && detail.data.event !== undefined;
  });

  const [pickUid, setPickUid] = useState("");

  // Two pieces rather than one `Notice | undefined` driving both, as `Events.tsx` records: MUI keeps
  // a Snackbar's children mounted through its closing fade, so clearing the notice to close it would
  // blank the text mid-fade and leave an empty bar on screen for the length of the transition.
  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);

  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  // Defined only once the read has settled, which is what the render branches on below.
  const data = detail.status === "ready" ? detail.data : undefined;
  /**
   * The event itself, bound to a `const` rather than reached as `data.event` at each use.
   *
   * Not a tidy-up: TypeScript discards a narrowing of `data.event` inside every callback below,
   * because a property could in principle change before the handler runs, while a narrowing of a
   * `const` binding survives into them. Without this the Edit and Delete handlers each need a
   * re-check that can never fail, which reads as defensiveness against a case that does not exist.
   */
  const event = data?.event;
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
      announce({ severity: res.ok ? "success" : "error", text: res.message });
    } catch (cause) {
      announce({ severity: "error", text: describeApiError(cause) });
    }
    // Re-read either way. A tap that failed on the way back may still have been recorded, so the
    // screen should not be left asserting the state from before it. Since `reload` now keeps the
    // rendered data in place while it runs, this costs a refresh rather than the whole screen.
    detail.reload();
  };

  const openEdit = (event: EventItem) => {
    // The scope is settled here, at the one moment it is known to be a scope at all, and carried with
    // the event. Re-deriving it in the render below would mean calling `editabilityOf` twice on a
    // value the compiler cannot narrow across the two calls — and writing a fallback for the arm that
    // has no scope, which would hand a Closed event a form it can only get a 409 from.
    const editable = editabilityOf(event);
    if (!editable.can) return;

    // A failure from an attempt the user walked away from is still in the mutation, and without this
    // it would greet them as though it were about the edit they have not made yet. Refused while a
    // write is in flight — see `useApiMutation.reset` — in which case the reopened form correctly
    // shows that one still running.
    edit.reset();
    setEditing({ event, scope: editable.scope });
  };

  const openDelete = (event: EventItem) => {
    remove.reset();
    setDeleting(event);
  };

  const openStatusChange = (event: EventItem, change: StatusChange) => {
    move.reset();
    setChanging({ event, change });
  };

  const submitEdit = (request: EventWriteRequest) => {
    // `run` never rejects; it answers with an outcome. The floating promise is deliberate and marked.
    void edit.run(request).then((settled) => {
      // `ignored` — a save was already in flight and this submit sent nothing. Nothing settled, so
      // there is nothing to report and no reason to re-read: the first one is still coming.
      if (settled.outcome === "ignored") return;

      // Either way, and the failure case is the one that matters: a `PUT` that failed on the way back
      // may still have been applied, and a screen that goes on asserting the state from before it is
      // how someone comes to believe an edit was lost. `reload` keeps the rows already rendered and
      // raises `refreshing` rather than dropping back to `loading`.
      detail.reload();

      if (settled.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to “${settled.data.name}”.` });
        return;
      }

      // The dialog renders the failure in context when it is still open — beside the draft that
      // caused it, with the heading derived from what the server may have done. Announcing it here as
      // well would double it, and a Snackbar sits above the modal. When the dialog is gone, this is
      // the only place left for it.
      if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmStatusChange = (name: string, target: EventStatusName) => {
    void move.run(target).then((settled) => {
      if (settled.outcome === "ignored") return;

      // Either way. On success the whole screen changes meaning — the tap simulator appears or
      // disappears, the summary is recomputed against a frozen denominator, and a close has just
      // written attendance rows the grid below is showing a stale version of. On a failure the change
      // may still have been applied, and a screen that goes on asserting the previous status is how
      // someone presses it a second time.
      detail.reload();

      if (settled.outcome === "succeeded") {
        setChanging(undefined);
        // From the status the server came back with, not the one that was asked for. They differ
        // exactly once — the no-op arm answering a retried request — and that is the case where
        // narrating the freeze would describe something that did not run.
        announce({ severity: "success", text: statusSettledText(name, settled.data.status) });
        return;
      }

      if (!changeOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmDelete = (name: string) => {
    void remove.run().then((settled) => {
      if (settled.outcome === "ignored") return;

      if (settled.outcome === "succeeded") {
        setDeleting(undefined);
        // No re-read. The event is gone, so re-reading it would answer "no such event" — true, and
        // the same sentence a mistyped URL produces. What actually happened deserves to be said in
        // its own words, including what survived it.
        setDeleted(name);
        return;
      }

      // A failed delete may still have been applied. The re-read settles it: the screen either comes
      // back with the event or with "no event with this id", which is the honest answer either way —
      // and the same one someone else deleting it first would produce.
      detail.reload();

      if (!deleteOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const openPicker = () => {
    // A failure from an attempt the user walked away from is still in the mutation, and without this
    // it would greet them as though it were about the sections they have not chosen yet.
    attach.reset();
    setPicking(true);
  };

  const submitAttach = (request: EventAudienceRequest) => {
    void attach.run(request).then((settled) => {
      if (settled.outcome === "ignored") return;

      // Either way. A failed attach may still have been applied — the post is idempotent, so a
      // re-read is both the cheapest and the only honest way to find out what actually landed.
      audience.reload();
      // The stat cards are the other half of the same fact: `expected` is the denominator of the
      // attendance rate printed there (ADR-003 D-19), so attaching a section moves a number on the
      // summary as well as a row on the panel.
      detail.reload();

      if (settled.outcome === "succeeded") {
        setPicking(false);
        // Held rather than announced: a Snackbar times out, and a warning about which cohort was
        // attached is the kind of thing an organizer reads after they have looked at the list.
        setWarnings(settled.data.warnings);
        announce({ severity: "success", text: attachSettledText(settled.data) });
        return;
      }

      if (!pickerOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const removeFromAudience = (target: DetachTarget, subject: string) => {
    setDetaching(target);
    void detach.run(target).then((settled) => {
      setDetaching(undefined);
      if (settled.outcome === "ignored") return;

      audience.reload();
      detail.reload();

      if (settled.outcome === "succeeded") {
        announce({
          severity: "success",
          text: `${subject} is no longer part of this event's audience.`,
        });
        return;
      }

      // The panel renders the failure in place, beside the row it is about. When a re-read has taken
      // the panel off the screen, this is the only place left for it.
      if (!panelMounted.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const removeGroup = (group: EventAudienceGroup) =>
    removeFromAudience({ kind: "group", id: group.studentGroupId }, `Section ${group.name}`);

  const removeStudent = (student: EventAudienceStudent) =>
    removeFromAudience({ kind: "student", id: student.studentId }, student.fullName);

  /** The audience read, mapped into the three states the panel renders. */
  const audienceRead: AudienceRead =
    audience.status === "ready"
      ? { status: "ready", audience: audience.data }
      : audience.status === "error"
        ? { status: "error", error: audience.error }
        : { status: "loading" };

  /**
   * What the picker must not offer again — read from the audience rather than remembered across the
   * dialog's life, so a section attached in another tab shows as attached here too.
   */
  /** The scan-log read, mapped into the three states its panel renders. */
  const scansRead: ScanLogRead =
    scans.status === "ready"
      ? { status: "ready", log: scans.data }
      : scans.status === "error"
        ? { status: "error", error: scans.error }
        : { status: "loading" };

  const attachedGroupIds = new Set(
    (audience.status === "ready" ? audience.data?.groups ?? [] : []).map(
      (group) => group.studentGroupId,
    ),
  );

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

  // The delete confirmation replaces the screen it was about, so nothing below renders once it has
  // succeeded — a stat grid for an event that no longer exists is a screen contradicting itself.
  if (deleted !== undefined) {
    return (
      <Box>
        <EventDeleted name={deleted} onBack={() => nav("/events")} />
      </Box>
    );
  }

  const editability = event === undefined ? undefined : editabilityOf(event);

  // Only the moves the graph actually has, mirrored from `EventStatusTransition` — a button for a
  // transition the server does not have is a control whose only possible outcome is a 400. When there
  // are none, `noneBecause` says why: a status action that is simply absent is as silent as one that
  // greys out, and this codebase's standing rule is that neither may be.
  const statusActions = event === undefined ? undefined : statusActionsFor(event.status);

  return (
    <Box>
      {detail.status === "loading" && <LoadingState label="Loading the event…" />}

      {/* Nothing below renders on failure: a header above four zeroed stat cards reads as a quiet
          event rather than a broken one, and that is the reading a user is most likely to believe. */}
      {detail.status === "error" && (
        <ErrorState subject="this event" error={detail.error} onRetry={detail.reload} />
      )}

      {data !== undefined &&
        (event === undefined ? (
          <EmptyState message={NO_SUCH_EVENT} />
        ) : (
          <>
            <Breadcrumbs sx={{ mb: 1 }}>
              <MuiLink component={Link} to="/events" underline="hover">
                Events
              </MuiLink>
              <Typography color="text.primary">{event.name}</Typography>
            </Breadcrumbs>

            {/* `flexWrap` and a gap rather than a fixed row: the title, the status chip and two
                buttons do not fit on one line at 320px, and a header that overflows horizontally
                takes the whole page's scrollbar with it. */}
            <Stack
              direction="row"
              justifyContent="space-between"
              alignItems="flex-start"
              flexWrap="wrap"
              gap={2}
              sx={{ mb: 2 }}
            >
              <Box>
                <Typography variant="h5" fontWeight={700}>
                  {event.name}
                </Typography>
                <Typography color="text.secondary">
                  {event.location} · {new Date(event.startAt).toLocaleString()} · grace{" "}
                  {event.graceMinutes}m
                </Typography>
              </Box>

              <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
                <Chip
                  label={event.status}
                  color={event.status === EVENT_STATUS.Open ? "success" : "default"}
                />
                {/* One button per reachable target, never a menu of all four. Two at most, and each
                    opens its own confirmation: which transition is being made is the whole content of
                    that confirmation, so it cannot be a shared "Are you sure?" over a dropdown. */}
                {statusActions?.changes.map((change) => (
                  <Button
                    key={change.target}
                    size="small"
                    variant="outlined"
                    onClick={() => openStatusChange(event, change)}
                  >
                    {change.verb} event
                  </Button>
                ))}
                {/* Rendered whether or not it can be pressed, so the action is discoverable and its
                    absence is never silent. When it cannot, the sentence below says why — a control
                    that greys out without saying why leaves the user hunting for a permission they
                    do not lack. */}
                <Button
                  size="small"
                  startIcon={<EditIcon />}
                  onClick={() => openEdit(event)}
                  disabled={editability?.can !== true}
                >
                  Edit
                </Button>
                {/* Never disabled by status: a soft delete is allowed from every one of them,
                    including Closed, because the attendance rows survive it untouched. */}
                <Button
                  size="small"
                  color="error"
                  startIcon={<DeleteOutlineIcon />}
                  onClick={() => openDelete(event)}
                >
                  Delete
                </Button>
              </Stack>
            </Stack>

            {/* Measured in characters, because the limit being avoided is a line too long to track
                back to its start rather than a number of pixels. */}
            {statusActions?.noneBecause !== undefined && (
              <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: "68ch" }}>
                {statusActions.noneBecause}
              </Typography>
            )}

            {editability?.can === false && (
              <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: "68ch" }}>
                {editability.reason}
              </Typography>
            )}

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

            {/* Above the tap simulator and the grid, because it is the question they both depend on:
                who this event expects is what the denominator, the absentee list and every "did they
                turn up" reading below are computed against. */}
            <EventAudiencePanel
              eventStatus={event.status}
              read={audienceRead}
              refreshing={audience.refreshing}
              onRetry={audience.reload}
              attach={{
                open: openPicker,
                warnings,
                dismissWarnings: () => setWarnings([]),
              }}
              detach={{
                running: detaching,
                failure: detach.status === "failed" ? { error: detach.error } : undefined,
                group: removeGroup,
                student: removeStudent,
              }}
            />

            {/* Below the roster and the audience, because it is the remainder of what they describe:
                these scans have no student, so they cannot be rows in either, and reading them
                anywhere but directly beneath the attendance they are missing from would make them
                look like a separate system rather than the same event. */}
            <UnresolvedScansPanel read={scansRead} onRetry={scans.reload} />

            {event.status === EVENT_STATUS.Open && (
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

                  {/* Outside the roster branch: the key is worth setting (and clearing) whether or
                      not the picker has anything to offer, and a roster that failed to load says
                      nothing about the credential. `import.meta.env.DEV` is a compile-time literal —
                      in a production build this whole subtree, its import and `deviceKey.ts` are
                      eliminated, so the artefact carries no capture credential and no way to hold
                      one. Verified with a grep over `dist/assets/*.js`, not assumed. */}
                  {import.meta.env.DEV && <DevDeviceKeyPanel />}
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

      {/* Outside the branches above, and deliberately: a re-read that fails — or that comes back
          without the event — replaces the ready branch, and a dialog living inside it would be torn
          down mid-write, taking the failure alert it was about to render with it. Each holds its own
          copy of the event for the same reason. */}
      {editing !== undefined && (
        <EditEventDialog
          event={editing.event}
          scope={editing.scope}
          onClose={() => setEditing(undefined)}
          onSubmit={submitEdit}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {changing !== undefined && (
        <ChangeEventStatusDialog
          name={changing.event.name}
          change={changing.change}
          onClose={() => setChanging(undefined)}
          onConfirm={() => confirmStatusChange(changing.event.name, changing.change.target)}
          running={move.status === "running"}
          failure={move.status === "failed" ? { error: move.error } : undefined}
        />
      )}

      {picking && (
        <AudiencePickerDialog
          attachedGroupIds={attachedGroupIds}
          onClose={() => setPicking(false)}
          onSubmit={submitAttach}
          running={attach.status === "running"}
          failure={attach.status === "failed" ? { error: attach.error } : undefined}
        />
      )}

      {deleting !== undefined && (
        <DeleteEventDialog
          name={deleting.name}
          onClose={() => setDeleting(undefined)}
          onConfirm={() => confirmDelete(deleting.name)}
          running={remove.status === "running"}
          failure={remove.status === "failed" ? { error: remove.error } : undefined}
        />
      )}

      {/* Outside the branches on purpose: a re-read after a tap keeps the ready branch mounted, but a
          re-read that *fails* replaces it with the error state, and a Snackbar living inside that
          branch would take the message it was just given down with it — at exactly the moment the
          message is a failure the user needs to read. */}
      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {/* Rendered from the notice rather than defaulted from it: a fallback severity would paint a
            failure green for one render if the two ever came apart. `status` for a confirmation —
            something the user caused is not an interruption — and `alert` for a failure, which is the
            one case where interrupting is the point, because the dialog that would have shown it is
            gone. */}
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
