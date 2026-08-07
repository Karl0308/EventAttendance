// The terms screen — D-53, and the page that retires a piece of hand-written SQL.
//
// Everything else in the operator's flow starts here. A roster is imported *against* a term, every
// academic row is term-scoped through one, and until these three routes existed nothing in the
// product could create one: `CLAUDE.md` documented an `INSERT INTO dbo.Terms` as the only way, and a
// developer carrying a database forward from before the seed row existed got an empty picker on the
// import page that refuses to stage a batch. This is the screen that answers that.
//
// The shape follows `Devices.tsx`: the page owns every write, so dismissing a dialog does not end the
// request that dialog started, and a failure that arrives after its dialog is gone is announced
// rather than lost.
//
// **There is no delete, and its absence is a decision rather than an omission.** A term with a batch
// imported against it cannot be removed without data loss, which the global no-DROP rule forbids and
// which no confirmation dialog can make safe. "Stop using this term" is retiring it — clearing the
// current flag — and that is offered on every row that holds it.

import { useLayoutEffect, useRef, useState } from "react";
import { Alert, Box, Button, Chip, IconButton, Snackbar, Stack, Typography } from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import StarIcon from "@mui/icons-material/Star";
import StarBorderIcon from "@mui/icons-material/StarBorder";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import NewTermDialog from "../components/NewTermDialog";
import EditTermDialog from "../components/EditTermDialog";
import ChangeCurrentTermDialog from "../components/ChangeCurrentTermDialog";
import { currentTermConsequence, termLabel } from "../termDraft";
import type { CurrentTermIntent } from "../termDraft";
import type { Term, TermWriteRequest } from "../types";

const loadTerms = () => api.listTerms();

/**
 * The empty state, which has to say **what to do** rather than "none". A school with no term cannot
 * import a roster at all, and this screen is the way out of that — so the message names the button.
 */
const NO_TERMS =
  "This school has no terms yet. A term — a school year and semester — has to exist before a roster " +
  "can be imported against it, and nothing else in the product creates one. Create the first one here.";

/** What a column reads as when the term has no value for it. Both dates are usually absent. */
const NO_VALUE = "—";

/** What the Snackbar is currently saying, and how loudly. As `Devices.tsx`, for the same reasons. */
interface Notice {
  severity: "success" | "error";
  text: string;
}

const NOTICE_MS = 6000;

/**
 * A failure does not time out — it is only ever announced here when the dialog that would have
 * carried it is gone, so it is the only copy the user gets. MUI reads `null` as "stay until
 * dismissed".
 */
const NO_AUTO_HIDE = null;

/** Which term the current-flag confirmation is about, and which way it would move it. */
interface CurrentChange {
  term: Term;
  intent: CurrentTermIntent;
}

