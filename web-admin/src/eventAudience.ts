// Who an event expects: whether that can still be changed, and what to say about it.
//
// A module rather than helpers beside the panel, for the reason `eventStatus.ts` records about its own
// table: two of the three things here are **mirrors of a server rule** — which statuses accept an
// audience change, and what a 409 from that surface means — and a mirror that lives at its one call
// site is a mirror nobody can find when the original moves. The originals are
// `EventStatusTransition.IsTerminal` and `EventsController`'s `EventLocked` → 409 mapping.
//
// It decides nothing. The server still refuses; this decides what to *offer*, and what to say when the
// offer and the server disagree — which they can, because an event can be closed in another tab
// between this screen's read and the organizer's press.
//
// Free of React and of MUI, so it is testable the way the other three vocabulary modules are.
// `ApiError` is imported for the 409 test, exactly as `apiGuidance.ts` imports it.

import { ApiError } from "./api";
import { AUDIENCE_FROZEN, isTerminal, knownStatus } from "./eventStatus";
import type { EventAudience, EventAudienceResult } from "./types";

// ---------------------------------------------------------------------------------------------
// Whether the audience can be changed at all
// ---------------------------------------------------------------------------------------------

/**
 * Whether this event's audience may be edited, and — when it may not — why, said out loud.
 *
 * The same shape as `EventDetail`'s `Editability` and for the same standing rule: a control that
 * greys out without saying why leaves the user hunting for a permission they do not lack.
 */
export type AudienceEditability = { can: true } | { can: false; reason: string };

/** Nothing to explain: the ordinary case, allocated once rather than per render. */
const CAN_EDIT: AudienceEditability = { can: true };

/**
 * Why a terminal event's audience is fixed.
 *
 * The middle sentence is `eventStatus.ts`'s, imported rather than rewritten: it is the sentence the
 * confirmation showed *before* the press, so an organizer who closed the event five minutes ago reads
 * the same claim again rather than a second wording that has to be reconciled with the first.
 *
 * The last sentence exists because the panel it appears on will usually be showing sections and no
 * students. That is not "the students were lost" — ADR-003 D-13 wrote them down as rows that are now
 * the denominator, and `GET /events/{id}/roster` is where they are enumerable. Saying so here means
 * the explanation sits next to the thing that needs explaining.
 */
const lockedByStatus = (status: string) =>
  `This event is ${status}, so its audience can no longer be changed. ${AUDIENCE_FROZEN} The sections ` +
  "below are the record of what was invited; the frozen list of individual students is on the event's " +
  "roster, not here.";

/**
 * A status this build has never heard of. It refuses the controls and says so as version skew rather
 * than as finality — the same split `eventStatus.ts` draws, and for the same reason: telling an
 * organizer their event is over when the build merely does not recognise its status is the one
 * confusion that costs them the event.
 *
 * Refusing rather than allowing is deliberate. The two known-safe statuses are an allow-list, so a
 * fifth status the server adds tomorrow lands on the cautious side — which is also the side the server
 * lands on, since a status outside `Draft`/`Open` is what raises `EventLocked`.
 */
const lockedByUnknownStatus = (status: string) =>
  `This event's status (“${status}”) is not one this admin build recognises, so it cannot tell whether ` +
  "the audience may be changed and does not offer to change it. This build and the API are probably " +
  "different versions.";

/**
 * `Draft` and `Open` may change the audience; `Closed` and `Cancelled` may not.
 *
 * Read from `isTerminal` rather than from a list of two status names written out here, so this cannot
 * disagree with the transition table — and so it inherits ADR-003 D-16's correction for free: the
 * freeze is a property of **terminality**, not of the status happening to be `Closed`. A build that
 * tested `status === "Closed"` would offer a cancelled event controls that can only earn a 409.
 */
export function audienceEditability(status: string): AudienceEditability {
  const known = knownStatus(status);
  if (known === undefined) return { can: false, reason: lockedByUnknownStatus(status) };
  return isTerminal(known) ? { can: false, reason: lockedByStatus(known) } : CAN_EDIT;
}

// ---------------------------------------------------------------------------------------------
// The 409 that arrives anyway
// ---------------------------------------------------------------------------------------------

/** `EventLocked` maps here, following the split ADR-003 D-18 records: 400 is wrong, 409 is refused. */
const HTTP_CONFLICT = 409;

