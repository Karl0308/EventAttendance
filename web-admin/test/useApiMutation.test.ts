/** @vitest-environment happy-dom */

// The double-submit guard in `src/useApiMutation.ts`, and the outcome contract around it.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS — and what it does not
// ---------------------------------------------------------------------------------------------
//
// The one target here that needs React, so it is the one file in the suite that pays for a DOM (via
// the docblock above; the other three run in plain Node). `happy-dom` rather than `jsdom` because
// nothing below queries or lays out a document — the hook is rendered, not a form — and jsdom's
// startup is the larger share of a run this small.
//
// These tests drive the hook through its public surface: `run`, `reset`, and the four rendered
// states. They do not reach into the refs. That is deliberate — `inFlight` being a ref rather than a
// read of `state.status` is the *implementation* of the guard, and a test asserting on it would pin
// the mechanism instead of the behaviour and would have to be rewritten by anyone improving it.
//
// **They do not test the button.** No form is rendered here, so "the submit button is disabled while
// running" is not covered; the hook's own comment is explicit that `disabled` narrows the race rather
// than closing it, and what closes it is what is tested below. Nor do they cover the unmounted-write
// paths (`console.debug`) or the route-change hole recorded against D3 — those are about state
// ownership above this hook, and MDVault #206 has them owed to jose-arch.

import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";

import { useApiMutation } from "../src/useApiMutation";
import type { MutationOutcome } from "../src/useApiMutation";

afterEach(cleanup);

// ---------------------------------------------------------------------------------------------
// A promise the test decides when to settle
// ---------------------------------------------------------------------------------------------

interface Deferred<T> {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
  readonly reject: (reason: unknown) => void;
}

/**
 * Built without a definite-assignment assertion: the executor runs synchronously, so the two
 * captures are always set by the time the guard below runs, and the guard is what narrows them.
 */
function deferred<T>(): Deferred<T> {
  let capturedResolve: ((value: T) => void) | undefined;
  let capturedReject: ((reason: unknown) => void) | undefined;

  const promise = new Promise<T>((resolve, reject) => {
    capturedResolve = resolve;
    capturedReject = reject;
  });

  if (capturedResolve === undefined || capturedReject === undefined) {
    throw new Error("The Promise executor did not run synchronously.");
  }

  return { promise, resolve: capturedResolve, reject: capturedReject };
}

const CREATED = "the created event";

// ---------------------------------------------------------------------------------------------

