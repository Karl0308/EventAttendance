// One read from `api.ts`, as the three states a screen actually has to render — plus whether a
// further read is running over one of them.
//
// It exists because Students, Events and Dashboard each used to do `api.listX().then(setState)` with
// no rejection handler: a backend that was down left the initial empty array in place, so the screen
// said "there are no students" — the one reading of an outage that costs someone an afternoon. The
// point of this hook is that "failed" and "returned nothing" cannot collapse into each other, so
// `data` and `error` are never both present and never both absent.

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";

/** Server state only. UI state (search text, grid page) stays in the component. */
type Resource<T> =
  | { status: "loading"; data: undefined; error: undefined }
  | { status: "ready"; data: T; error: undefined }
  | { status: "error"; data: undefined; error: unknown };

const LOADING: Resource<never> = { status: "loading", data: undefined, error: undefined };

/**
 * A read in flight, and what is on screen while it runs. `refreshing` is deliberately NOT a fourth
 * `status`: making it one would put "has data" and "is re-reading" in the same slot, and every
 * consumer branching on `status === "ready"` would silently stop rendering its screen the moment a
 * refresh started — the blanking this exists to stop, moved into the type.
 *
 * `refreshing` is true only beside `ready`. Beside `loading` it would say nothing `status` does not
 * already say, and beside `error` there is nothing on screen to keep.
 */
interface InFlight {
  /** A re-read is running over data that is still rendered. Say so; do not unmount the screen. */
  refreshing: boolean;
}

/** Element-wise, as React compares an effect's dependency array. */
const sameSubject = (a: readonly unknown[], b: readonly unknown[]) =>
  a.length === b.length && a.every((value, i) => Object.is(value, b[i]));

/**
 * @param load reads the resource. Its identity does NOT decide when the read happens, so an inline
 *   arrow is fine and no `useCallback` is needed: the effect always calls the newest one it has been
 *   given, and `deps` alone says when to call it again.
 * @param deps what the read depends on — the values `load` closes over. `[]` for a load that takes
 *   nothing (`() => api.listEvents()`), `[id]` for one parameterised by a route param. As with any
 *   effect, the array's length must not change between renders.
 * @returns the resource plus `refreshing` and `reload`, the retry the error state offers.
 *
 * A `reload()` keeps the data already on screen and raises `refreshing` instead of dropping back to
 * `loading`. Blanking the screen for a re-read is not free: it destroys the focused control under the
 * user's finger (focus falls to `document.body`), resets grid scroll and paging, and flashes on every
 * press. A read for a *different* subject — `deps` changed — does still blank, because the data on
 * screen is not an older version of what is being read, it is a different event's.
 *
 * `load` used to be the dependency itself, guarded only by a line of prose asking callers to keep it
 * stable. That held for as long as every caller was a module-scope function, and would have failed
 * on the first parameterised one: `useApiResource(() => api.eventDetail(id))` allocates a new arrow
 * per render, so the effect re-fires, sets state, re-renders, allocates again — an infinite refetch
 * loop that hammers the API and never settles. A dependency the caller must *remember* to control is
 * the same defect class this file exists to close, one level up. Naming `deps` makes it a decision
 * the compiler asks for at every call site rather than a footnote in a doc comment.
 *
 * The raw `error` is handed back rather than a message, so the renderer can branch on `ApiError`'s
 * machine-readable `kind`/`status`/`code` and produce the text with `describeApiError`. Branching on
 * `title`/`detail` is prose-matching and breaks the moment the server rewords a message.
 */