export default function Terms() {
  // No deps: the read takes nothing, so it runs once per mount, and again on Retry or after a write.
  const terms = useApiResource(loadTerms, []);

  /**
   * All three writes live here rather than inside the dialogs that start them — `Devices.tsx`'s shape.
   * A dialog owning its own write ends that write when it unmounts, which turns Escape during a
   * request into a change with no reported outcome.
   */
  const create = useApiMutation((request: TermWriteRequest) => api.createTerm(request));
  const edit = useApiMutation((termId: string, request: TermWriteRequest) =>
    api.updateTerm(termId, request),
  );
  const setCurrent = useApiMutation((termId: string, isCurrent: boolean) =>
    api.setTermCurrent(termId, isCurrent),
  );

  // Mounting a dialog only while it is open is what keeps its form honest: every open starts from an
  // empty draft, or from the term as it was last read.
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Term | undefined>(undefined);
  const [changing, setChanging] = useState<CurrentChange | undefined>(undefined);

  const rows = terms.data;

  /**
   * Whether each dialog is on screen, read at the moment a write *settles* rather than from the
   * closure that started it. The state captured there is a render old, and the interesting case is
   * exactly the one where it changed in between — the user dismissed the dialog while the write was
   * in flight, so there is no longer an alert for the failure to appear in.
   */
  const createOpen = useRef(creating);
  const editOpen = useRef(editing !== undefined);
  const changeOpen = useRef(changing !== undefined);
  useLayoutEffect(() => {
    createOpen.current = creating;
    editOpen.current = editing !== undefined;
    changeOpen.current = changing !== undefined;
  });

  // Two pieces rather than one `Notice | undefined` driving both, as `Events.tsx` records: MUI keeps
  // a Snackbar's children mounted through its closing fade, so clearing the notice to close it would
  // blank the text mid-fade and leave an empty bar on screen for the length of the transition.
  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);

  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  // ------------------------------------------------------------------------------------- opening
  //
  // Every one of these resets its mutation first. A failure from an attempt the user walked away from
  // is still in the hook, and without the reset it would greet them as though it were about the thing
  // they have not decided on yet — which on this page would mean a duplicate-code refusal shown
  // against a code they have not typed.

  const openCreate = () => {
    create.reset();
    setCreating(true);
  };

  const openEdit = (term: Term) => {
    edit.reset();
    setEditing(term);
  };

  const openCurrentChange = (term: Term, intent: CurrentTermIntent) => {
    setCurrent.reset();
    setChanging({ term, intent });
  };

  // ------------------------------------------------------------------------------------ settling
  //
  // Each of these re-reads the list whichever way the write settled: a write that failed on the way
  // back may still have been applied, and a list that goes on asserting the state from before it is
  // what has someone press it a second time. On this page the re-read does a second job — the
  // duplicate-code check is made against the list, so a stale list is a check that misses.

  const submitCreate = (request: TermWriteRequest) => {
    // `run` never rejects; it answers with an outcome. The floating promise is deliberate and marked.
    void create.run(request).then((settled) => {
      // `ignored` — a create was already in flight and this submit sent nothing.
      if (settled.outcome === "ignored") return;
      terms.reload();

      if (settled.outcome === "succeeded") {
        setCreating(false);
        announce({
          severity: "success",
          // Says what is *not* true as well as what is: a new term is never current, and an operator
          // who created one expecting the import page to default to it would otherwise conclude the
          // save had not worked.
          text: `${termLabel(settled.data)} was created. It is not the current term — use “Make current” when you want it to be.`,
        });
        return;
      }

      if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const submitEdit = (termId: string, request: TermWriteRequest) => {
    void edit.run(termId, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      terms.reload();

      if (settled.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to ${termLabel(settled.data)}.` });
        return;
      }

      if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmCurrentChange = ({ term, intent }: CurrentChange) => {
    // The sentence is composed *before* the write, against the list as it was when the operator read
    // the confirmation — so what is announced afterwards is what they were told would happen, rather
    // than a re-derivation against a list that has since been re-read.
    const { settled: settledText } = currentTermConsequence(intent, term, rows ?? []);

    void setCurrent.run(term.id, intent === "make-current").then((settled) => {
      if (settled.outcome === "ignored") return;
      terms.reload();

      if (settled.outcome === "succeeded") {
        setChanging(undefined);
        announce({ severity: "success", text: settledText });
        return;
      }

      if (!changeOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  // --------------------------------------------------------------------------------------- grid

  const cols: GridColDef<Term>[] = [
    {
      field: "code",
      headerName: "Code",
      flex: 1,
      minWidth: 160,
      // Monospace: codes are compared character by character against what someone typed into another
      // screen, and a proportional font makes that a worse job than it has to be.
      renderCell: (p) => (
        <Box component="span" sx={{ fontFamily: "monospace" }}>
          {p.row.code}
        </Box>
      ),
    },
    {
      field: "isCurrent",
      headerName: "Current",
      width: 130,
      // The value the column sorts and filters on is the *label*, so ordering by this column groups
      // the terms by what the cell actually says.
      valueGetter: (_v, row) => (row.isCurrent ? "Current" : ""),
      renderCell: (p) =>
        p.row.isCurrent ? (
          // Colour is never the only carrier: the chip renders the word as well, so a monochrome
          // screen or a red-green-blind reader loses nothing.
          <Chip size="small" label="Current" color="success" variant="outlined" />
        ) : (
          // An empty cell rather than a "Not current" chip: exactly one row can carry this, so
          // labelling the other five would make the one that matters harder to find, not easier.
          <Box component="span" aria-hidden>
            {NO_VALUE}
          </Box>
        ),
    },
    { field: "schoolYear", headerName: "School year", width: 150 },
    { field: "semester", headerName: "Semester", width: 150 },
    {
      field: "startsOn",
      headerName: "Starts on",
      width: 130,
      // Rendered as the stored `YYYY-MM-DD` rather than through `toLocaleDateString`. These are
      // calendar dates with no time of day: parsing one into a `Date` gives UTC midnight, which
      // formats as the *previous* day for every browser west of Greenwich. The stored text is
      // unambiguous and is what the form round-trips.
      valueGetter: (_v, row) => row.startsOn ?? NO_VALUE,
    },
    {
      field: "endsOn",
      headerName: "Ends on",
      width: 130,
      valueGetter: (_v, row) => row.endsOn ?? NO_VALUE,
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 120,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <RowActions term={p.row} onEdit={openEdit} onChangeCurrent={openCurrentChange} />
      ),
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Terms
        </Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate}>
          Create term
        </Button>
      </Stack>

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        A term is a school year and semester. Rosters are imported against one, and every college,
        program, section and enrolment belongs to one — so a term has to exist before anything else
        can. Exactly one term can be current at a time; that is the one the roster-import page
        defaults to.
      </Typography>

      {terms.status === "loading" && <LoadingState label="Loading terms…" />}

      {terms.status === "error" && (
        <ErrorState subject="terms" error={terms.error} onRetry={terms.reload} />
      )}

      {terms.status === "ready" && (
        <>
          {/* Mounted whether or not a re-read is running, so the live region exists in the DOM before
              anything is put into it — a region inserted and populated in the same commit is
              announced unreliably. */}
          <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center", mb: 1 }}>
            {terms.refreshing && (
              <Typography variant="body2" color="text.secondary">
                Refreshing…
              </Typography>
            )}
          </Box>

          {terms.data.length === 0 ? (
            <EmptyState message={NO_TERMS} />
          ) : (
            <div style={{ height: 520, width: "100%" }}>
              <DataGrid
                rows={terms.data}
                columns={cols}
                getRowId={(r) => r.id}
                disableRowSelectionOnClick
                pageSizeOptions={[10, 25]}
                initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
              />
            </div>
          )}
        </>
      )}

      {/* Outside the `ready` branch on purpose: a re-read that fails replaces that branch with the
          error state, and a dialog living inside it would be torn down mid-write, taking the failure
          alert with it. */}
      {creating && (
        <NewTermDialog
          // `rows ?? []` rather than the ready branch's `terms.data`: this dialog outlives a failed
          // re-read, and an empty list there means "nothing to compare against", which leaves the
          // server as the only duplicate check — correct, just slower.
          terms={rows ?? []}
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <EditTermDialog
          term={editing}
          terms={rows ?? []}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {changing !== undefined && (
        <ChangeCurrentTermDialog
          term={changing.term}
          intent={changing.intent}
          terms={rows ?? []}
          onClose={() => setChanging(undefined)}
          onConfirm={() => confirmCurrentChange(changing)}
          running={setCurrent.status === "running"}
          failure={setCurrent.status === "failed" ? { error: setCurrent.error } : undefined}
        />
      )}

      {/* Outside the branches on purpose: a re-read that fails replaces the ready branch, and a
          Snackbar living inside it would take the message it was just given down with it — at exactly
          the moment the message is a failure the user needs to read. */}
      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {/* Rendered from the notice rather than defaulted from it: a fallback severity would paint a
            failure green for one render if the two ever came apart. */}
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

/**
 * The two row actions.
 *
 * Icon-only, so each carries an `aria-label` naming **both the action and the term**: a screen reader
 * reads a button's label out of context, and a grid of six rows would otherwise announce "Edit, Make
 * current" six times with nothing to tell them apart.
 *
 * The second action changes with the row, and its label says which direction it goes rather than
 * relying on a filled or hollow star to carry it. **There is no third action**: see the module note —
 * a term cannot be deleted without data loss, so retiring is the whole of stopping using one.
 */
function RowActions({
  term,
  onEdit,
  onChangeCurrent,
}: {
  term: Term;
  onEdit: (term: Term) => void;
  onChangeCurrent: (term: Term, intent: CurrentTermIntent) => void;
}) {
  const retiring = term.isCurrent;
  const label = retiring
    ? `Retire ${term.code} — stop it being the current term`
    : `Make ${term.code} the current term`;

  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton
        // 44 × 44, the smallest touch target that can be hit reliably; MUI's own default is 40 and
        // these sit side by side in a narrow column.
        sx={{ p: 1.25 }}
        onClick={() => onEdit(term)}
        aria-label={`Edit ${term.code}`}
        title={`Edit ${term.code}`}
      >
        <EditIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color={retiring ? "warning" : "primary"}
        onClick={() => onChangeCurrent(term, retiring ? "retire" : "make-current")}
        aria-label={label}
        title={label}
      >
        {retiring ? <StarIcon fontSize="small" /> : <StarBorderIcon fontSize="small" />}
      </IconButton>
    </Stack>
  );
}
