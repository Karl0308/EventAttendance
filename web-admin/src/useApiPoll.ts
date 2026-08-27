// One read from `api.ts`, repeated until it says it is finished — and, crucially, **not**
// `useApiResource`.
//
// ---------------------------------------------------------------------------------------------
// Why this is a second hook and not an option on the first
// ---------------------------------------------------------------------------------------------
//
// `useApiResource`'s union has one arm for failure: `{ status: "error"; data: undefined }`. Its own
// note explains the choice — "the failure wins the screen" — and for a page that is right: `data` and
// `error` both present is a shape that lets four screens each forget to render the warning banner, and
// a user acting on numbers that are quietly stale is worse than a user looking at a Retry button.
//
// A poll inverts every term of that trade. The subject here is a roster import that takes minutes, and
// the screen is watching it because there is nothing else to look at. One blipped request — a Wi-Fi
// handover, a proxy hiccup, an app-pool recycle — would blank the operator's progress and put an error
// where "phase 5 of 9" was. The screen would be saying *the import failed*. Nothing failed: a single
// GET did not answer, and the very next one, two seconds later, will. The stale data is not quietly
// wrong here, it is the last true thing the server said, and it is far more useful than its absence.
//
// So this hook keeps last-good data across a failed poll and puts the failure **beside** it in a
// `stale` arm the type forces a consumer to notice. It also counts consecutive failures, because "one
// poll missed" and "nothing has answered for a minute" are different sentences and only the second one
// is allowed to say the outcome is unknown.
//
// ---------------------------------------------------------------------------------------------
// Why polling pauses on a hidden tab, and why it resumes with a read rather than a timer
// ---------------------------------------------------------------------------------------------
//
// A background tab polling every two seconds for six minutes is 180 requests nobody is reading, and
// browsers throttle background timers unpredictably anyway — so the interval an unattended tab
// *believes* it is keeping is not one it is keeping. Pausing on `document.hidden` is the honest
// version. Coming back, the first thing an operator wants is the current state, not the state as of
// whenever the timer happens to land, so becoming visible fires a read immediately.

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";

/**
 * Where the poll is, and what may be rendered from it.
 *
 * Four arms rather than `useApiResource`'s three, and the fourth is the whole point:
 *
 *   - `loading` — the first read has not answered yet. Nothing to show.
 *   - `ready` — the last read answered. This is the ordinary state of a running poll.
 *   - `stale` — the last read **failed** over data an earlier one returned. Both are present, and a
 *     consumer must render the data and say the reading is not current. This is the arm that stops a
 *     dropped packet from looking like a failed import.
 *   - `error` — a read failed and there has never been any data. Nothing to keep, so the failure is
 *     the screen, exactly as `useApiResource` would have it.
 *
 * `data` is present on `ready` and `stale` and absent on the other two, so `poll.data !== undefined`
 * narrows to "there is something to render" without asking which of the two it is.
 */
export type Poll<T> =
  | { status: "loading"; data: undefined; error: undefined }
  | { status: "ready"; data: T; error: undefined }
  | { status: "stale"; data: T; error: unknown }
  | { status: "error"; data: undefined; error: unknown };

const LOADING: Poll<never> = { status: "loading", data: undefined, error: undefined };

/** How a poll ran and whether it still is. Beside the union rather than inside it — see `Poll`. */
export interface PollProgress {
  /**
   * How many reads in a row have failed, reset to 0 by any success.
   *
   * The number rather than a boolean, because the two sentences a screen owes are graded: one failed
   * poll is "could not reach the API just then, still trying", and a dozen is "this screen cannot see
   * the run at all, and whether it is still going is unknown from here". A boolean can only say the
   * first, or only the second, and both of those are wrong half the time.
   */
  consecutiveFailures: number;
  /** Reads are still being scheduled. False once `isDone` said so, and false while paused. */
  polling: boolean;
  /** The tab is hidden, so nothing is being scheduled. A rendered fact, not a silent one. */
  paused: boolean;
}

/** What the caller has to decide, since none of it can be guessed from a `Promise<T>`. */
export interface PollOptions<T> {
  /** Milliseconds between the settling of one read and the start of the next. */
  intervalMs: number;
  /**
   * Whether this answer is the last one — the poll stops on `true` and never restarts by itself.
   *
   * Measured from the *data*, not from an elapsed time or an attempt count: the server owns the
   * question "is this over?", and a client that decides it locally will eventually stop watching a run
   * that is still going, or watch a finished one forever.
   */
  isDone: (data: T) => boolean;
}

const isHidden = (): boolean =>
  // Guarded rather than assumed. This hook is imported by modules the `node`-environment half of the
  // suite also loads, and a bare `document.hidden` there is a `ReferenceError` at import time.
  typeof document !== "undefined" && document.hidden;

/**
 * @param read performs one poll. Its identity does not decide anything — it is reached through a ref,
 *   so an inline arrow is fine and no `useCallback` is needed. `deps` alone says what is being polled.
 * @param deps what `read` closes over, compared element-wise as React compares an effect's array. A
 *   change means a **different subject**: the poll restarts from `loading` and last-good data is
 *   dropped, because it is not an older version of the new subject, it is another one's.
 * @param options the interval and the stop condition. See `PollOptions`.
 * @returns the poll's state, how it is running, and `reload` — one immediate read, which also restarts
 *   a poll that had stopped. That is the retry an error state offers.
 *
 * Reads never overlap. The next one is scheduled from the *settling* of the last, not from a fixed
 * wall-clock grid, so a server that takes three seconds to answer a two-second poll produces one
 * request in flight at a time rather than a growing queue of them — which is the failure mode that
 * turns a slow API into an unreachable one.
 */
