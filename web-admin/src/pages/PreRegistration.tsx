// The Pre-Registration screen (PreRegistration.docx) — sessions opened against an event audience, into
// which attendees are pre-registered by RFID tap or manual selection, up to a capacity. The page owns the
// session writes; the manage dialog owns the per-session registration/removal writes against its own list.

import { useEffect, useLayoutEffect, useRef, useState } from "react";
import {
  Alert,
  Autocomplete,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControl,
  FormHelperText,
  IconButton,
  InputLabel,
  LinearProgress,
  List,
  ListItem,
  ListItemText,
  MenuItem,
  Select,
  Snackbar,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import GroupAddIcon from "@mui/icons-material/GroupAdd";
import DeleteIcon from "@mui/icons-material/Delete";
import LockIcon from "@mui/icons-material/Lock";
import LockOpenIcon from "@mui/icons-material/LockOpen";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import type {
  AudienceDefinition,
  Personnel,
  PreRegistrant,
  PreRegistrationSession,
  Student,
} from "../types";

const loadSessions = () => api.listPreRegistrationSessions();
const loadAudiences = () => api.listAudienceDefinitions({ includeInactive: false });

const NO_SESSIONS =
  "No pre-registration sessions yet. A session is opened against an event audience with a capacity, then " +
  "attendees are pre-registered into it by tapping a card or picking them from the Academic Community. " +
  "Create the first one here.";

interface Notice {
  severity: "success" | "error";
  text: string;
}

const NOTICE_MS = 6000;
const NO_AUTO_HIDE = null;

export default function PreRegistration() {
  const sessions = useApiResource(loadSessions, []);
  const audiences = useApiResource(loadAudiences, []);

  const create = useApiMutation((name: string, audienceDefinitionId: string, capacity: number) =>
    api.createPreRegistrationSession({ name, audienceDefinitionId, capacity }),
  );
  const setClosed = useApiMutation((id: string, isClosed: boolean) =>
    api.setPreRegistrationClosed(id, isClosed),
  );

  const [creating, setCreating] = useState(false);
  const [managing, setManaging] = useState<PreRegistrationSession | undefined>(undefined);

  const createOpen = useRef(creating);
  useLayoutEffect(() => {
    createOpen.current = creating;
  });

  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);
  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const activeAudiences = audiences.data ?? [];

  const submitCreate = (name: string, audienceId: string, capacity: number) => {
    void create.run(name, audienceId, capacity).then((settled) => {
      if (settled.outcome === "ignored") return;
      sessions.reload();
      if (settled.outcome === "succeeded") {
        setCreating(false);
        announce({ severity: "success", text: `“${settled.data.name}” was created.` });
      } else if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const toggleClosed = (s: PreRegistrationSession) => {
    void setClosed.run(s.id, !s.isClosed).then((settled) => {
      if (settled.outcome === "ignored") return;
      sessions.reload();
      announce(
        settled.outcome === "succeeded"
          ? {
              severity: "success",
              text: s.isClosed ? `“${s.name}” was reopened.` : `“${s.name}” was closed.`,
            }
          : { severity: "error", text: describeApiError(settled.error) },
      );
    });
  };

  const cols: GridColDef<PreRegistrationSession>[] = [
    { field: "name", headerName: "Session", flex: 1, minWidth: 180 },
    { field: "audienceName", headerName: "Audience", flex: 1, minWidth: 160 },
    {
      field: "registeredCount",
      headerName: "Registered",
      width: 140,
      valueGetter: (_v, row) => `${row.registeredCount} / ${row.capacity}`,
    },
    {
      field: "status",
      headerName: "Status",
      width: 130,
      sortable: false,
      valueGetter: (_v, row) => (row.isClosed ? "Closed" : row.isFull ? "Full" : "Open"),
      renderCell: (p) =>
        p.row.isClosed ? (
          <Chip size="small" label="Closed" variant="outlined" />
        ) : p.row.isFull ? (
          <Chip size="small" label="Full" color="warning" variant="outlined" />
        ) : (
          <Chip size="small" label="Open" color="success" variant="outlined" />
        ),
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 190,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
          <IconButton
            sx={{ p: 1.25 }}
            onClick={() => setManaging(p.row)}
            aria-label={`Manage ${p.row.name}`}
            title={`Manage ${p.row.name}`}
          >
            <GroupAddIcon fontSize="small" />
          </IconButton>
          <IconButton
            sx={{ p: 1.25 }}
            color={p.row.isClosed ? "success" : "warning"}
            onClick={() => toggleClosed(p.row)}
            aria-label={p.row.isClosed ? `Reopen ${p.row.name}` : `Close ${p.row.name}`}
            title={p.row.isClosed ? `Reopen ${p.row.name}` : `Close ${p.row.name}`}
          >
            {p.row.isClosed ? <LockOpenIcon fontSize="small" /> : <LockIcon fontSize="small" />}
          </IconButton>
        </Stack>
      ),
    },
  ];

  const noAudiences = audiences.status === "ready" && activeAudiences.length === 0;

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Pre-registration
        </Typography>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={() => {
            create.reset();
            setCreating(true);
          }}
          disabled={activeAudiences.length === 0}
        >
          Create session
        </Button>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Open a session against an event audience, set its capacity, then pre-register attendees by tapping a
        card or picking them from the Academic Community. Duplicate and capacity rules apply to both methods.
      </Typography>

      {noAudiences && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Create an active event audience first — a pre-registration session is opened against one.
        </Alert>
      )}

      {sessions.status === "loading" && <LoadingState label="Loading sessions…" />}
      {sessions.status === "error" && (
        <ErrorState subject="pre-registration sessions" error={sessions.error} onRetry={sessions.reload} />
      )}

      {sessions.status === "ready" &&
        (sessions.data.length === 0 ? (
          <EmptyState message={NO_SESSIONS} />
        ) : (
          <div style={{ height: 520, width: "100%" }}>
            <DataGrid
              rows={sessions.data}
              columns={cols}
              getRowId={(r) => r.id}
              disableRowSelectionOnClick
              pageSizeOptions={[10, 25]}
              initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
            />
          </div>
        ))}

      {creating && (
        <CreateSessionDialog
          audiences={activeAudiences}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          error={create.status === "failed" ? create.error : undefined}
        />
      )}

      {managing !== undefined && (
        <ManageSessionDialog
          session={managing}
          onClose={() => {
            setManaging(undefined);
            sessions.reload();
          }}
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

// ------------------------------------------------------------------------------------- create dialog

function CreateSessionDialog({
  audiences,
  onClose,
  onSubmit,
  running,
  error,
}: {
  audiences: readonly AudienceDefinition[];
  onClose: () => void;
  onSubmit: (name: string, audienceId: string, capacity: number) => void;
  running: boolean;
  error: unknown;
}) {
  const [name, setName] = useState("");
  const [audienceId, setAudienceId] = useState("");
  const [capacity, setCapacity] = useState("50");

  const capacityNum = Number(capacity);
  const capacityValid = Number.isInteger(capacityNum) && capacityNum > 0;
  const canSubmit = name.trim().length > 0 && audienceId !== "" && capacityValid;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle>Create a pre-registration session</DialogTitle>
      <DialogContent>
        <Stack spacing={2.5} sx={{ mt: 1 }}>
          {error !== undefined && (
            <Alert severity="error" role="alert">
              {describeApiError(error)}
            </Alert>
          )}
          <TextField
            label="Session name"
            required
            fullWidth
            value={name}
            onChange={(e) => setName(e.target.value)}
            helperText="A short label, such as “Freshman Orientation”."
          />
          <FormControl fullWidth required>
            <InputLabel id="prereg-audience-label">Event audience</InputLabel>
            <Select
              labelId="prereg-audience-label"
              label="Event audience"
              value={audienceId}
              onChange={(e) => setAudienceId(e.target.value)}
            >
              {audiences.map((a) => (
                <MenuItem key={a.id} value={a.id}>
                  {a.name}
                </MenuItem>
              ))}
            </Select>
            <FormHelperText>The audience this session pre-registers attendees for.</FormHelperText>
          </FormControl>
          <TextField
            label="Capacity"
            required
            type="number"
            fullWidth
            value={capacity}
            onChange={(e) => setCapacity(e.target.value)}
            error={capacity !== "" && !capacityValid}
            helperText={
              capacity !== "" && !capacityValid
                ? "Capacity must be a whole number greater than zero."
                : "The maximum number of attendees accepted."
            }
            inputProps={{ min: 1 }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={() => onSubmit(name, audienceId, capacityNum)}
          disabled={running || !canSubmit}
        >
          {running ? "Creating…" : "Create session"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

// ------------------------------------------------------------------------------------- manage dialog

interface PersonOption {
  id: string;
  kind: "Student" | "Personnel";
  label: string;
}

function ManageSessionDialog({
  session,
  onClose,
}: {
  session: PreRegistrationSession;
  onClose: () => void;
}) {
  const registrants = useApiResource(() => api.listPreRegistrants(session.id), [session.id]);

  const register = useApiMutation((req: Parameters<typeof api.registerPreRegistrant>[1]) =>
    api.registerPreRegistrant(session.id, req),
  );
  const remove = useApiMutation((registrantId: string) =>
    api.removePreRegistrant(session.id, registrantId),
  );

  const [counter, setCounter] = useState({ count: session.registeredCount, capacity: session.capacity });
  const [mode, setMode] = useState<"Manual" | "Tapped">("Manual");
  const [cardUid, setCardUid] = useState("");
  const [chosen, setChosen] = useState<PersonOption | null>(null);
  const [notice, setNotice] = useState<Notice | undefined>(undefined);

  // People for the manual picker, loaded once.
  const [people, setPeople] = useState<PersonOption[] | undefined>(undefined);
  useEffect(() => {
    let live = true;
    Promise.all([api.listStudents(), api.listPersonnel()]).then(
      ([students, personnel]: [Student[], Personnel[]]) => {
        if (!live) return;
        setPeople([
          ...students.map((s) => opt(s.id, "Student", s.studentNumber, s.fullName)),
          ...personnel.map((p) => opt(p.id, "Personnel", p.personnelNumber, p.fullName)),
        ]);
      },
      () => {
        /* the manual picker simply stays empty; tapping still works */
      },
    );
    return () => {
      live = false;
    };
  }, []);

  const full = counter.count >= counter.capacity;
  const busy = register.status === "running";

  const afterRegister = (settled: Awaited<ReturnType<typeof register.run>>) => {
    if (settled.outcome === "ignored") return;
    if (settled.outcome === "succeeded") {
      setCounter({ count: settled.data.registeredCount, capacity: settled.data.capacity });
      setCardUid("");
      setChosen(null);
      registrants.reload();
      setNotice({ severity: "success", text: "Attendee registered." });
    } else {
      setNotice({ severity: "error", text: describeApiError(settled.error) });
    }
  };

  const registerManual = () => {
    if (chosen === null) return;
    void register.run({ method: "Manual", type: chosen.kind, attendeeId: chosen.id }).then(afterRegister);
  };

  const registerTap = () => {
    if (cardUid.trim() === "") return;
    void register.run({ method: "Tapped", cardUid }).then(afterRegister);
  };

  const removeOne = (r: PreRegistrant) => {
    void remove.run(r.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      if (settled.outcome === "succeeded") {
        setCounter({ count: settled.data.registeredCount, capacity: settled.data.capacity });
        registrants.reload();
      } else {
        setNotice({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const rows = registrants.data ?? [];

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md">
      <DialogTitle>
        {session.name}
        <Typography variant="body2" color="text.secondary">
          Audience: {session.audienceName}
        </Typography>
      </DialogTitle>
      <DialogContent dividers>
        <Stack direction="row" alignItems="center" spacing={2} sx={{ mb: 2 }}>
          <Chip
            color={full ? "warning" : "primary"}
            label={`Registered: ${counter.count} / ${counter.capacity}`}
          />
          {session.isClosed && <Chip label="Closed" variant="outlined" />}
        </Stack>

        {session.isClosed ? (
          <Alert severity="info" sx={{ mb: 2 }}>
            This session is closed. Reopen it from the sessions list to register more attendees.
          </Alert>
        ) : full ? (
          <Alert severity="warning" sx={{ mb: 2 }}>
            This session has reached its capacity. Remove an attendee to free a slot.
          </Alert>
        ) : (
          <Box sx={{ mb: 2 }}>
            <ToggleButtonGroup
              exclusive
              size="small"
              value={mode}
              onChange={(_e, next) => next && setMode(next)}
              sx={{ mb: 2 }}
            >
              <ToggleButton value="Manual">Manual</ToggleButton>
              <ToggleButton value="Tapped">Tap a card</ToggleButton>
            </ToggleButtonGroup>

            {mode === "Manual" ? (
              <Stack direction="row" spacing={1}>
                <Autocomplete
                  sx={{ flexGrow: 1 }}
                  options={people ?? []}
                  loading={people === undefined}
                  value={chosen}
                  onChange={(_e, next) => setChosen(next)}
                  getOptionLabel={(o) => o.label}
                  isOptionEqualToValue={(a, b) => a.id === b.id}
                  groupBy={(o) => (o.kind === "Student" ? "Students" : "Personnel")}
                  renderInput={(params) => (
                    <TextField {...params} label="Search a student or employee" />
                  )}
                />
                <Button variant="contained" onClick={registerManual} disabled={busy || chosen === null}>
                  Add
                </Button>
              </Stack>
            ) : (
              <Stack direction="row" spacing={1}>
                <TextField
                  sx={{ flexGrow: 1 }}
                  label="Card UID"
                  value={cardUid}
                  onChange={(e) => setCardUid(e.target.value)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter") registerTap();
                  }}
                  helperText="Tap or type the card, then Register."
                />
                <Button variant="contained" onClick={registerTap} disabled={busy || cardUid.trim() === ""}>
                  Register
                </Button>
              </Stack>
            )}
          </Box>
        )}

        <Divider sx={{ mb: 1 }} />

        {registrants.status === "loading" && <LinearProgress />}
        {registrants.status === "error" && (
          <ErrorState subject="registrants" error={registrants.error} onRetry={registrants.reload} />
        )}
        {registrants.status === "ready" &&
          (rows.length === 0 ? (
            <Typography variant="body2" color="text.secondary" sx={{ py: 2 }}>
              No attendees registered yet.
            </Typography>
          ) : (
            <List dense>
              {rows.map((r) => (
                <ListItem
                  key={r.id}
                  secondaryAction={
                    <IconButton
                      edge="end"
                      color="error"
                      onClick={() => removeOne(r)}
                      aria-label={`Remove ${r.fullName}`}
                      title={`Remove ${r.fullName}`}
                      disabled={remove.status === "running"}
                    >
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  }
                >
                  <ListItemText
                    primary={`${r.fullName} (${r.number})`}
                    secondary={
                      `${r.type}` +
                      (r.departmentOrProgram ? ` · ${r.departmentOrProgram}` : "") +
                      ` · ${r.method}`
                    }
                  />
                </ListItem>
              ))}
            </List>
          ))}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
      </DialogActions>

      <Snackbar
        open={notice !== undefined}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setNotice(undefined)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {notice ? (
          <Alert
            severity={notice.severity}
            role={notice.severity === "error" ? "alert" : "status"}
            onClose={() => setNotice(undefined)}
          >
            {notice.text}
          </Alert>
        ) : undefined}
      </Snackbar>
    </Dialog>
  );
}

const opt = (id: string, kind: PersonOption["kind"], number: string, fullName: string): PersonOption => ({
  id,
  kind,
  label: `${number} — ${fullName}`,
});
