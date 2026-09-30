// The Live Attendance attendance-code panel (LiveAttendance.docx §3–§5): generate a unique code per
// attendee, list the codes, and email them to All eligible attendees or a Selected subset. Self-contained —
// it owns its own read and its two writes — so it drops into EventDetail with a single tag and does not add
// state to that already-large page. No SMTP is wired in this build; the server's send is a logging no-op,
// so a "sent" tally means the flow ran, not that mail left the building.

import { useMemo, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  FormControl,
  FormControlLabel,
  FormLabel,
  List,
  ListItem,
  Radio,
  RadioGroup,
  Snackbar,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from "@mui/material";
import KeyIcon from "@mui/icons-material/Key";
import EmailIcon from "@mui/icons-material/Email";
import RefreshIcon from "@mui/icons-material/Refresh";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "./ResourceStates";
import type { AttendanceCode, AttendanceCodeEmailResult } from "../types";

const NO_CODES =
  "No attendance codes have been generated for this event yet. Generate codes to issue each attendee a " +
  "unique code that can then be emailed to them.";

interface Notice {
  severity: "success" | "error";
  text: string;
}

const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function AttendanceCodesPanel({
  eventId,
  canWrite,
}: {
  eventId: string;
  canWrite: boolean;
}) {
  const codes = useApiResource(() => api.listAttendanceCodes(eventId), [eventId]);

  const generate = useApiMutation((regenerate: boolean) =>
    api.generateAttendanceCodes(eventId, regenerate),
  );
  const sendEmail = useApiMutation((mode: "All" | "Selected", studentIds?: string[]) =>
    api.emailAttendanceCodes(eventId, { mode, studentIds }),
  );

  const [regenerating, setRegenerating] = useState(false);
  const [emailing, setEmailing] = useState(false);

  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);
  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const rows = codes.data ?? [];

  const runGenerate = (regenerate: boolean) => {
    void generate.run(regenerate).then((settled) => {
      if (settled.outcome === "ignored") return;
      codes.reload();
      setRegenerating(false);

      if (settled.outcome === "succeeded") {
        const { created, regenerated, alreadyHad } = settled.data;
        announce({
          severity: "success",
          text: regenerate
            ? `Regenerated ${regenerated} code(s).`
            : `Generated ${created} new code(s); ${alreadyHad} attendee(s) already had one.`,
        });
      } else {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const runEmail = (mode: "All" | "Selected", studentIds?: string[]) => {
    void sendEmail.run(mode, studentIds).then((settled) => {
      if (settled.outcome === "ignored") return;
      codes.reload();

      if (settled.outcome === "succeeded") {
        setEmailing(false);
        announce({ severity: "success", text: summarize(settled.data) });
      } else {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  return (
    <Box sx={{ mt: 4 }}>
      <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h6">Attendance codes ({rows.length})</Typography>
        {codes.refreshing && (
          <Typography variant="body2" color="text.secondary" role="status">
            Refreshing…
          </Typography>
        )}
        <Box sx={{ flexGrow: 1 }} />
        {canWrite && (
          <>
            <Button
              size="small"
              startIcon={<KeyIcon />}
              variant="contained"
              onClick={() => runGenerate(false)}
              disabled={generate.status === "running"}
            >
              Generate codes
            </Button>
            <Button
              size="small"
              startIcon={<RefreshIcon />}
              onClick={() => setRegenerating(true)}
              disabled={generate.status === "running" || rows.length === 0}
            >
              Regenerate all
            </Button>
            <Button
              size="small"
              startIcon={<EmailIcon />}
              onClick={() => setEmailing(true)}
              disabled={rows.length === 0}
            >
              Email codes
            </Button>
          </>
        )}
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Each attendee gets one unique code for this event. Emailing is available for attendees with an email
        address on file; those without one are skipped. (No mail server is configured in this build, so
        sends are recorded but not delivered.)
      </Typography>

      {codes.status === "loading" && <LoadingState label="Loading attendance codes…" />}

      {codes.status === "error" && (
        <ErrorState subject="attendance codes" error={codes.error} onRetry={codes.reload} />
      )}

      {codes.status === "ready" &&
        (rows.length === 0 ? (
          <EmptyState message={NO_CODES} />
        ) : (
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Attendee</TableCell>
                <TableCell>ID number</TableCell>
                <TableCell>Code</TableCell>
                <TableCell>Email</TableCell>
                <TableCell>Emailed</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((c) => (
                <TableRow key={c.id}>
                  <TableCell>{c.studentName}</TableCell>
                  <TableCell>{c.studentNumber}</TableCell>
                  <TableCell>
                    <Chip label={c.code} size="small" sx={{ fontFamily: "monospace" }} />
                  </TableCell>
                  <TableCell>{c.email ?? "—"}</TableCell>
                  <TableCell>
                    {c.emailCount > 0 ? `${c.emailCount}×` : "—"}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ))}

      {regenerating && (
        <Dialog open onClose={() => setRegenerating(false)} fullWidth maxWidth="sm">
          <DialogTitle>Regenerate all codes?</DialogTitle>
          <DialogContent>
            <DialogContentText>
              This replaces every attendee's existing code with a new one. Codes already emailed will no
              longer match what attendees received. This cannot be undone.
            </DialogContentText>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setRegenerating(false)}>Cancel</Button>
            <Button
              color="warning"
              variant="contained"
              onClick={() => runGenerate(true)}
              disabled={generate.status === "running"}
            >
              {generate.status === "running" ? "Regenerating…" : "Regenerate all"}
            </Button>
          </DialogActions>
        </Dialog>
      )}

      {emailing && (
        <EmailCodesDialog
          codes={rows}
          onClose={() => setEmailing(false)}
          onSend={runEmail}
          running={sendEmail.status === "running"}
          error={sendEmail.status === "failed" ? sendEmail.error : undefined}
        />
      )}

      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {notice ? (
          <Alert
            severity={notice.severity}
            role={notice.severity === "error" ? "alert" : "status"}
            onClose={() => setAnnouncing(false)}
          >
            {notice.text}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Box>
  );
}

function summarize(result: AttendanceCodeEmailResult): string {
  const parts = [`Sent ${result.sent} of ${result.requested}`];
  if (result.skippedNoEmail > 0) parts.push(`${result.skippedNoEmail} had no email`);
  if (result.skippedNoCode > 0) parts.push(`${result.skippedNoCode} had no code`);
  if (result.failed > 0) parts.push(`${result.failed} failed`);
  return `${parts.join("; ")}.`;
}

function EmailCodesDialog({
  codes,
  onClose,
  onSend,
  running,
  error,
}: {
  codes: readonly AttendanceCode[];
  onClose: () => void;
  onSend: (mode: "All" | "Selected", studentIds?: string[]) => void;
  running: boolean;
  error: unknown;
}) {
  const [mode, setMode] = useState<"All" | "Selected">("All");
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());

  const withEmail = useMemo(() => codes.filter((c) => c.email), [codes]);

  const toggle = (studentId: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(studentId)) next.delete(studentId);
      else next.add(studentId);
      return next;
    });

  const canSend = mode === "All" ? withEmail.length > 0 : selected.size > 0;

  const send = () => {
    if (mode === "All") onSend("All");
    else onSend("Selected", [...selected]);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Email attendance codes</DialogTitle>
      <DialogContent>
        <FormControl sx={{ mb: 1 }}>
          <FormLabel id="email-mode-label">Recipients</FormLabel>
          <RadioGroup
            aria-labelledby="email-mode-label"
            value={mode}
            onChange={(e) => setMode(e.target.value as "All" | "Selected")}
          >
            <FormControlLabel
              value="All"
              control={<Radio />}
              label={`All attendees with an email (${withEmail.length})`}
            />
            <FormControlLabel value="Selected" control={<Radio />} label="Selected attendees" />
          </RadioGroup>
        </FormControl>

        {mode === "Selected" && (
          <List dense sx={{ maxHeight: 280, overflow: "auto", border: 1, borderColor: "divider", borderRadius: 1 }}>
            {codes.map((c) => (
              <ListItem key={c.id} disableGutters sx={{ px: 1 }}>
                <FormControlLabel
                  control={
                    <Checkbox
                      checked={selected.has(c.studentId)}
                      onChange={() => toggle(c.studentId)}
                      disabled={!c.email}
                    />
                  }
                  label={
                    <span>
                      {c.studentName} ({c.studentNumber})
                      {!c.email && (
                        <Typography component="span" variant="caption" color="text.secondary">
                          {" "}
                          — no email
                        </Typography>
                      )}
                    </span>
                  }
                />
              </ListItem>
            ))}
          </List>
        )}

        {error !== undefined && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            {describeApiError(error)}
          </Alert>
        )}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" onClick={send} disabled={running || !canSend}>
          {running ? "Sending…" : "Send"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
