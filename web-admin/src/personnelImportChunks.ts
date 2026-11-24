// Personnel import, sent in chunks.
//
// `POST /personnel/import` accepts up to 5,000 rows a call, but each row with a card costs real server
// time (about 7 s per 500), so one call carrying a 70,000-person file would sit far past the 15 s read
// budget and be abandoned by the browser while the server carried on. Chunks of 500 keep every request
// well inside the budget.
//
// Safe to send in pieces and safe to send again: the endpoint is an upsert keyed on the personnel
// number, so a chunk that is repeated reports its rows as updated rather than creating a second copy.
// That is what makes "stop at the first failed chunk and keep what was saved" an honest design — the
// remedy for a failed chunk is simply to import the same file again.

import type { PersonnelImportResult, PersonnelWriteRequest } from "./types";

/** Rows per `POST /personnel/import`. Well under the server's 5,000 cap; see the header. */
export const PERSONNEL_IMPORT_CHUNK_SIZE = 500;

export interface PersonnelImportProgress {
  /** 1-based number of the chunk being sent. */
  chunk: number;
  chunks: number;
}

/**
 * A chunk failed. Carries what the earlier chunks already saved so the screen can say so, because the
 * alternative — a bare error — reads as "nothing was imported" over a roster that is partly loaded.
 */
export class PersonnelImportInterrupted extends Error {
  /** 1-based number of the chunk that failed. */
  readonly failedChunk: number;
  readonly chunks: number;
  /** Rows in the chunks before the failed one: saved, and kept. */
  readonly rowsSaved: number;
  /** The aggregate of every chunk that did complete. */
  readonly partial: PersonnelImportResult;

  constructor(
    failedChunk: number,
    chunks: number,
    rowsSaved: number,
    partial: PersonnelImportResult,
    cause: unknown,
  ) {
    super(`Personnel import stopped at chunk ${failedChunk} of ${chunks}.`, { cause });
    this.name = "PersonnelImportInterrupted";
    this.failedChunk = failedChunk;
    this.chunks = chunks;
    this.rowsSaved = rowsSaved;
    this.partial = partial;
  }
}

const EMPTY_RESULT: PersonnelImportResult = { total: 0, created: 0, updated: 0, failed: 0, errors: [] };

export function chunkRows<T>(rows: readonly T[], size: number = PERSONNEL_IMPORT_CHUNK_SIZE): T[][] {
  const chunks: T[][] = [];
  for (let start = 0; start < rows.length; start += size) chunks.push(rows.slice(start, start + size));
  return chunks;
}

/**
 * Adds one chunk's result to the running total. The server numbers a row within the request it was
 * sent in, so each reported error row is shifted by `offset` (rows sent before this chunk) to point at
 * the row's place in the whole file.
 */
export function mergeImportResult(
  total: PersonnelImportResult,
  next: PersonnelImportResult,
  offset: number,
): PersonnelImportResult {
  return {
    total: total.total + next.total,
    created: total.created + next.created,
    updated: total.updated + next.updated,
    failed: total.failed + next.failed,
    errors: [...total.errors, ...next.errors.map((e) => ({ ...e, row: e.row + offset }))],
  };
}

/**
 * Sends `rows` one chunk at a time and aggregates the results.
 *
 * Sequential on purpose: the server serialises personnel writes behind a lock, so parallel chunks
 * would only turn into 503 WriteLockUnavailable refusals. Stops at the first chunk that throws and
 * raises `PersonnelImportInterrupted`, so nothing is skipped silently.
 */
export async function importInChunks(
  rows: readonly PersonnelWriteRequest[],
  sendChunk: (chunk: PersonnelWriteRequest[]) => Promise<PersonnelImportResult>,
  onProgress: (progress: PersonnelImportProgress) => void,
): Promise<PersonnelImportResult> {
  const chunks = chunkRows(rows);
  let total = EMPTY_RESULT;
  let sent = 0;

  for (const [index, chunk] of chunks.entries()) {
    onProgress({ chunk: index + 1, chunks: chunks.length });
    try {
      total = mergeImportResult(total, await sendChunk(chunk), sent);
    } catch (cause: unknown) {
      throw new PersonnelImportInterrupted(index + 1, chunks.length, sent, total, cause);
    }
    sent += chunk.length;
  }
  return total;
}
