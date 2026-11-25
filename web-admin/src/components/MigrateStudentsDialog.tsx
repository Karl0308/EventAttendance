// "Move students to Personnel" — the dialog over `POST /personnel/migrate-from-students`. It previews
// (dry run) which students a number prefix would move, asks for an explicit confirmation, and then shows
// what the run did. Both calls are owned by the Personnel page (page-owns-write, as `PersonnelImportDialog`
// does), so dismissing this dialog cannot cancel a move that is already running.

import { useState } from "react";
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from "@mui/material";
import { alpha } from "@mui/material/styles";
import { describeApiError } from "../api";
import { advise } from "../apiGuidance";
import type { MigrateFromStudentsResult, MigrateFromStudentsRow, MigrateOutcome } from "../types";

/** The server's default (`MigrateFromStudentsRequest.DefaultPrefix`) and floor (`MinPrefixLength`). */
export const DEFAULT_MIGRATE_PREFIX = "7200";
const MIN_PREFIX_LENGTH = 4;

/** A school can match hundreds of students; the table shows conflicts first and stops here. */
const MAX_ROWS_SHOWN = 500;

const NO_VALUE = "—";

const OUTCOME_LABEL: Record<MigrateOutcome, string> = {
  Create: "Will create",
  Update: "Will update",
  Conflict: "Conflict",
};
/** After a run the same outcome reads in the past tense. */
const OUTCOME_LABEL_DONE: Record<MigrateOutcome, string> = {
  Create: "Created",
  Update: "Updated",
  Conflict: "Conflict",
};
const OUTCOME_COLOR: Record<MigrateOutcome, "success" | "primary" | "error"> = {
  Create: "success",
  Update: "primary",
  Conflict: "error",
};

interface Props {
  onClose: () => void;
  onPreview: (prefix: string) => void;
  /** Runs for real, with the prefix that was previewed. */
  onRun: (prefix: string) => void;
  previewing: boolean;
  preview: MigrateFromStudentsResult | undefined;
  previewError: unknown;
  running: boolean;
  result: MigrateFromStudentsResult | undefined;
  runError: unknown;
}

/** Conflicts first, then the rest in the server's order; capped so a huge match cannot freeze the table. */
function rowsToShow(rows: readonly MigrateFromStudentsRow[]): MigrateFromStudentsRow[] {
  const conflicts = rows.filter((r) => r.outcome === "Conflict");
  const movable = rows.filter((r) => r.outcome !== "Conflict");
  return [...conflicts, ...movable].slice(0, MAX_ROWS_SHOWN);
}