/**
 * Whether this failure is the audience surface saying "not on an event in that state".
 *
 * It matters because `audienceEditability` above has already hidden the controls for every status this
 * build knows is locked — so a 409 reaching the user means the event's status **moved underneath
 * them**, in another tab or by another organizer, since this screen read it. That is a specific,
 * actionable fact, and `advise()` cannot produce it: to that taxonomy a 409 is an ordinary 4xx whose
 * advice is "correct what it says above, or send it again if the reason may have cleared" — and this
 * reason cannot clear, because both terminal statuses are one-way.
 *
 * Tested on `status` and `kind`, never on `title`/`detail`, which are prose the server rewords.
 */
export const isAudienceLocked = (error: unknown): boolean =>
  error instanceof ApiError && error.kind === "http" && error.status === HTTP_CONFLICT;

/** What to say when it arrives. Names the cause, because "conflict" names nothing a user can act on. */
export const AUDIENCE_LOCKED_ELSEWHERE =
  "This event was closed or cancelled after this screen loaded, so its audience is now fixed and the " +
  "change was refused. Nothing was applied. Reload the page to see the status it actually holds — " +
  "there is no way back out of either terminal status, so this will not clear on a retry.";

// ---------------------------------------------------------------------------------------------
// What one attach did
// ---------------------------------------------------------------------------------------------

/** English, for counts small enough that a rule beats a table. */
const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * What to tell the organizer after an attach — and the whole job is that **"already attached" reads
 * as already there, not as a failure.**
 *
 * `POST /events/{id}/attendees` is idempotent and its `Already` counters exist so that idempotency is
 * observable rather than merely true: a re-post answering `{ groupsAttached: 0, groupsAlreadyAttached:
 * 3 }` is the server confirming the earlier request landed. A UI that reported that as "nothing
 * happened" would leave the organizer pressing it a third time, and one that reported it as an error
 * would have them undoing a save that worked.
 *
 * `expected` is stated because it is the number the whole panel is about, and it comes back on the
 * write specifically so the denominator can move on screen without a follow-up read.
 */
export function attachSettledText(result: EventAudienceResult): string {
  const attached = result.groupsAttached + result.studentsAttached;
  const already = result.groupsAlreadyAttached + result.studentsAlreadyAttached;

  const parts: string[] = [];
  if (result.groupsAttached > 0) {
    parts.push(`${plural(result.groupsAttached, "section", "sections")} attached.`);
  }
  if (result.studentsAttached > 0) {
    parts.push(`${plural(result.studentsAttached, "student", "students")} attached.`);
  }
  if (already > 0) {
    parts.push(
      attached > 0
        ? `${plural(already, "was", "were")} already attached and ${already === 1 ? "was" : "were"} left as ${already === 1 ? "it is" : "they are"}.`
        : `${plural(already, "was", "were")} already attached, so nothing changed — the earlier save had landed.`,
    );
  }
  if (parts.length === 0) parts.push("Nothing was attached.");

  return `${parts.join(" ")} ${plural(result.expected, "student is", "students are")} now expected.`;
}

// ---------------------------------------------------------------------------------------------
// Copy the panel needs in one place
// ---------------------------------------------------------------------------------------------

/**
 * The empty state, which has to say **what to do** rather than "none" — an event with no audience has
 * `expected: 0`, which ADR-003 D-19 makes a real number rather than a missing one, and every student
 * who taps at it is a walk-in counted as `Unexpected`.
 */
export const NO_AUDIENCE_YET =
  "No sections are attached, so this event expects nobody: its attendance rate has no denominator, " +
  "and closing it would mark no one Absent. Add the sections whose students are expected to attend.";

/** The same fact for an event where nothing can be added any more, so it does not read as advice. */
export const NO_AUDIENCE_EVER =
  "No sections were ever attached to this event, so it expected nobody. Any attendance recorded " +
  "against it was a walk-in.";

/**
 * Why a frozen event lists sections and no students — **the single most misreadable thing on this
 * panel**, and the reason it is stated rather than left to an empty list.
 *
 * On a terminal event the per-student audience is exactly what was written down (ADR-003 D-13) and is
 * exactly what this endpoint does not return. An empty students list beneath a non-zero `expected` is
 * a page contradicting itself unless it says which of the two is the whole story.
 */
