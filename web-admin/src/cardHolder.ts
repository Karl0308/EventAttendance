// The duplicate-card refusal, said with the holder's name (ADR-015 D-98, #706 F2-3).
//
// A UID identifies one person across students and personnel, so a 409 on a card (`CardUidInUse` on the
// student routes, `DuplicateRfid` on the personnel ones) can now say WHO holds it. The `holder`
// extension is optional on the wire (absent on a lost race), and every caller falls back to the
// server's own `detail` when it is, so this returns `undefined` rather than inventing a sentence.

import type { CardHolder } from "./types";

/** "This card is already assigned to Maria Santos (2026-0001)." Undefined when there is no holder. */
export function holderSentence(holder: CardHolder | undefined): string | undefined {
  if (holder === undefined) return undefined;
  return `This card is already assigned to ${holder.name} (${holder.number}).`;
}