export function useApiPoll<T>(
  read: () => Promise<T>,
  deps: readonly unknown[],
  options: PollOptions<T>,
): Poll<T> & PollProgress & { reload: () => void } {
  const [state, setState] = useState<Poll<T>>(LOADING);
  const [consecutiveFailures, setConsecutiveFailures] = useState(0);
  const [polling, setPolling] = useState(true);
  const [paused, setPaused] = useState(isHidden);
  const [attempt, setAttempt] = useState(0);

  // The three caller-supplied values, reached through refs so that re-creating any of them never
  // re-runs the effect below. Assigned in a layout effect rather than during render, for the reason
  // `useApiResource` records: layout effects are flushed before passive ones, so the effect always
  // sees the values belonging to the commit it runs for, and render stays free of a mutation React is
  // free to discard, replay, or throw away for a render that never commits.
  const latest = useRef({ read, options });
  useLayoutEffect(() => {
    latest.current = { read, options };
  });

  useEffect(() => {
    let live = true;
    let done = false;
    let inFlight = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const clear = () => {
      if (timer !== undefined) clearTimeout(timer);
      timer = undefined;
    };

    const schedule = () => {
      clear();
      if (!live || done) return;
      // Nothing is scheduled while hidden. The visibility listener below is what starts it again, and
      // it does so with a read rather than a timer — see the module header.
      if (isHidden()) return;
      timer = setTimeout(() => void poll(), latest.current.options.intervalMs);
    };

    const poll = async (): Promise<void> => {
      // `inFlight` closes the one race this hook actually has: a `visibilitychange` (or a `reload`)
      // landing while a read is already out. Without it the two answers race and the older one can win
      // the render, which on a monotonic progress panel reads as the import going backwards.
      if (!live || done || inFlight) return;
      inFlight = true;

      let request: Promise<T>;
      try {
        // Called inside the try so a `read` that throws synchronously — an arrow that is not `async`
        // and blows up before it ever returns a promise — lands in the same rendered failure as a
        // rejection instead of escaping this effect and, with no error boundary anywhere in `src/`,
        // unmounting the tree to a blank page.
        request = latest.current.read();
      } catch (cause: unknown) {
        request = Promise.reject(cause);
      }

      try {
        const data = await request;
        if (!live) return;
        setConsecutiveFailures(0);
        setState({ status: "ready", data, error: undefined });
        if (latest.current.options.isDone(data)) {
          done = true;
          setPolling(false);
          clear();
        }
      } catch (cause: unknown) {
        // Not a swallow, and the distinction between the two arms below is the reason this hook
        // exists: over data, the failure is rendered *beside* what the server last said; with no data
        // behind it, the failure is the screen. Either way it is a rendered state and never a console
        // line alone.
        if (!live) {
          console.debug("EAMS: discarded a failed poll from an unmounted or superseded screen", cause);
          return;
        }
        setConsecutiveFailures((n) => n + 1);
        setState((current) =>
          current.data === undefined
            ? { status: "error", data: undefined, error: cause }
            : { status: "stale", data: current.data, error: cause },
        );
      } finally {
        inFlight = false;
      }

      // Outside the try/catch so it runs however the read settled. A poll that stopped scheduling
      // itself because one request failed is a poll that has silently become a single read.
      schedule();
    };

    const onVisibilityChange = () => {
      const hidden = isHidden();
      setPaused(hidden);
      if (hidden) {
        clear();
        setPolling(false);
        return;
      }
      if (done) return;
      setPolling(true);
      // Immediately, not on the next tick of a timer. The first thing someone coming back to the tab
      // wants is the state now, and a two-second-old blank is the one thing this screen must not show
      // at the moment it is being read again.
      void poll();
    };

    // The subject changed (or `reload` fired): back to `loading`, and last-good data goes with it.
    setState(LOADING);
    setConsecutiveFailures(0);
    setPolling(true);
    setPaused(isHidden());

    // The first read is immediate and happens even on a hidden tab — a cold mount is a page someone is
    // about to look at, and `hidden` only decides whether a *further* read is scheduled.
    void poll();

    if (typeof document !== "undefined") {
      document.addEventListener("visibilitychange", onVisibilityChange);
    }

    return () => {
      live = false;
      clear();
      if (typeof document !== "undefined") {
        document.removeEventListener("visibilitychange", onVisibilityChange);
      }
    };
    // `deps` is spread rather than passed as one element, for the reason `useApiResource` records: an
    // array literal is a fresh identity every render, so `[attempt, deps]` would restart the poll
    // forever. `options` is deliberately not a dependency — it is reached through the ref above, so a
    // caller may pass an object literal without re-arming the poll on every render.
    //
    // The suppression is narrow and hides no missed dependency: exhaustive-deps audits an effect
    // against what its body closes over, and it cannot do that for a generic hook whose dependencies
    // are the *caller's* by construction. It reports the spread as unanalysable, not as a wrong list.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt, ...deps]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  return { ...state, consecutiveFailures, polling, paused, reload };
}