function RowsTable({ rows, done }: { rows: readonly MigrateFromStudentsRow[]; done: boolean }) {
  const shown = rowsToShow(rows);
  const labels = done ? OUTCOME_LABEL_DONE : OUTCOME_LABEL;
  return (
    <>
      <TableContainer sx={{ maxHeight: 320, border: 1, borderColor: "divider", borderRadius: 1 }}>
        <Table size="small" stickyHeader aria-label={done ? "Students that were processed" : "Students to be moved"}>
          <TableHead>
            <TableRow>
              <TableCell>Student number</TableCell>
              <TableCell>Name</TableCell>
              <TableCell>RFID UID</TableCell>
              <TableCell>Outcome</TableCell>
              <TableCell>Detail</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {shown.map((r) => (
              <TableRow key={r.studentId} sx={r.outcome === "Conflict" ? { bgcolor: (t) => alpha(t.palette.error.main, 0.08) } : undefined}>
                <TableCell>{r.studentNumber}</TableCell>
                <TableCell>{r.fullName}</TableCell>
                <TableCell>{r.rfidUid ?? NO_VALUE}</TableCell>
                <TableCell>
                  <Chip size="small" variant="outlined" color={OUTCOME_COLOR[r.outcome]} label={labels[r.outcome]} />
                </TableCell>
                <TableCell>{r.outcome === "Conflict" ? r.message : NO_VALUE}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
      {rows.length > shown.length && (
        <Typography variant="caption" color="text.secondary">
          Showing {shown.length} of {rows.length} students, conflicts first.
        </Typography>
      )}
    </>
  );
}

function CountChips({ r, done }: { r: MigrateFromStudentsResult; done: boolean }) {
  return (
    <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
      <Chip color="success" variant="outlined" label={`${r.created} ${done ? "created" : "will be created"}`} />
      <Chip color="primary" variant="outlined" label={`${r.updated} ${done ? "updated" : "will be updated"}`} />
      <Chip
        color={r.conflicts > 0 ? "error" : "default"}
        variant="outlined"
        label={`${r.conflicts} ${r.conflicts === 1 ? "conflict" : "conflicts"}`}
      />
    </Stack>
  );
}

export default function MigrateStudentsDialog({
  onClose,
  onPreview,
  onRun,
  previewing,
  preview,
  previewError,
  running,
  result,
  runError,
}: Props) {
  const [prefix, setPrefix] = useState(DEFAULT_MIGRATE_PREFIX);
  const [confirming, setConfirming] = useState(false);

  const trimmed = prefix.trim();
  const prefixOk = trimmed.length >= MIN_PREFIX_LENGTH;
  // The preview describes the prefix it was made for; editing the field leaves it describing something else.
  const stale = preview !== undefined && preview.prefix !== trimmed;
  const movable = preview === undefined ? 0 : preview.created + preview.updated;
  const busy = previewing || running;

  const submitPreview = () => {
    setConfirming(false);
    onPreview(trimmed);
  };

  const runFailureEffect = runError === undefined ? undefined : advise(runError).serverEffect;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md" aria-labelledby="migrate-students-title">
      <DialogTitle id="migrate-students-title">Move students to Personnel</DialogTitle>
      <DialogContent>
        {result !== undefined ? (
          <Stack spacing={2} sx={{ mt: 1 }}>
            <Alert severity={result.conflicts > 0 ? "warning" : "success"} role="status">
              {result.created + result.updated} of {result.matched} students were moved to Personnel.
              {result.conflicts > 0 && " The conflicts below stayed in the Students list."}
            </Alert>
            <CountChips r={result} done />
            {result.rows.length > 0 && <RowsTable rows={result.rows} done />}
          </Stack>
        ) : confirming && preview !== undefined ? (
          <Stack spacing={2} sx={{ mt: 1 }}>
            <Alert severity="warning" role="alert">
              <AlertTitle>
                Move {movable} {movable === 1 ? "student" : "students"} to Personnel?
              </AlertTitle>
              These students will be removed from the Students list and added to Personnel, and each one’s
              RFID card moves with them. Past attendance stays as it was recorded. {preview.conflicts > 0 &&
                `${preview.conflicts} conflicting ${preview.conflicts === 1 ? "student is" : "students are"} left where they are. `}
              Running it again is safe.
            </Alert>
            {runError !== undefined && (
              <Alert severity="error" role="alert">
                {describeApiError(runError)}
                {runFailureEffect === "unknown" &&
                  " Some students may already have moved — preview again to see what is left."}
              </Alert>
            )}
            {running && (
              <Alert severity="info" role="status">
                Moving students… Keep this window open.
              </Alert>
            )}
          </Stack>
        ) : (
          <Stack
            component="form"
            spacing={2}
            sx={{ mt: 1 }}
            onSubmit={(e) => {
              e.preventDefault();
              if (prefixOk && !busy) submitPreview();
            }}
            noValidate
          >
            <DialogContentText>
              Moves every student whose number starts with the prefix into Personnel. Preview first: nothing is
              changed until you confirm.
            </DialogContentText>
            <Box>
              <TextField
                label="Student number prefix"
                value={prefix}
                onChange={(e) => setPrefix(e.target.value)}
                size="small"
                error={!prefixOk}
                helperText={`At least ${MIN_PREFIX_LENGTH} characters.`}
                slotProps={{ htmlInput: { autoComplete: "off" } }}
              />
            </Box>

            {previewError !== undefined && (
              <Alert severity="error" role="alert">
                {describeApiError(previewError)}
              </Alert>
            )}

            {previewing && (
              <Alert severity="info" role="status">
                Looking up students…
              </Alert>
            )}

            {preview !== undefined && !previewing && (
              <Stack spacing={1.5}>
                {stale ? (
                  <Alert severity="info" role="status">
                    The prefix changed. Preview again to see which students it matches.
                  </Alert>
                ) : preview.matched === 0 ? (
                  <Alert severity="info" role="status">
                    No students start with “{preview.prefix}”.
                  </Alert>
                ) : (
                  <>
                    <Typography variant="body2">
                      {preview.matched} {preview.matched === 1 ? "student matches" : "students match"} “{preview.prefix}”.
                    </Typography>
                    <CountChips r={preview} done={false} />
                    <RowsTable rows={preview.rows} done={false} />
                  </>
                )}
              </Stack>
            )}
          </Stack>
        )}
      </DialogContent>
      <DialogActions>
        {result !== undefined ? (
          <Button onClick={onClose}>Close</Button>
        ) : confirming ? (
          <>
            <Button onClick={() => setConfirming(false)} disabled={running}>
              Back
            </Button>
            <Button
              variant="contained"
              color="warning"
              disabled={running || preview === undefined}
              onClick={() => preview !== undefined && onRun(preview.prefix)}
            >
              {running ? "Moving…" : `Yes, move ${movable} ${movable === 1 ? "student" : "students"}`}
            </Button>
          </>
        ) : (
          <>
            <Button onClick={onClose}>Cancel</Button>
            <Button variant="outlined" disabled={!prefixOk || busy} onClick={submitPreview}>
              {previewing ? "Previewing…" : "Preview"}
            </Button>
            <Button
              variant="contained"
              disabled={preview === undefined || stale || movable === 0 || busy}
              onClick={() => setConfirming(true)}
            >
              Move {movable} {movable === 1 ? "student" : "students"}
            </Button>
          </>
        )}
      </DialogActions>
    </Dialog>
  );
}
