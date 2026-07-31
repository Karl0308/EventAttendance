// Which status an event may move to, what pressing that costs, and why there is no way back.
//
// A module rather than a menu built inline in `EventDetail`, for the reason `eventDraft.ts` records
// about §4.5's limits: this is a **mirror of a server rule**, and a mirror that lives at its one call
// site is a mirror nobody can find when the original changes. The original is
// `EventStatusTransition` in `EAMS.Domain/EventStatusTransition.cs` — `AllowedTargets` field for
// field — and the server is still the authority. Nothing here decides anything; it decides what to
// *offer*, which is a different job, and getting it wrong produces a control that can only ever
// return a 400.
//
// It is deliberately free of React and of `api.ts`: pure values and pure functions, which is also what
// makes it testable when a runner lands (owed before D2).
//
// ---------------------------------------------------------------------------------------------
// The graph
// ---------------------------------------------------------------------------------------------
//
//   Draft     → Open, Cancelled
//   Open      → Closed, Cancelled
//   Closed    → (terminal, no edges out)
//   Cancelled → (terminal, no edges out)
//
// Every edge is one-way. There is no Open → Draft and no way out of either terminal status, so this
// table has no reverse of anything in it — which is why each entry below carries its own `finality`
// sentence rather than the UI deciding case by case which presses are undoable.
//
// **Why `Closed` is terminal**, since it is the question a user will ask and the answer is not
// arbitrary: closing materialises an `Absent` record for every expected student who has none, and that
// is what fixes the event's denominator. Re-opening would have to either delete those rows —
// destroying records an organizer may since have corrected by hand, and taking the absentee list with
// them — or keep them, leaving an event that claims to be live while carrying a roster that stopped
// moving. Both break the guarantee the freeze exists to give, and they break it invisibly. So the
// graph has no edge out, and no UI can offer one. Correcting an individual student afterwards is
// `POST /attendance/manual`, which is audited and does not move the denominator.
//
// **Why `Draft → Closed` is refused**: an event that never opened recorded nothing, so closing it
// would mark its whole audience `Absent` — an absentee list for a thing that never happened.
// `Cancelled` is the honest terminal state for that, and it is reachable.

import { EVENT_STATUS } from "./types";
import type { EventStatusName } from "./types";

/**
 * One reachable move, and everything a confirmation needs in order to say what it will do.
 *
 * The prose is here and not in the dialog because it is **per transition, not per target**:
 * `Draft → Cancelled` and `Open → Cancelled` reach the same status and do different things — the
 * second one is cancelling an event that may already hold taps. A dialog that keyed its wording on
 * the target alone would tell one of those two the other's story.
 */
export interface StatusChange {
  /** The status this moves the event to — the body of `PATCH /events/{id}/status`. */
  readonly target: EventStatusName;
  /** The action, as a verb. Titles the confirmation and labels the buttons on both sides of it. */
  readonly verb: string;
  /** The same action while the request is in flight. Spelled out rather than derived from `verb`. */
  readonly progress: string;
  /**
   * What this press actually does to the data — stated **before** the press, because two of the four
   * write rows in bulk and all four are one-way. "Are you sure?" is not a description of any of them.
   */
  readonly consequence: string;
  /** Why it cannot be taken back. Every edge in this graph is one-way, so every entry has one. */
  readonly finality: string;
}

/**
 * Said the same way wherever an audience is written down, because it is the same write.
 *
 * Exported since the audience panel landed: `eventAudience.ts` tells a terminal event's organizer why
 * the sections can no longer be changed, and that is the same fact this sentence states before the
 * press. Two sentences for one write is how the confirmation and the panel come to disagree about
 * what closing an event did.
 */
export const AUDIENCE_FROZEN =
  "Everyone currently expected is written down as this event's audience, and that list stops moving: " +
  "a student enrolled into an attached section afterwards is no longer counted.";

const CHANGES_FROM: Readonly<Record<EventStatusName, readonly StatusChange[]>> = {
  [EVENT_STATUS.Draft]: [
    {
      target: EVENT_STATUS.Open,
      verb: "Open",
      progress: "Opening…",
      consequence:
        "The event goes live. Card taps start being recorded against it, and its expected roster " +
        "stays live too — a student enrolled between now and the event is still expected.",
      finality:
        "There is no way back to Draft. An event that was published and then called off is " +
        "Cancelled, not un-published, because taps can exist by then and “not yet published” would " +
        "stop being true of it.",
    },
    {
      target: EVENT_STATUS.Cancelled,
      verb: "Cancel",
      progress: "Cancelling…",
      consequence:
        `The event is recorded as not having happened. ${AUDIENCE_FROZEN} Nobody is marked Absent ` +
        "and no attendance is recorded — nobody was expected to attend an event that did not happen.",
      finality:
        "There is no way back from Cancelled. Who was invited to an event that did not happen is a " +
        "historical fact rather than a working list, so EAMS keeps it and does not reopen it.",
    },
  ],

  [EVENT_STATUS.Open]: [
    {
      target: EVENT_STATUS.Closed,
      verb: "Close",
      progress: "Closing…",
      consequence:
        "Every expected student with no attendance record is marked Absent, in one press — that can " +
        `be hundreds of rows. ${AUDIENCE_FROZEN} Together those fix this event's denominator and its ` +
        "absentee list permanently: its attendance rate stops changing when the roster does.",
      finality:
        "There is no way back from Closed. Reopening would have to either delete the Absent records " +
        "this writes — including any an organizer had since corrected by hand — or keep them, leaving " +
        "an event that claims to be live while carrying a roster that stopped moving. To correct one " +
        "student afterwards, their attendance is overridden individually and on the record; the event " +
        "is not reopened.",
    },
    {
      target: EVENT_STATUS.Cancelled,
      verb: "Cancel",
      progress: "Cancelling…",
      consequence:
        `The event is recorded as not having happened. ${AUDIENCE_FROZEN} Taps already recorded ` +
        "against it are kept exactly as they are, and nobody else is marked Absent — nobody was " +
        "expected to attend an event that did not happen.",
      finality:
        "There is no way back from Cancelled, and this event is already Open, so the taps it has " +
        "taken stay on the record under a cancelled event.",
    },
  ],

  [EVENT_STATUS.Closed]: [],
  [EVENT_STATUS.Cancelled]: [],
};

