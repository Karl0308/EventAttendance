/** @vitest-environment happy-dom */

// `useApiPoll` — the hook that watches a detached roster import.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Four behaviours, and each of them is a bug that has a face:
//
//   1. **It stops.** A poll that keeps asking after the server said the run is over is a request
//      every two seconds for as long as the tab is open, on a screen nobody is watching any more.
//   2. **It keeps the last good answer.** This is the whole reason the hook exists rather than
//      `useApiResource`. One dropped GET during a six-minute import must not replace "phase 5 of 9"
//      with an error, because "an import is running and one request did not answer" and "the import
//      failed" are different facts and the operator will act on the second one.
//   3. **It pauses on a hidden tab and reads again the moment it is visible.** A background tab
//      polling for six minutes is requests nobody reads, and browsers throttle background timers to
//      an interval this hook is not choosing anyway. Coming back must show *now*, not whenever the
//      next tick lands.
//   4. **The interval is what it says it is.** Measured from the settling of one read, so a slow
//      server produces one request in flight rather than a queue of them.
//
// The reads below are plain promises rather than `fetch` stubs: the subject is the scheduling, and a
// transport in the way would only add a second thing that can make the timing wrong.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";

import { useApiPoll } from "../src/useApiPoll";

const INTERVAL_MS = 2_000;

interface Tick {
  n: number;
  done: boolean;
}

/** Whether `document.hidden` currently answers true. Rewritten by `setHidden` below. */
let hidden = false;

/**
 * `document.hidden` is a getter on the document, and happy-dom lets an own property shadow it — which
 * is the only way to drive this from a test, because there is no API for "pretend the tab went to the
 * background". The `visibilitychange` event is dispatched separately and deliberately: the browser
 * fires it *after* the flag has changed, and a hook that read the event instead of the document would
 * pass a test that fired them in the other order and fail in a browser.
 */
function setHidden(next: boolean): void {
  hidden = next;
  document.dispatchEvent(new Event("visibilitychange"));
}

beforeEach(() => {
  hidden = false;
  Object.defineProperty(document, "hidden", { configurable: true, get: () => hidden });
  vi.useFakeTimers();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  Reflect.deleteProperty(document, "hidden");
});

/** Flushes the promise the current read returned, without moving the clock. */
const settle = () => act(async () => {});

/** Moves the clock and flushes whatever that started. */
const advance = (ms: number) => act(async () => void vi.advanceTimersByTime(ms));

