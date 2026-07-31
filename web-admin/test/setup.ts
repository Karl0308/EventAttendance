// Runs before every test file, in that file's worker.
//
// Its only job is the time zone, and the assertion beside it is not ceremony: on Windows the usual
// `TZ=… vitest` route is silently dropped, so "the suite runs in Asia/Manila" has to be established
// rather than declared. See `timeZone.ts` for the measurements.

import { assertTimeZoneInEffect, BASELINE_TIME_ZONE } from "./timeZone";

process.env.TZ = BASELINE_TIME_ZONE;
assertTimeZoneInEffect(BASELINE_TIME_ZONE);
