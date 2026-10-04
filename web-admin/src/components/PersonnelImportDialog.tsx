// The Personnel bulk-import dialog. It parses a CSV client-side (the columns the export writes) into rows,
// posts them to `POST /personnel/import`, and shows the per-row summary the server returns. The write itself
// is owned by the Personnel page (page-owns-write), so dismissing this dialog cannot cancel a running import.

import { useMemo, useRef, useState } from "react";
import {
  Alert,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  List,
  ListItem,
  ListItemText,
  Stack,
  Typography,
} from "@mui/material";
import { describeApiError } from "../api";
import type { PersonnelImportResult, PersonnelWriteRequest } from "../types";

/** The request fields a CSV column can fill. */
type Column =
  | "personnelnumber"
  | "firstname"
  | "middlename"
  | "lastname"
  | "email"
  | "classification"
  | "department"
  | "organization"
  | "position"
  | "rfiduid"
  | "status";

/**
 * Every header a column is read by. The first name of each is the spec header the export now writes
 * (e.g. "Personnel ID"); the compact names are what earlier exports wrote, kept so an older file still
 * imports. Matching is on `normalizeHeader`, so case, surrounding whitespace and runs of inner
 * whitespace do not matter — which is why these are written in their normalized (lower-case) form.
 * Column order in the file is free.
 */
const HEADER_ALIASES: Record<Column, readonly string[]> = {
  personnelnumber: ["personnel id", "personnelnumber"],
  rfiduid: ["rfid uid", "rfiduid"],
  lastname: ["last name", "lastname"],
  firstname: ["first name", "firstname"],
  middlename: ["middle name", "middlename"],
  email: ["email"],
  classification: ["classification"],
  department: ["department"],
  organization: ["organization"],
  status: ["status"],
  position: ["position"],
};

/** Trim, lower-case and collapse inner whitespace, so " Personnel  ID " and "personnel id" are one header. */
const normalizeHeader = (h: string): string => h.trim().replace(/\s+/g, " ").toLowerCase();

/** Normalized header text -> the column it fills. Built once from `HEADER_ALIASES`. */
const COLUMN_BY_HEADER: ReadonlyMap<string, Column> = new Map(
  (Object.entries(HEADER_ALIASES) as [Column, readonly string[]][]).flatMap(([column, aliases]) =>
    aliases.map((alias): [string, Column] => [normalizeHeader(alias), column]),
  ),
);

/**
 * A minimal RFC-4180-ish CSV parse: handles quoted fields, escaped quotes (<c>""</c>), commas and newlines
 * inside quotes, and both CRLF and LF line endings. Enough for the file this app's own export writes and for
 * a spreadsheet's "Save as CSV".
 */
function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let field = "";
  let row: string[] = [];
  let inQuotes = false;

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inQuotes) {
      if (c === '"') {
        if (text[i + 1] === '"') {
          field += '"';
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        field += c;
      }
    } else if (c === '"') {
      inQuotes = true;
    } else if (c === ",") {
      row.push(field);
      field = "";
    } else if (c === "\n" || c === "\r") {
      // Close the row on the first of a CRLF pair, and swallow the paired LF.
      if (c === "\r" && text[i + 1] === "\n") i++;
      row.push(field);
      field = "";
      rows.push(row);
      row = [];
    } else {
      field += c;
    }
  }
  // A trailing field/row with no terminating newline.
  if (field.length > 0 || row.length > 0) {
    row.push(field);
    rows.push(row);
  }
  return rows.filter((r) => r.some((cell) => cell.trim() !== ""));
}

interface Parsed {
  rows: PersonnelWriteRequest[];
  error?: string;
}

const orNull = (v: string | undefined): string | null => {
  const t = (v ?? "").trim();
  return t === "" ? null : t;
};

function toRows(text: string): Parsed {
  const grid = parseCsv(text);
  if (grid.length === 0) return { rows: [], error: "The file has no rows." };

  const index: Partial<Record<Column, number>> = {};
  grid[0].forEach((h, at) => {
    const col = COLUMN_BY_HEADER.get(normalizeHeader(h));
    // The first column wins if a file repeats one under two names.
    if (col !== undefined && index[col] === undefined) index[col] = at;
  });
  if (index.personnelnumber === undefined) {
    return {
      rows: [],
      error:
        "The header row must include a Personnel ID column. Export the current list to see the " +
        "expected columns.",
    };
  }

  const cell = (r: string[], col: Column): string | undefined => {
    const at = index[col];
    return at === undefined ? undefined : r[at];
  };

  const rows: PersonnelWriteRequest[] = grid.slice(1).map((r) => {
    const status = (cell(r, "status") ?? "").trim().toLowerCase();
    return {
      personnelNumber: (cell(r, "personnelnumber") ?? "").trim(),
      firstName: (cell(r, "firstname") ?? "").trim(),
      middleName: orNull(cell(r, "middlename")),
      lastName: (cell(r, "lastname") ?? "").trim(),
      email: orNull(cell(r, "email")),
      classification: orNull(cell(r, "classification")),
      department: orNull(cell(r, "department")),
      organization: orNull(cell(r, "organization")),
      position: orNull(cell(r, "position")),
      rfidUid: orNull(cell(r, "rfiduid")),
      status: status === "inactive" ? "Inactive" : "Active",
    };
  });

  return { rows };
}