describe("useApiPoll", () => {
  it("stops polling once the answer says it is done", async () => {
    let calls = 0;
    const read = vi.fn(
      (): Promise<Tick> => Promise.resolve({ n: ++calls, done: calls >= 2 }),
    );

    const { result } = renderHook(() =>
      useApiPoll(read, [], { intervalMs: INTERVAL_MS, isDone: (tick: Tick) => tick.done }),
    );

    await settle();
    expect(read).toHaveBeenCalledTimes(1);
    expect(result.current.polling).toBe(true);

    // The second read answers `done`.
    await advance(INTERVAL_MS);
    expect(read).toHaveBeenCalledTimes(2);
    expect(result.current.polling).toBe(false);
    expect(result.current.data).toEqual({ n: 2, done: true });

    // And that is the last one, however long the tab is left open. Ten intervals, not one, because a
    // scheduler that fired once more before noticing would still pass a single-interval check.
    await advance(INTERVAL_MS * 10);
    expect(read).toHaveBeenCalledTimes(2);
  });

  it("keeps the last good answer when a poll fails, and counts the failures", async () => {
    let calls = 0;
    const boom = new Error("connection reset");
    const read = vi.fn((): Promise<Tick> => {
      calls += 1;
      // First read answers; every one after it fails. That is the shape of the incident this arm
      // exists for — data on screen, and then the network going away underneath it.
      return calls === 1 ? Promise.resolve({ n: 1, done: false }) : Promise.reject(boom);
    });

    const { result } = renderHook(() =>
      useApiPoll(read, [], { intervalMs: INTERVAL_MS, isDone: (tick: Tick) => tick.done }),
    );

    await settle();
    expect(result.current.status).toBe("ready");
    expect(result.current.data).toEqual({ n: 1, done: false });
    expect(result.current.consecutiveFailures).toBe(0);

    await advance(INTERVAL_MS);
    // The failure is rendered, and the data it was refreshing is still here. `useApiResource` would
    // have dropped it — see this hook's own header for why that is right there and wrong here.
    expect(result.current.status).toBe("stale");
    expect(result.current.data).toEqual({ n: 1, done: false });
    expect(result.current.error).toBe(boom);
    expect(result.current.consecutiveFailures).toBe(1);

    // And it keeps trying rather than becoming a single read that gave up.
    await advance(INTERVAL_MS);
    expect(result.current.consecutiveFailures).toBe(2);
    expect(result.current.data).toEqual({ n: 1, done: false });
  });

  it("reports a first-read failure as an error, because there is nothing to keep", async () => {
    const boom = new Error("connection reset");
    const read = vi.fn((): Promise<Tick> => Promise.reject(boom));

    const { result } = renderHook(() =>
      useApiPoll(read, [], { intervalMs: INTERVAL_MS, isDone: (tick: Tick) => tick.done }),
    );

    await settle();
    // No `stale` here: `stale` means "over data", and there has never been any. The failure is the
    // screen, exactly as `useApiResource` would have it.
    expect(result.current.status).toBe("error");
    expect(result.current.data).toBeUndefined();
    expect(result.current.error).toBe(boom);
  });

  it("polls on the interval it was given, measured from the last answer", async () => {
    const read = vi.fn((): Promise<Tick> => Promise.resolve({ n: 0, done: false }));

    renderHook(() =>
      useApiPoll(read, [], { intervalMs: INTERVAL_MS, isDone: (tick: Tick) => tick.done }),
    );

    await settle();
    expect(read).toHaveBeenCalledTimes(1);

    // One millisecond short. A hook that had scheduled on some other clock would already have fired.
    await advance(INTERVAL_MS - 1);
    expect(read).toHaveBeenCalledTimes(1);

    await advance(1);
    expect(read).toHaveBeenCalledTimes(2);
  });

  it("pauses while the tab is hidden and reads immediately when it comes back", async () => {
    const read = vi.fn((): Promise<Tick> => Promise.resolve({ n: 0, done: false }));

    const { result } = renderHook(() =>
      useApiPoll(read, [], { intervalMs: INTERVAL_MS, isDone: (tick: Tick) => tick.done }),
    );

    await settle();
    expect(read).toHaveBeenCalledTimes(1);
    expect(result.current.paused).toBe(false);

    await act(async () => setHidden(true));
    expect(result.current.paused).toBe(true);
    expect(result.current.polling).toBe(false);

    // Five intervals in the background and not one request. This is the assertion that would fail if
    // the pause only stopped *rendering* rather than stopping the schedule.
    await advance(INTERVAL_MS * 5);
    expect(read).toHaveBeenCalledTimes(1);

    // Back, and the read happens on the visibility change itself — no clock advanced. Waiting for the
    // next tick would show a two-second-old blank at the exact moment someone is reading it again.
    await act(async () => setHidden(false));
    expect(read).toHaveBeenCalledTimes(2);
    expect(result.current.paused).toBe(false);
    expect(result.current.polling).toBe(true);

    // And the ordinary schedule is running again afterwards, rather than the hook having done one
    // read and gone quiet.
    await advance(INTERVAL_MS);
    expect(read).toHaveBeenCalledTimes(3);
  });

  it("does not poll after the hook is unmounted", async () => {
    const read = vi.fn((): Promise<Tick> => Promise.resolve({ n: 0, done: false }));

    const { unmount } = renderHook(() =>
      useApiPoll(read, [], { intervalMs: INTERVAL_MS, isDone: (tick: Tick) => tick.done }),
    );

    await settle();
    expect(read).toHaveBeenCalledTimes(1);

    unmount();
    await advance(INTERVAL_MS * 5);
    expect(read).toHaveBeenCalledTimes(1);
  });
});
