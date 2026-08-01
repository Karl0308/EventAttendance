// One write through `api.ts`, as the four states a form actually has to render — plus a `run` a
// submit handler can await.
//
// `useApiResource`'s sibling, and deliberately not the same hook. A read is owned by the screen: it
// starts on mount, re-runs when its subject changes, and its result *is* that screen's server state.
// A write is owned by the user: it never starts on its own, it happens at most once per invocation,
// and its result is an outcome to act on rather than a thing the screen is about. Folding the two
// together would hand every form a `deps` array that must never fire and a first paint that claims
// to be loading something nobody asked for.
//
// What it does inherit is the discipline: "failed" and "returned nothing" cannot collapse into each
// other. `data` and `error` are never both present and never both absent.

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";

/**
 * Where one invocation got to. `idle` is not `running` with nothing in it and `succeeded` is not
 * `idle` again — a form that could not tell those apart would either re-enable its button before the
 * server answered, or leave it disabled forever after it did.
 */
type Mutation<T> =
  | { status: "idle"; data: undefined; error: undefined }
  | { status: "running"; data: undefined; error: undefined }
  | { status: "succeeded"; data: T; error: undefined }
  | { status: "failed"; data: undefined; error: unknown };

const IDLE: Mutation<never> = { status: "idle", data: undefined, error: undefined };
const RUNNING: Mutation<never> = { status: "running", data: undefined, error: undefined };

/**
 * What one `run` settled as, returned as a value and never as a rejection.
 *
 * The state above is for *rendering*; this is for *sequencing* — closing the dialog, re-reading the
 * list. Reading that from the state instead would mean an effect watching `status` for the moment it
 * turns `succeeded`, which is derived state routed through the render loop: it fires again on any
 * re-render that changes the effect's inputs, and it cannot tell one success from the next.
 *
 * A rejection would be the other obvious shape and is worse: `onSubmit` handlers drop the promise
 * they return, so a caller that forgot one `.catch` would turn a refused write into an unhandled
 * rejection and show the user nothing at all.
 *
 * `ignored` is its own arm rather than a `failed` carrying an invented error. Nothing was sent and
 * nothing changed; a caller that read it as a failure would tell the user their event was refused
 * while their first submit was still in flight.
 */
export type MutationOutcome<T> =
  | { outcome: "succeeded"; data: T }
  | { outcome: "failed"; error: unknown }
  | { outcome: "ignored" };

/**
 * @param perform sends the write. Its identity does not matter — it is reached through a ref, so an
 *   inline arrow is fine and `run` keeps one identity for the life of the component.
 * @returns the mutation state plus `run`, which never rejects and answers with a `MutationOutcome`.
 *
 * There is no `deps` argument and no automatic re-run: a write happens because someone pressed a
 * button, never because a value changed. That also settles the lint question — `.oxlintrc.json`
 * registers `^useApiResource$` with `react-hooks/exhaustive-deps` because that hook takes a
 * caller-supplied dependency array. `useApiMutation` takes none and must NOT be added to that regex:
 * the rule would start auditing an argument list that has no dependency array in it.
 *
 * `reset` exists because the hook now outlives the form it renders. It did not need to when the one
 * caller was the dialog itself — every open mounted a fresh component, so `idle` was where it always
 * started. `Events.tsx` owns the write now, precisely so that dismissing the dialog no longer throws
 * the outcome away, and the page survives the dialog: without a reset, a failure from an attempt the
 * user walked away from would still be here when they open the form again, greeting them as if it
 * were about the draft they have not typed yet.
 *
 * The raw `error` is handed back rather than a message, for the reason `useApiResource` records: the
 * renderer branches on `ApiError`'s machine-readable `kind`/`status`/`code` through `advise()` and
 * produces the text with `describeApiError`.
 */