export const FROZEN_STUDENTS_ELSEWHERE =
  "Individually-attached students are not listed for an event whose audience is frozen. This is not " +
  "“nobody was attached”: the students expected at this event were written down when it reached this " +
  "status, and that frozen list is what the roster shows — the count beside it is the same number.";

// ---------------------------------------------------------------------------------------------
// What the panel's body should render
// ---------------------------------------------------------------------------------------------

/**
 * How the students half of the panel is rendered — three different facts that look alike.
 *
 * `"list"` — there are individually-attached students to show.
 * `"none-attached"` — a **live** event with nobody attached individually. Everyone expected comes
 *   from the sections, which is ordinary and needs no explanation beyond saying so.
 * `"elsewhere"` — a **frozen** event, where the empty list is the contract's answer rather than an
 *   absence, and the per-student set is on `GET /events/{id}/roster` (ADR-003 D-13).
 */
export type StudentsSection = "list" | "none-attached" | "elsewhere";

/** What the panel's body is, once the audience has been read. */
export type AudienceListsState =
  /** Nobody was ever invited. The message differs by whether that can still be fixed. */
  | { kind: "nothing-invited"; message: string }
  /** There is an audience to render; `students` says how its second half reads. */
  | { kind: "lists"; students: StudentsSection };

/**
 * Which of the two the panel renders — **and the whole reason this is a function rather than two
 * conditions inline is the frozen case, where the obvious test is wrong.**
 *
 * The bug this exists to prevent, stated so it cannot be reintroduced by simplification:
 *
 * > On a frozen event `students` is **empty by contract**. So `groups.length === 0 &&
 * > students.length === 0` reduces to `groups.length === 0`, and a terminal event whose audience was
 * > individuals-only — attach a section and a student, remove the section, close — satisfies it while
 * > `expected` is 1. The panel then prints "expected nobody" directly beneath its own "1 expected"
 * > chip. Both cannot be true, and the false one is the reassuring one.
 *
 * **On a frozen event, `expected` is the only field that means anything about who was invited.**
 * `groups` is the historical record of *which cohort* (and can legitimately be empty when everyone
 * was named individually); `students` is empty whatever happened. So the frozen branch tests the
 * denominator, and it only calls an audience empty when the server says nobody is expected *and*
 * no cohort was recorded — the two independent ways an invitation leaves a trace.
 *
 * The live branch keeps the list-length test unchanged, and that asymmetry is correct rather than an
 * oversight: on a live event `students` really is the individually-attached set, so the two lists
 * together are the whole audience. This is the same live/frozen asymmetry ADR-003 D-15 records — the
 * two branches answer two different questions with two different sources.
 *
 * @param canEdit whether the audience can still be changed, which decides only whether the empty
 *   message gives instructions or states a fact. A frozen event is never told to add sections.
 */
export function audienceListsState(
  audience: EventAudience,
  canEdit: boolean,
): AudienceListsState {
  if (audience.isFrozen) {
    // Not a list length. See above.
    const nobodyWasInvited = audience.expected === 0 && audience.groups.length === 0;
    if (nobodyWasInvited) return { kind: "nothing-invited", message: NO_AUDIENCE_EVER };
    // `students` is empty by contract here, so "elsewhere" is the ordinary answer. A non-empty list
    // is still rendered rather than suppressed: it would mean the server changed its mind about that
    // contract, and showing what arrived beats hiding it behind a sentence saying it cannot exist.
    return {
      kind: "lists",
      students: audience.students.length > 0 ? "list" : "elsewhere",
    };
  }

  if (audience.groups.length === 0 && audience.students.length === 0) {
    return { kind: "nothing-invited", message: canEdit ? NO_AUDIENCE_YET : NO_AUDIENCE_EVER };
  }

  return {
    kind: "lists",
    students: audience.students.length > 0 ? "list" : "none-attached",
  };
}

/** Reads a section row's own label out of context, which is what a screen reader does with a button. */
export const removeSectionLabel = (name: string) =>
  `Remove section ${name} from this event's audience`;

/** As above, for an individually-attached student. */
export const removeStudentLabel = (fullName: string, studentNumber: string) =>
  `Remove ${fullName} (${studentNumber}) from this event's audience`;