/**
 * The wire's status as one this client has a rule for, or nothing. `EventItem.status` is `string`
 * because a response cannot prove a union; this is where that becomes a decision instead of an
 * assumption — the same job `knownMode` does in `eventDraft.ts`.
 */
export const knownStatus = (wire: string): EventStatusName | undefined =>
  Object.values(EVENT_STATUS).find((status) => status === wire);

/** No status is reachable from here. `Closed` and `Cancelled`. */
export const isTerminal = (status: EventStatusName): boolean => CHANGES_FROM[status].length === 0;

/** Why a terminal event offers nothing, said out loud — an absent control is as silent as a dead one. */
const terminalNotice = (status: EventStatusName) =>
  `This event is ${status}, and that is final: there is no transition out of ${status}, so it cannot ` +
  "be reopened and this app does not offer one. A single student's attendance can still be corrected " +
  "afterwards — one row at a time, attributed, and without moving the event's denominator — but that " +
  "is a server operation this admin app does not expose yet.";

/**
 * A status this build has never heard of. It offers nothing, and says so as a version skew rather
 * than as finality: mirroring the server's own answer here — `AllowedFrom` returns an empty list for
 * an unknown status rather than guessing a menu — but not mirroring its silence about why.
 */
const unrecognisedNotice = (status: string) =>
  `This event's status (“${status}”) is not one this admin build recognises, so it cannot say which ` +
  "changes are possible and offers none. This build and the API are probably different versions.";

/** What the event screen may offer, and — when that is nothing — why. */
export interface StatusActions {
  /** Reachable targets, in the order they are offered. Empty for a terminal or unknown status. */
  readonly changes: readonly StatusChange[];
  /** Why there are none. `undefined` exactly when `changes` is non-empty. */
  readonly noneBecause: string | undefined;
}

export function statusActionsFor(status: string): StatusActions {
  const known = knownStatus(status);
  if (known === undefined) return { changes: [], noneBecause: unrecognisedNotice(status) };

  const changes = CHANGES_FROM[known];
  return changes.length > 0
    ? { changes, noneBecause: undefined }
    : { changes, noneBecause: terminalNotice(known) };
}

/**
 * What to tell the user after the server has moved the event — **composed here, and it should not
 * have to be.**
 *
 * `EventService.ChangeStatusAsync` builds a far better sentence than this one and puts a count in it
 * that no client can compute: *"Event is now Closed. 42 expected attendees with no record were marked
 * Absent; this event's denominator and absentee list are now fixed."* That sentence does not reach the
 * wire. `EventsController.ChangeStatus` answers `Ok(response.Event)`, so on success only the
 * `EventDto` is serialised and `EventWriteResponse.Message` is dropped; the contract's 200 schema is
 * `EventDto` and carries no message field. It survives only on a *failure*, as `detail` — which
 * `httpError` ranks first, so refusals do read in the server's own words.
 *
 * So this says what is true without the number, rather than inventing one. The count cannot be
 * recovered from a re-read either: `EventSummaryDto.absent` is the event's whole absentee total, which
 * includes rows that were already there, and differencing it across the write would be a guess with a
 * race in it.
 *
 * Keyed on the status the server **came back with**, never on the one that was requested. The two
 * cannot differ today — the no-op arm is reached only when they are already equal — so this is not
 * defending against a case that exists; it is refusing to depend on that staying true, and the
 * returned value is the only one that is server truth.
 *
 * What actually makes this safe on a retry is the wording, not the keying. A request that lands on
 * the no-op arm did no work, and this still says every expected student with no record **has been**
 * marked Absent. That is true because the sentence describes the *event's state*, not what this
 * request did — the earlier attempt materialised them. Keep it in that tense; an action-describing
 * rewrite ("marked 42 students Absent") would narrate a freeze that did not run.
 */
export function statusSettledText(name: string, status: string): string {
  const known = knownStatus(status);
  const event = `“${name}” is now ${status}.`;

  if (known === EVENT_STATUS.Open) {
    return `${event} Card taps are recorded against it from now on.`;
  }
  if (known === EVENT_STATUS.Closed) {
    return (
      `${event} Every expected student with no record has been marked Absent, and its attendance ` +
      "figures are now fixed."
    );
  }
  if (known === EVENT_STATUS.Cancelled) {
    return `${event} Its audience has been written down and no longer moves.`;
  }
  // Draft, or a status this build does not know: state the fact and claim nothing about what it did.
  return event;
}