export function useApiMutation<A extends readonly unknown[], T>(
  perform: (...args: A) => Promise<T>,
): Mutation<T> & { run: (...args: A) => Promise<MutationOutcome<T>>; reset: () => void } {
  const [state, setState] = useState<Mutation<T>>(IDLE);

  // The same layout-effect ref `useApiResource` uses, and for the same reason: assigning during
  // render is a mutation React is free to discard, replay, or throw away for a render that never
  // commits, while layout effects are flushed before passive ones and before any handler can fire.
  const latest = useRef(perform);
  useLayoutEffect(() => {
    latest.current = perform;
  });

  /**
   * The double-submit guard, and it is a ref rather than a read of `state.status` on purpose.
   *
   * `state` is the *committed* value. Two clicks inside one React batch — a double-click, or an
   * Enter keypress landing on the same tick as a click — both observe `idle` and both send.
   * `disabled` on the button has exactly the same hole, because it too only takes effect on the next
   * commit. A ref changes on the line that reads it, so it is the only one of the three that closes
   * the race rather than narrowing it. The button is still disabled while running; that is for the
   * user's benefit, not for correctness.
   *
   * A second invocation is *dropped*, not queued and not allowed to supersede. `POST /events` is not
   * idempotent and carries no client-supplied key the way the tap flow does with `deviceTapId`:
   * running both creates two events, and letting the last win creates two and reports one.
   */
  const inFlight = useRef(false);

  // Assigned in the effect body as well as the cleanup, because StrictMode mounts, unmounts and
  // remounts in development — a cleanup-only `false` would leave the second mount believing it was
  // already gone and silently stop rendering every result.
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const run = useCallback(async (...args: A): Promise<MutationOutcome<T>> => {
    if (inFlight.current) return { outcome: "ignored" };
    inFlight.current = true;
    setState(RUNNING);

    try {
      // `perform` is *called* inside the try, so a caller that throws synchronously — an arrow that
      // is not `async` and blows up before it ever returns a promise — lands in the same rendered
      // `failed` state as a rejection instead of escaping into a submit handler that drops it.
      const data = await latest.current(...args);
      if (mounted.current) setState({ status: "succeeded", data, error: undefined });
      // The outcome is still returned to the caller: `run` answers the invocation, and whether the
      // form that made it is still on screen is the caller's business, not this hook's.
      //
      // **`data` is deliberately not logged, and that omission is the point rather than an oversight.**
      // This hook is generic over every write in the app, and one of them — `registerDevice` /
      // `regenerateDeviceKey` — answers with the only plaintext copy of a device credential
      // (`DeviceKeyIssued.apiKey`). Unmounted is exactly the case where that copy is gone from the UI,
      // so a console line carrying it would make devtools history the sole holder of a live key. The
      // fact worth recording here is *that* a write landed with nobody watching; the payload adds no
      // diagnosis the status does not already give, and no future caller can opt out of this line.
      else console.debug("EAMS: a write succeeded after the form that sent it was gone");
      return { outcome: "succeeded", data };
    } catch (error: unknown) {
      // Not a swallow. The failure becomes the rendered `failed` state *and* the returned outcome,
      // and when there is no longer a screen to render it, the console line keeps it from vanishing
      // without trace. `debug` rather than `error`, as in `useApiResource`: navigating away from a
      // form mid-submit is ordinary, and red console entries for ordinary things teach people to
      // scroll past the console.
      if (mounted.current) setState({ status: "failed", data: undefined, error });
      else console.debug("EAMS: a write failed after the form that sent it was gone", error);
      return { outcome: "failed", error };
    } finally {
      // Cleared however the write settled, including the unmounted case: a remount of the same
      // component gets a fresh ref anyway, and leaving it raised would wedge the guard shut if this
      // hook is ever used somewhere the form survives its own submit.
      inFlight.current = false;
    }
  }, []);

  /**
   * Back to `idle`, for a caller that reopens a form the last attempt left a failure on.
   *
   * Refused while a write is in flight, and that refusal is not a swallowed error — it is the only
   * consistent answer. Clearing the rendered state would say `idle` while `inFlight` is still raised,
   * so the very next submit would be dropped as a duplicate against a state claiming nothing is
   * running: a button that looks ready, does nothing, and says nothing. A caller that reopens its
   * form mid-write keeps seeing `running`, which is what is actually true of it.
   */
  const reset = useCallback(() => {
    if (inFlight.current) return;
    setState(IDLE);
  }, []);

  return { ...state, run, reset };
}