export function useApiResource<T>(
  load: () => Promise<T>,
  deps: readonly unknown[],
): Resource<T> & InFlight & { reload: () => void } {
  // One state, not two. `status`/`data`/`error` and `refreshing` change together on every transition,
  // and holding them apart would let a render observe a half-applied one — "ready with the old data,
  // no longer refreshing" is a lie that lasts a frame and is exactly the kind this file refuses.
  const [state, setState] = useState<Resource<T> & InFlight>({ ...LOADING, refreshing: false });
  const [attempt, setAttempt] = useState(0);

  // The read is reached through a ref so that re-creating it never re-runs the effect. Assigned in a
  // layout effect rather than during render: layout effects are flushed before passive ones, so the
  // effect below always sees the load belonging to the commit it runs for, and render itself stays
  // free of the mutation — which under concurrent rendering may be discarded, replayed, or thrown
  // away for a render that never commits.
  const latestLoad = useRef(load);
  useLayoutEffect(() => {
    latestLoad.current = load;
  });

  // What the data currently on screen was read for. Compared rather than merely stored: a re-read of
  // the same subject may keep that data on screen, a read of a different one may not.
  const shownFor = useRef(deps);

  useEffect(() => {
    let live = true;

    const sameAsShown = sameSubject(shownFor.current, deps);
    shownFor.current = deps;

    // The updater form is what makes this a decision about the *committed* state rather than about
    // whatever this closure captured, and it keeps `state` out of the dependency array — where it
    // would re-fire this effect on its own result, forever.
    setState((current) =>
      sameAsShown && current.status === "ready"
        ? { ...current, refreshing: true }
        : { ...LOADING, refreshing: false },
    );

    // `load` is *called* inside the try, so a caller that throws synchronously — an arrow that is not
    // `async` and blows up before it ever returns a promise — lands in the same rendered error state
    // as a rejection. Left to propagate, it would escape this effect callback rather than reach the
    // handler below, stranding the state at whatever the line above just set: `loading`, or `ready`
    // with `refreshing` raised, with nothing left running to clear it. There is no error boundary
    // anywhere in `src/`, so React 19 unmounts the tree to a blank page instead of showing what failed.
    //
    // No caller does this today; every one of them is an arrow returning an `async` call. But the
    // contract this hook publishes is `() => Promise<T>`, and a contract the caller must remember to
    // honour is the same defect class the `deps` argument above exists to close.
    let read: Promise<T>;
    try {
      read = latestLoad.current();
    } catch (cause: unknown) {
      read = Promise.reject(cause);
    }

    read.then(
      (data) => {
        if (live) setState({ status: "ready", data, error: undefined, refreshing: false });
      },
      (cause: unknown) => {
        // Not a swallow: the failure becomes the rendered `error` state. `live` is false only when
        // this result belongs to a screen that is gone or to a superseded attempt, and the console
        // line keeps even that from vanishing without trace. `debug`, not `error` — navigating away
        // from a loading screen during an outage is ordinary, and a red console entry for ordinary
        // behaviour teaches people to scroll past the console.
        //
        // A failed re-read drops the data it was refreshing. Keeping it would mean `data` and `error`
        // both present — the one shape this file exists to forbid — and the softer reading of that,
        // "stale rows plus a warning banner", is only honest for as long as every screen remembers to
        // render the banner. A type cannot make four pages remember. So the failure wins the screen,
        // says what failed, and offers Retry; the context that costs is real, and it is the cheaper
        // of the two, because the alternative's failure mode is a user acting on numbers that are
        // quietly wrong.
        if (live) setState({ status: "error", data: undefined, error: cause, refreshing: false });
        else console.debug("EAMS: discarded a failed read from an unmounted or superseded screen", cause);
      },
    );

    return () => {
      live = false;
    };
    // `deps` is spread rather than passed as one element: an array literal is a fresh identity every
    // render, so `[attempt, deps]` would re-fetch forever — the exact bug this argument exists to
    // prevent. Spreading compares the caller's values one by one, as React expects.
    //
    // The suppression is narrow and is not hiding a missed dependency: exhaustive-deps checks that an
    // effect's dependency array matches what its body closes over, and it cannot do that for a
    // generic hook whose dependencies are the *caller's* by construction. The rule reports the spread
    // itself as unanalysable, not a wrong list. What the rule would otherwise catch here — `load`
    // being read but not listed — is deliberate and is the point of the ref above.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [attempt, ...deps]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  return { ...state, reload };
}
