// Running a zone-dependent test in the zone it claims — and proving it, rather than trusting it.
//
// ---------------------------------------------------------------------------------------------
// Why this file exists at all
// ---------------------------------------------------------------------------------------------
//
// `eventDraft.ts` converts between a `datetime-local` wall-clock reading and an instant in **this
// process's local zone**. Every assertion about `localFrom`, `instantFrom` and `resolve` is therefore
// a statement about the zone the runner happens to be in, and a suite that does not control that is
// asserting nothing in particular.
//
// The trap is specific and was found by measurement, not by reading. On Windows, the shell's `TZ`
// **does not reach Node**:
//
//     $ TZ=America/New_York node -p "process.env.TZ"                  → undefined
//     $ TZ=America/New_York node -p "Intl.DateTimeFormat().resolvedOptions().timeZone"
//                                                                     → Asia/Manila
//
// So the ordinary `TZ=… vitest` incantation is silently dropped and the DST test below would run in
// the machine's own zone. It would still pass — a fall-back fold that is not in effect simply makes
// the scenario unreachable, and every assertion about it becomes vacuous. That is precisely the
// false-green this suite exists to prevent, so the zone is never assumed: it is set in-process and
// then checked.
//
// What *does* work, also measured: assigning `process.env.TZ` at runtime reaches ICU immediately,
// even after a `Date` has already been constructed, and is reversible.
//
//     process.env.TZ = "America/New_York";
//     new Date("2026-11-01T05:30:00Z").toString()
//       → Sun Nov 01 2026 01:30:00 GMT-0400 (Eastern Daylight Time)
//
// ---------------------------------------------------------------------------------------------

/**
 * The zone the whole suite runs in unless a test says otherwise.
 *
 * `Asia/Manila` rather than `UTC`, deliberately. UTC is the one zone where an offset error is
 * invisible — a sign slip in the local↔instant conversion is the identity there — so a suite pinned
 * to it would pass on code that shifts every event by eight hours. Manila is this school's actual
 * zone, is UTC+8, and has had no DST since 1978, so it is both realistic and deterministic.
 */
export const BASELINE_TIME_ZONE = "Asia/Manila";

/**
 * The zone with a fall-back fold in it. Used by the handful of tests that need a wall-clock reading
 * to name two different instants; `BASELINE_TIME_ZONE` cannot express that, which is the point.
 */
export const FOLD_TIME_ZONE = "America/New_York";

/**
 * Fails loudly when the zone did not take effect, naming both what was asked for and what is
 * actually in force.
 *
 * A throw rather than a skip. A skipped DST test and a passing one look identical in a summary line,
 * and the whole reason this helper exists is that the failure mode here is silence.
 */
export function assertTimeZoneInEffect(zone: string): void {
  const inForce = Intl.DateTimeFormat().resolvedOptions().timeZone;
  if (inForce !== zone) {
    throw new Error(
      `Time zone did not take effect: asked for ${zone}, ICU reports ${inForce}. ` +
        "Zone-dependent tests would run in the wrong zone and pass for the wrong reason.",
    );
  }
}

/**
 * Runs `body` with `zone` in force, and restores whatever was in force before — however `body`
 * settles.
 *
 * Synchronous by design. `process.env.TZ` is process-global, so an async body would leave the zone
 * changed for whatever ran in the gap. Vitest runs the tests within one file sequentially and each
 * file in its own worker, so a synchronous swap is contained; an interleaved one would not be.
 */
export function inTimeZone<T>(zone: string, body: () => T): T {
  const before = process.env.TZ;
  process.env.TZ = zone;
  assertTimeZoneInEffect(zone);
  try {
    return body();
  } finally {
    if (before === undefined) delete process.env.TZ;
    else process.env.TZ = before;
  }
}
