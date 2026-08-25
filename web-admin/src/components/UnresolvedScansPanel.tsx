import {
  Alert,
  Box,
  Card,
  CardContent,
  Chip,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from "@mui/material";
import HelpOutlineIcon from "@mui/icons-material/HelpOutline";
import type { EventScanLog } from "../types";
import { EmptyState, ErrorState, LoadingState } from "./ResourceStates";

/**
 * Scans at this event that resolved to no student.
 *
 * **What this panel is for.** A card the server cannot place is refused, and until recently that
 * refusal was the whole record - the device was told and nothing was kept. So "nobody scanned" and
 * "somebody scanned a card we could not place" looked identical from the back office, and the second
 * is the one worth acting on: a card that was never imported, a card issued to somebody who left, or
 * a device running a manifest from before the roster changed.
 *
 * **It is deliberately not part of the roster.** The roster answers "who was expected and who turned
 * up". These scans have no student to be about, so they cannot be rows there; showing them beneath it
 * keeps the two questions next to each other without pretending they are the same question.
 */
/** The three states this panel renders, shaped like `AudienceRead` beside it. */
export type ScanLogRead =
  | { status: "loading" }
  | { status: "error"; error: unknown }
  | { status: "ready"; log: EventScanLog | undefined };

export default function UnresolvedScansPanel({
  read,
  onRetry,
}: {
  read: ScanLogRead;
  onRetry: () => void;
}) {
  return (
    <Card variant="outlined" sx={{ mt: 3 }}>
      <CardContent>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
          <Typography variant="h6" component="h2">
            Unrecognised scans
          </Typography>
          <Tooltip
            title={
              "Cards presented at this event that matched no student. They are not attendance - " +
              "nobody was marked present or absent by them - and they are kept so a card that is " +
              "missing from the roster, or a device holding an out-of-date copy of it, is visible " +
              "rather than silent."
            }
          >
            <HelpOutlineIcon fontSize="small" color="action" aria-label="What is this?" />
          </Tooltip>
        </Stack>

        {read.status === "loading" && <LoadingState label="Loading unrecognised scans" />}

        {read.status === "error" && (
          <ErrorState subject="the unrecognised scans" error={read.error} onRetry={onRetry} />
        )}

        {read.status === "ready" && (read.log?.totalScans ?? 0) === 0 && (
          <EmptyState
            message={
              "Every card scanned here was recognised. Nothing to look at, which is the good answer - " +
              "those scans resolved to students and appear in the roster above."
            }
          />
        )}

        {read.status === "ready" && read.log !== undefined && read.log.totalScans > 0 && (
          <>
            {/* Both counts, because the gap between them is the finding rather than a detail. Many
                scans over few cards is somebody presenting a card that is not working and trying
                again; one scan each over many cards is a roster that has not been given its RFID
                column, and those want completely different responses. */}
            <Stack direction="row" spacing={1} sx={{ mb: 2 }} flexWrap="wrap" useFlexGap>
              <Chip label={`${read.log.totalScans} scan${read.log.totalScans === 1 ? "" : "s"}`} />
              <Chip
                variant="outlined"
                label={`${read.log.distinctCards} card${read.log.distinctCards === 1 ? "" : "s"}`}
              />
            </Stack>

            {read.log.distinctCards === read.log.totalScans && read.log.distinctCards > 2 && (
              <Alert severity="info" sx={{ mb: 2 }}>
                Each of these cards was presented once. That usually means the cards are genuine and
                simply are not on the roster yet - the imported roster carries no RFID column, so a
                student whose card has never been loaded scans exactly like this.
              </Alert>
            )}

            <Box sx={{ overflowX: "auto" }}>
              <Table size="small" aria-label="Unrecognised scans">
                <TableHead>
                  <TableRow>
                    <TableCell>Card</TableCell>
                    <TableCell>Scanned</TableCell>
                    <TableCell>Outcome</TableCell>
                    <TableCell>Device said</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {read.log.scans.map((scan) => (
                    // deviceTapId is the device's own idempotency key and is unique per scan; the
                    // card is not, because presenting the same card twice is exactly what this panel
                    // exists to show.
                    <TableRow key={scan.deviceTapId ?? `${scan.cardUid}-${scan.scannedAt}`}>
                      <TableCell>
                        {/* Monospace and never reformatted. These serials carry significant leading
                            zeros, and a display that trims or groups them shows a different card from
                            the one that was presented. */}
                        <Typography variant="body2" fontFamily="monospace">
                          {scan.cardUid}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Tooltip title={`Recorded ${new Date(scan.recordedAt).toLocaleString()}`}>
                          <span>{new Date(scan.scannedAt).toLocaleString()}</span>
                        </Tooltip>
                      </TableCell>
                      <TableCell>{scan.serverOutcome}</TableCell>
                      <TableCell>
                        {/* The device's own claim, and only interesting when it disagrees. A device
                            reporting it found a card the server could not place means its cached
                            roster is out of date, or the card is a copy of one. */}
                        {scan.localOutcome ? (
                          <Chip
                            size="small"
                            color={scan.localOutcome.toLowerCase().includes("found") ? "warning" : "default"}
                            label={scan.localOutcome}
                          />
                        ) : (
                          <Typography variant="body2" color="text.secondary">
                            —
                          </Typography>
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
          </>
        )}
      </CardContent>
    </Card>
  );
}
