// Manual RFID Records (UnrecognizedRFIDScans.docx). When a reader scans a card the system cannot place,
// the operator records the corresponding Student or Employee ID here; the entry is matched against the
// roster best-effort and kept for reconciliation. The recognized-RFID capture flow is untouched — this is
// the review-and-record surface for the ones that were not recognized.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  MenuItem,
  Snackbar,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { ErrorState, LoadingState } from "../components/ResourceStates";
import type { ManualIdEntry, ManualIdEntryRequest } from "../types";

const loadEntries = () => api.listManualIdEntries();

const PERSON_TYPES = ["Student", "Employee"] as const;

export default function ManualRfidRecords() {
  const entries = useApiResource(loadEntries, []);
  const record = useApiMutation((request: ManualIdEntryRequest) => api.recordManualId(request));

  const [cardUid, setCardUid] = useState("");
  const [idNumber, setIdNumber] = useState("");
  const [personType, setPersonType] = useState<(typeof PERSON_TYPES)[number]>("Student");
  const [note, setNote] = useState("");
  const [notice, setNotice] = useState<{ severity: "success" | "error"; text: string } | undefined>(undefined);

  const canSubmit = cardUid.trim().length > 0 && idNumber.trim().length > 0;

  const submit = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!canSubmit) return;
    void record
      .run({
        cardUid: cardUid.trim(),
        idNumber: idNumber.trim(),
        personType,
        eventId: null,
        note: note.trim().length === 0 ? null : note.trim(),
      })
      .then((s) => {
        if (s.outcome === "ignored") return;
        if (s.outcome === "succeeded") {
          setNotice({
            severity: "success",
            text: s.data.isResolved
              ? `Recorded and matched to ${s.data.resolvedName}.`
              : "Recorded. The ID matched no one yet — kept for reconciliation.",
          });
          setCardUid("");
          setIdNumber("");
          setNote("");
          entries.reload();
        } else {
          setNotice({ severity: "error", text: describeApiError(s.error) });
        }
      });
  };

  const cols: GridColDef<ManualIdEntry>[] = [
    { field: "cardUid", headerName: "RFID UID", width: 150 },
    { field: "idNumber", headerName: "ID entered", width: 140 },
    { field: "personType", headerName: "Type", width: 110 },
    {
      field: "isResolved",
      headerName: "Matched",
      width: 180,
      renderCell: (p) =>
        p.row.isResolved ? (
          <Chip size="small" color="success" variant="outlined" label={p.row.resolvedName ?? "Matched"} />
        ) : (
          <Chip size="small" color="warning" variant="outlined" label="Unmatched" />
        ),
    },
    { field: "note", headerName: "Note", flex: 1, minWidth: 140, valueGetter: (_v, r) => r.note ?? "—" },
    {
      field: "recordedAt",
      headerName: "Recorded",
      width: 170,
      valueGetter: (_v, r) => new Date(r.recordedAt).toLocaleString(),
    },
  ];

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} sx={{ mb: 1 }}>
        Manual RFID Records
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Record the Student or Employee ID for a card a reader could not recognize. Each entry is matched
        against the roster and kept so it can be reconciled once the card is registered.
      </Typography>

      <Card variant="outlined" sx={{ mb: 3 }}>
        <CardContent>
          <form onSubmit={submit}>
            <Stack direction={{ xs: "column", md: "row" }} spacing={2} alignItems={{ md: "flex-start" }}>
              <TextField
                label="RFID UID"
                value={cardUid}
                onChange={(e) => setCardUid(e.target.value)}
                size="small"
                required
                helperText="Separators are stripped and it is upper-cased."
              />
              <TextField
                label="ID number"
                value={idNumber}
                onChange={(e) => setIdNumber(e.target.value)}
                size="small"
                required
              />
              <TextField
                label="Type"
                select
                value={personType}
                onChange={(e) => setPersonType(e.target.value as (typeof PERSON_TYPES)[number])}
                size="small"
                sx={{ minWidth: 140 }}
              >
                {PERSON_TYPES.map((t) => (
                  <MenuItem key={t} value={t}>
                    {t}
                  </MenuItem>
                ))}
              </TextField>
              <TextField
                label="Note (optional)"
                value={note}
                onChange={(e) => setNote(e.target.value)}
                size="small"
                sx={{ flex: 1 }}
              />
              <Button
                type="submit"
                variant="contained"
                disabled={!canSubmit || record.status === "running"}
                sx={{ mt: { xs: 0, md: 0.5 } }}
              >
                {record.status === "running" ? "Saving…" : "Record"}
              </Button>
            </Stack>
          </form>
        </CardContent>
      </Card>

      {entries.status === "loading" && <LoadingState label="Loading records…" />}
      {entries.status === "error" && (
        <ErrorState subject="manual RFID records" error={entries.error} onRetry={entries.reload} />
      )}
      {entries.status === "ready" && (
        <div style={{ height: 480, width: "100%" }}>
          <DataGrid
            rows={entries.data}
            columns={cols}
            getRowId={(r) => r.id}
            disableRowSelectionOnClick
            pageSizeOptions={[10, 25, 50]}
            initialState={{ pagination: { paginationModel: { pageSize: 25 } } }}
          />
        </div>
      )}

      <Snackbar
        open={notice !== undefined}
        autoHideDuration={notice?.severity === "error" ? null : 6000}
        onClose={() => setNotice(undefined)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {notice !== undefined ? (
          <Alert severity={notice.severity} onClose={() => setNotice(undefined)}>
            {notice.text}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Box>
  );
}
