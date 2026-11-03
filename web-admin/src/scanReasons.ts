// Why a scan whose card DID resolve to a person still produced no attendance row (ADR-015 D-100,
// #706 F2-6). Absent on an ordinary unrecognised card.

/** `ScanLog.PersonnelClientNotCapableReason`: a personnel card tapped on a capture app that cannot record personnel. */
export const PERSONNEL_CLIENT_NOT_CAPABLE = "PersonnelClientNotCapable";

const REASON_LABELS: Record<string, string> = {
  [PERSONNEL_CLIENT_NOT_CAPABLE]: "Personnel card, tapped on an outdated app",
};

/**
 * What to print beside a scan's outcome. An unknown code is returned verbatim, as
 * `describeImportWarning` does: the API can grow a reason without this build being redeployed, and a
 * blank would read as "no reason". No reason at all is `undefined`.
 */
export function describeScanReason(reason: string | undefined): string | undefined {
  if (reason === undefined) return undefined;
  return REASON_LABELS[reason] ?? reason;
}