interface Props {
  onClose: () => void;
  onImport: (rows: PersonnelWriteRequest[]) => void;
  running: boolean;
  result: PersonnelImportResult | undefined;
  error: unknown;
}

export default function PersonnelImportDialog({ onClose, onImport, running, result, error }: Props) {
  const [text, setText] = useState<string | undefined>(undefined);
  const [fileName, setFileName] = useState<string | undefined>(undefined);
  const [readError, setReadError] = useState<string | undefined>(undefined);
  const fileInput = useRef<HTMLInputElement>(null);

  const parsed = useMemo<Parsed | undefined>(
    () => (text === undefined ? undefined : toRows(text)),
    [text],
  );

  const onFile = (file: File | undefined) => {
    if (file === undefined) return;
    setReadError(undefined);
    setFileName(file.name);
    file
      .text()
      .then((t) => setText(t))
      .catch(() => setReadError("The file could not be read."));
  };

  const canImport =
    parsed !== undefined && parsed.error === undefined && parsed.rows.length > 0 && !running;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Import personnel</DialogTitle>
      <DialogContent>
        {result === undefined ? (
          <Stack spacing={2} sx={{ mt: 1 }}>
            <DialogContentText>
              Choose a CSV file with a header row. The columns match the export: Personnel ID, RFID UID, Last
              Name, First Name, Middle Name, Email, Classification, Department, Organization, Status and
              Position (optional). Header case and spacing do not matter, and the older compact names
              (PersonnelNumber, RfidUid, FirstName…) still work. Rows are matched to existing records by
              Personnel ID — an existing one is updated, a new one is created.
            </DialogContentText>

            <Button variant="outlined" component="label">
              Choose CSV file
              <input
                ref={fileInput}
                type="file"
                accept=".csv,text/csv"
                hidden
                onChange={(e) => onFile(e.target.files?.[0])}
              />
            </Button>

            {fileName !== undefined && (
              <Typography variant="body2" color="text.secondary">
                {fileName}
              </Typography>
            )}

            {readError !== undefined && (
              <Alert severity="error" role="alert">
                {readError}
              </Alert>
            )}

            {parsed?.error !== undefined && (
              <Alert severity="error" role="alert">
                {parsed.error}
              </Alert>
            )}

            {parsed !== undefined && parsed.error === undefined && (
              <Alert severity="info">
                {parsed.rows.length} row(s) ready to import.
              </Alert>
            )}

            {error !== undefined && (
              <Alert severity="error" role="alert">
                {describeApiError(error)}
              </Alert>
            )}
          </Stack>
        ) : (
          <Stack spacing={2} sx={{ mt: 1 }}>
            <Stack direction="row" spacing={1}>
              <Chip color="success" label={`${result.created} created`} variant="outlined" />
              <Chip color="primary" label={`${result.updated} updated`} variant="outlined" />
              <Chip
                color={result.failed > 0 ? "error" : "default"}
                label={`${result.failed} failed`}
                variant="outlined"
              />
            </Stack>
            {result.errors.length > 0 && (
              <>
                <Typography variant="subtitle2">Rows that were not imported</Typography>
                <List dense sx={{ maxHeight: 260, overflow: "auto", border: 1, borderColor: "divider", borderRadius: 1 }}>
                  {result.errors.map((e) => (
                    <ListItem key={`${e.row}-${e.personnelNumber ?? ""}`} disableGutters sx={{ px: 1 }}>
                      <ListItemText
                        primary={`Row ${e.row}${e.personnelNumber ? ` (${e.personnelNumber})` : ""}`}
                        secondary={e.message}
                      />
                    </ListItem>
                  ))}
                </List>
              </>
            )}
          </Stack>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{result === undefined ? "Cancel" : "Close"}</Button>
        {result === undefined && (
          <Button
            variant="contained"
            onClick={() => parsed && onImport(parsed.rows)}
            disabled={!canImport}
          >
            {running ? "Importing…" : "Import"}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}
