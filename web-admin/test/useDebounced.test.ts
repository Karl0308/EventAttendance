/** @vitest-environment happy-dom */

// The delay between typing and asking.
//
// It exists for the students search box, which changed from filtering an array to calling
// `GET /students?search=`. Two of the three tests below are about the second half of that: not just
// "fewer requests" but *which* answer wins. An un-debounced field sends a request per keystroke and
// renders whichever reply arrives last, which is not necessarily the one for what is in the box —
// so the failure it prevents is a result set for a prefix, presented as the result for the whole.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";

import { useDebounced } from "../src/useDebounced";

const DELAY = 300;

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useDebounced", () => {
  it("returns the first value immediately", () => {
    // A screen that mounts with a search already in hand must not read the unfiltered list first and
    // correct itself a moment later.
    const { result } = renderHook(() => useDebounced("Santos", DELAY));

    expect(result.current).toBe("Santos");
  });

  it("holds the old value until the new one has stood still", () => {
    const { result, rerender } = renderHook(({ value }) => useDebounced(value, DELAY), {
      initialProps: { value: "" },
    });

    rerender({ value: "San" });
    expect(result.current).toBe("");

    act(() => {
      vi.advanceTimersByTime(DELAY - 1);
    });
    expect(result.current).toBe("");

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(result.current).toBe("San");
  });

  it("settles once on the last value, not once per change", () => {
    const { result, rerender } = renderHook(({ value }) => useDebounced(value, DELAY), {
      initialProps: { value: "" },
    });

    // Six keystrokes inside one delay window. Each cancels the pending catch-up, which is the whole
    // mechanism: only the pause produces a value.
    for (const value of ["S", "Sa", "San", "Sant", "Santo", "Santos"]) {
      rerender({ value });
      act(() => {
        vi.advanceTimersByTime(50);
      });
      expect(result.current).toBe("");
    }

    act(() => {
      vi.advanceTimersByTime(DELAY);
    });

    // The last value typed, not the first and not an intermediate one.
    expect(result.current).toBe("Santos");
  });

  it("does not settle after unmount", () => {
    const { result, rerender, unmount } = renderHook(({ value }) => useDebounced(value, DELAY), {
      initialProps: { value: "" },
    });

    rerender({ value: "Santos" });
    unmount();

    // The pending timer is cleared by the effect's cleanup. Left running it would set state on a
    // component that is gone — which React 19 does not throw for, so nothing would have said so.
    act(() => {
      vi.advanceTimersByTime(DELAY * 2);
    });

    expect(result.current).toBe("");
  });
});
