// A value that follows another one late, so that typing does not become a request per keystroke.
//
// It exists for the students grid's search box. That field used to filter an in-memory array, where
// re-running on every keystroke cost nothing; it now goes to `GET /students?search=`, where a
// ten-character surname typed at speed is ten requests, nine of whose answers are discarded — and
// the last one to *arrive* is not necessarily the last one *sent*, so an un-debounced field can
// settle on the results for a prefix of what is in the box.

import { useEffect, useState } from "react";

/**
 * @param value the value that changes as fast as the user types.
 * @param delayMs how long it must hold still before the returned value catches up.
 * @returns `value`, but only after it has stopped changing for `delayMs`.
 *
 * The first render returns `value` itself rather than an empty initial: a screen that mounts with a
 * search already in hand — restored from a URL, say — must not read the unfiltered list first and
 * then correct itself a moment later.
 *
 * The timer is cleared on every change, which is the whole mechanism: each keystroke cancels the
 * pending catch-up and starts a new one, so only a pause produces a value. It is also cleared on
 * unmount, so a screen navigated away from mid-type does not set state on a component that is gone.
 */
export function useDebounced<T>(value: T, delayMs: number): T {
  const [settled, setSettled] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  return settled;
}
