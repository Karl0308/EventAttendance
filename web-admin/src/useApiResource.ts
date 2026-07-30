// One read from `api.ts`, as the three states a screen actually has to render.
//
// It exists because Students, Events and Dashboard each used to do `api.listX().then(setState)` with
// no rejection handler: a backend that was down left the initial empty array in place, so the screen
// said "there are no students" — the one reading of an outage that costs someone an afternoon. The
// point of this hook is that "failed" and "returned nothing" cannot collapse into each other, so
// `data` and `error` are never both present and never both absent.

import { useCallback, useEffect, useState } from "react";

/** Server state only. UI state (search text, grid page) stays in the component. */
type Resource<T> =
  | { status: "loading"; data: undefined; error: undefined }
  | { status: "ready"; data: T; error: undefined }
  | { status: "error"; data: undefined; error: unknown };

const LOADING: Resource<never> = { status: "loading", data: undefined, error: undefined };

/**
 * @param load must be stable across renders — a module-scope function, or wrapped in `useCallback`.
 *   It is an effect dependency, so a new identity each render re-fetches forever.
 * @returns the resource plus `reload`, the retry the error state offers.
 *
 * The raw `error` is handed back rather than a message, so the renderer can branch on `ApiError`'s
 * machine-readable `kind`/`status`/`code` and produce the text with `describeApiError`. Branching on
 * `title`/`detail` is prose-matching and breaks the moment the server rewords a message.
 */
export function useApiResource<T>(load: () => Promise<T>): Resource<T> & { reload: () => void } {
  const [resource, setResource] = useState<Resource<T>>(LOADING);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    setResource(LOADING);

    load().then(
      (data) => {
        if (live) setResource({ status: "ready", data, error: undefined });
      },
      (cause: unknown) => {
        // Not a swallow: the failure becomes the rendered `error` state. `live` is false only when
        // this result belongs to a screen that is gone or to a superseded attempt, and the console
        // line keeps even that from vanishing without trace. `debug`, not `error` — navigating away
        // from a loading screen during an outage is ordinary, and a red console entry for ordinary
        // behaviour teaches people to scroll past the console.
        if (live) setResource({ status: "error", data: undefined, error: cause });
        else console.debug("EAMS: discarded a failed read from an unmounted or superseded screen", cause);
      },
    );

    return () => {
      live = false;
    };
  }, [load, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  return { ...resource, reload };
}