describe("useApiMutation", () => {
  it("starts idle, with neither data nor error", () => {
    const { result } = renderHook(() => useApiMutation(vi.fn()));

    expect(result.current.status).toBe("idle");
    expect(result.current.data).toBeUndefined();
    expect(result.current.error).toBeUndefined();
  });

  it("never starts on its own", async () => {
    const perform = vi.fn(() => Promise.resolve(CREATED));
    renderHook(() => useApiMutation(perform));

    // A write happens because someone pressed a button, never because a value changed. Flush the
    // effects a mount would run so the assertion is about the hook and not about timing.
    await act(async () => {});

    expect(perform).not.toHaveBeenCalled();
  });

  describe("the double-submit guard", () => {
    it("drops a second run issued in the same tick, and sends the write once", async () => {
      const gate = deferred<string>();
      const perform = vi.fn(() => gate.promise);
      const { result } = renderHook(() => useApiMutation(perform));

      let outcomes: MutationOutcome<string>[] = [];
      await act(async () => {
        // Both calls made before any await, so both land in one React batch — a double-click, or an
        // Enter keypress arriving on the same tick as a click. Neither `disabled` nor a read of
        // `state.status` closes this, because neither has committed yet.
        const first = result.current.run();
        const second = result.current.run();
        gate.resolve(CREATED);
        outcomes = await Promise.all([first, second]);
      });

      expect(perform).toHaveBeenCalledTimes(1);
      expect(outcomes[0]).toEqual({ outcome: "succeeded", data: CREATED });
      // `ignored`, not a `failed` carrying an invented error: nothing was sent and nothing changed,
      // so a caller must not tell the user their event was refused.
      expect(outcomes[1]).toEqual({ outcome: "ignored" });
    });

    it("drops the second run without disturbing the first one's rendered state", async () => {
      const gate = deferred<string>();
      const perform = vi.fn(() => gate.promise);
      const { result } = renderHook(() => useApiMutation(perform));

      let first: Promise<MutationOutcome<string>> | undefined;
      await act(async () => {
        first = result.current.run();
        await result.current.run();
      });

      expect(result.current.status).toBe("running");

      await act(async () => {
        gate.resolve(CREATED);
        await first;
      });

      expect(result.current.status).toBe("succeeded");
      expect(result.current.data).toBe(CREATED);
    });

    it("lifts the guard once the write settles, so a later submit goes through", async () => {
      // Per invocation, not a latch. A guard that stayed raised would wedge the form shut.
      const perform = vi.fn(() => Promise.resolve(CREATED));
      const { result } = renderHook(() => useApiMutation(perform));

      await act(async () => {
        await result.current.run();
      });
      await act(async () => {
        await result.current.run();
      });

      expect(perform).toHaveBeenCalledTimes(2);
    });

    it("lifts the guard after a failure too", async () => {
      const perform = vi.fn(() => Promise.reject(new Error("refused")));
      const { result } = renderHook(() => useApiMutation(perform));

      await act(async () => {
        await result.current.run();
      });
      await act(async () => {
        await result.current.run();
      });

      expect(perform).toHaveBeenCalledTimes(2);
    });
  });

  describe("what one run settles as", () => {
    it("renders running while in flight, then succeeded with the data", async () => {
      const gate = deferred<string>();
      const { result } = renderHook(() => useApiMutation(() => gate.promise));

      let settled: Promise<MutationOutcome<string>> | undefined;
      await act(async () => {
        settled = result.current.run();
      });

      expect(result.current.status).toBe("running");
      expect(result.current.data).toBeUndefined();
      expect(result.current.error).toBeUndefined();

      await act(async () => {
        gate.resolve(CREATED);
        await settled;
      });

      expect(result.current.status).toBe("succeeded");
      expect(result.current.data).toBe(CREATED);
      expect(result.current.error).toBeUndefined();
    });

    it("answers a rejection as a failed outcome rather than by rejecting", async () => {
      // A rejection would be the other obvious shape and is worse: `onSubmit` handlers drop the
      // promise they return, so a missing `.catch` would show the user nothing at all.
      const refused = new Error("the API refused it");
      const { result } = renderHook(() => useApiMutation(() => Promise.reject(refused)));

      let outcome: MutationOutcome<never> | undefined;
      await act(async () => {
        outcome = await result.current.run();
      });

      expect(outcome).toEqual({ outcome: "failed", error: refused });
      expect(result.current.status).toBe("failed");
      expect(result.current.error).toBe(refused);
      expect(result.current.data).toBeUndefined();
    });

    it("answers a synchronous throw the same way", async () => {
      // `perform` is called inside the try, so an arrow that blows up before returning a promise
      // lands in the same rendered `failed` state instead of escaping into the submit handler.
      const threw = new Error("threw before returning a promise");
      const { result } = renderHook(() =>
        useApiMutation(() => {
          throw threw;
        }),
      );

      // `MutationOutcome<unknown>`, not `<never>`: a `perform` that only ever throws returns
      // `never`, which gives the compiler nothing to infer the success type from.
      let outcome: MutationOutcome<unknown> | undefined;
      await act(async () => {
        outcome = await result.current.run();
      });

      expect(outcome).toEqual({ outcome: "failed", error: threw });
      expect(result.current.status).toBe("failed");
    });

    it("passes its arguments through to perform", async () => {
      const perform = vi.fn((id: string, count: number) => Promise.resolve(`${id}:${count}`));
      const { result } = renderHook(() => useApiMutation(perform));

      await act(async () => {
        await result.current.run("event-1", 3);
      });

      expect(perform).toHaveBeenCalledWith("event-1", 3);
    });

    it("keeps data and error from ever both being present", async () => {
      const { result } = renderHook(() => useApiMutation(() => Promise.resolve(CREATED)));

      await act(async () => {
        await result.current.run();
      });

      // "Failed" and "returned nothing" must not collapse into each other — the discipline this
      // hook inherits from `useApiResource`.
      expect(result.current.data !== undefined && result.current.error !== undefined).toBe(false);
    });
  });

  describe("reset", () => {
    it("returns a settled failure to idle", async () => {
      // The hook outlives the form now, so a failure from an attempt the user walked away from
      // would otherwise greet them over a draft they have not typed yet.
      const { result } = renderHook(() => useApiMutation(() => Promise.reject(new Error("refused"))));

      await act(async () => {
        await result.current.run();
      });
      expect(result.current.status).toBe("failed");

      act(() => {
        result.current.reset();
      });

      expect(result.current.status).toBe("idle");
      expect(result.current.error).toBeUndefined();
    });

    it("refuses while a write is in flight, so the guard and the rendered state cannot disagree", async () => {
      // Clearing to `idle` here would leave `inFlight` raised: the next submit would be dropped as
      // a duplicate against a state claiming nothing is running — a button that looks ready, does
      // nothing, and says nothing.
      const gate = deferred<string>();
      const { result } = renderHook(() => useApiMutation(() => gate.promise));

      let settled: Promise<MutationOutcome<string>> | undefined;
      await act(async () => {
        settled = result.current.run();
      });
      expect(result.current.status).toBe("running");

      act(() => {
        result.current.reset();
      });

      expect(result.current.status).toBe("running");

      await act(async () => {
        gate.resolve(CREATED);
        await settled;
      });

      expect(result.current.status).toBe("succeeded");
    });
  });
});
