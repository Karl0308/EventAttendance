import { useLayoutEffect, useMemo, useRef, useState } from "react";
import {
  Alert,
  Box,
  Button,
  Chip,
  IconButton,
  Snackbar,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import AddIcon from "@mui/icons-material/Add";
import EditIcon from "@mui/icons-material/Edit";
import CreditCardIcon from "@mui/icons-material/CreditCard";
import DeleteOutlineIcon from "@mui/icons-material/DeleteOutline";
import { api, describeApiError } from "../api";
import { useApiResource } from "../useApiResource";
import { useApiMutation } from "../useApiMutation";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import NewStudentDialog from "../components/NewStudentDialog";
import EditStudentDialog from "../components/EditStudentDialog";
import DeleteStudentDialog from "../components/DeleteStudentDialog";
import StudentCardsDialog from "../components/StudentCardsDialog";
import { knownStatus } from "../studentDraft";
import { STUDENT_STATUS } from "../types";
import type { Card, Student, StudentCardRequest, StudentWriteRequest } from "../types";

const loadStudents = () => api.listStudents();

const NO_STUDENTS = "No students are on file yet.";
const NO_MATCH = "No student matches that search.";

/** What a column reads as when the student has no value for it. */
const NO_VALUE = "—";

/** What the Snackbar is currently saying, and how loudly. As `Events.tsx`, for the same reasons. */
interface Notice {
  severity: "success" | "error";
  text: string;
}

/** How long a confirmation stays up. Long enough to be read, not long enough to nag. */
const NOTICE_MS = 6000;

/**
 * A failure does not time out. It is only ever announced here when the dialog that would have carried
 * it is gone, so it is the only copy the user gets. MUI reads `null` as "stay until dismissed"; the
 * Alert has its own close button.
 */
const NO_AUTO_HIDE = null;

// ---------------------------------------------------------------------------------------------
// Whether this build may edit a student at all
// ---------------------------------------------------------------------------------------------
//
// `PUT /students/{id}` is a **full replacement**, so this client can only offer the form for a student
// it can reproduce exactly. One thing stops it: a `status` outside the three `StudentWriteRequest` can
// carry, which the form would send back as `Active` — silently graduating a student back into the
// roster on a save someone made to fix a typo in their surname. A refusal rather than a fallback, for
// the reason `EventDetail.editabilityOf` records: every fallback here changes data on a save the user
// made for an unrelated reason.
//
// The name parts have no equivalent arm because they cannot get this far: `StudentDto` declares
// `firstName`/`lastName` non-nullable and `toStudent` narrows them as required, so a server that
// stopped sending them fails loudly at the seam rather than arriving here as a half-student.

type Editability = { can: true } | { can: false; reason: string };

function editabilityOf(student: Student): Editability {
  if (knownStatus(student.status) === undefined) {
    return {
      can: false,
      reason:
        `This student's status (“${student.status}”) is not one this admin build recognises, so the ` +
        "edit form cannot send it back unchanged — saving would rewrite it. This build and the API " +
        "are probably different versions.",
    };
  }
  return { can: true };
}

/**
 * The Chip colour for a student status.
 *
 * Every status used to render green, which was harmless while nothing in the SPA could produce
 * anything but `Active` and is not now: this slice adds the control that sets `Inactive` and
 * `Graduated`, so a green "Graduated" chip would be a screen contradicting itself.
 *
 * An unrecognised status gets amber — deliberately its own colour rather than folded in with one of
 * the three. A status the server adds tomorrow should look unfamiliar rather than look like a value
 * this build understands, which is exactly the conflation recorded against the events list's
 * `statusColor` and is not worth reproducing here.
 */
const statusColor = (status: string): "success" | "info" | "default" | "warning" =>
  status === STUDENT_STATUS.Active
    ? "success"
    : status === STUDENT_STATUS.Graduated
      ? "info"
      : status === STUDENT_STATUS.Inactive
        ? "default"
        : "warning";

/**
 * What the RFID column shows: the **active** cards, and only those.
 *
 * `cards[0]` was the old answer and this slice is what makes it wrong. A detach keeps the row with
 * `isActive: false` — ADR-001 D-3 needs a past tap to keep resolving to the card that produced it —
 * so the first row of a student whose card has been replaced is the card they no longer hold, and the
 * grid would show a UID that stopped working.
 */
function activeCardSummary(cards: readonly Card[]): string {
  const active = cards.filter((card) => card.isActive);
  if (active.length === 0) return NO_VALUE;
  const [first, ...rest] = active;
  return rest.length === 0 ? first.cardUid : `${first.cardUid} +${rest.length}`;
}

export default function Students() {
  // No deps: the read takes nothing, so it runs once per mount, and again on Retry or after a write.
  const students = useApiResource(loadStudents, []);
  const [search, setSearch] = useState("");

  /**
   * All five writes live here rather than inside the dialogs that start them, which is D1's shape and
   * the reason for it: a dialog that owns its own write ends that write when it unmounts, so Cancel,
   * Escape and the backdrop all have to be blocked for up to `REQUEST_TIMEOUT_MS` — fifteen seconds of
   * a keyboard user trapped in a modal — and even then browser Back unmounts it anyway.
   *
   * Not a complete cure, and worth being precise about: navigating away from `/students` unmounts this
   * page too, and that outcome is still lost to `console.debug`. Closing that would take state above
   * the router, which is a design decision rather than a fix — carried, not smuggled in.
   *
   * The student id is an argument rather than something each hook closes over, because the row a write
   * is about is chosen when the button is pressed and there is no route parameter to read it from.
   */
  const create = useApiMutation((request: StudentWriteRequest) => api.createStudent(request));
  const edit = useApiMutation((studentId: string, request: StudentWriteRequest) =>
    api.updateStudent(studentId, request),
  );
  const remove = useApiMutation((studentId: string) => api.deleteStudent(studentId));
  const attach = useApiMutation((studentId: string, request: StudentCardRequest) =>
    api.addStudentCard(studentId, request),
  );
  const detach = useApiMutation((studentId: string, cardId: string) =>
    api.removeStudentCard(studentId, cardId),
  );

  // Mounting a dialog only while it is open is what keeps its form honest: every open starts from an
  // empty or freshly-filled draft with no leftover text.
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Student | undefined>(undefined);
  const [deleting, setDeleting] = useState<Student | undefined>(undefined);

  /** The student whose cards are open, captured when the dialog opens. See `managing` below. */
  const [cardsFor, setCardsFor] = useState<Student | undefined>(undefined);
  /** The card whose detach is being confirmed. The page owns it so a settled write can clear it. */
  const [detaching, setDetaching] = useState<Card | undefined>(undefined);
  /** Successful attaches on the open dialog — keys the attach form, so a success empties it. */
  const [attached, setAttached] = useState(0);

  const rows = students.data;

  /**
   * The open student's cards as freshly as this page can supply them.
   *
   * The cards dialog is *about* the card list, so unlike the edit form it must follow a write rather
   * than hold a snapshot: prefer the row the latest read returned, and fall back to the snapshot the
   * dialog was opened with. The fallback is what stops the dialog vanishing under the user's hands
   * while a re-read is in flight or has failed — `useApiResource` drops its data on a failed re-read —
   * and it is also what happens if someone else deletes the student meanwhile, in which case the next
   * card write answers 404 and says so, which is the honest way to find out.
   */
  const managing =
    cardsFor === undefined ? undefined : (rows?.find((s) => s.id === cardsFor.id) ?? cardsFor);

  /**
   * Whether each dialog is on screen, read at the moment a write *settles* rather than from the
   * closure that started it. The state captured there is a render old, and the interesting case is
   * exactly the one where it changed in between — the user dismissed the dialog while the write was in
   * flight, so there is no longer an alert for the failure to appear in.
   */
  const createOpen = useRef(creating);
  const editOpen = useRef(editing !== undefined);
  const deleteOpen = useRef(deleting !== undefined);
  const attachOpen = useRef(cardsFor !== undefined && detaching === undefined);
  const detachOpen = useRef(cardsFor !== undefined && detaching !== undefined);
  useLayoutEffect(() => {
    createOpen.current = creating;
    editOpen.current = editing !== undefined;
    deleteOpen.current = deleting !== undefined;
    // The cards dialog shows one view at a time: the confirm view replaces the whole body, so the
    // attach form is *unmounted* while a detach is being confirmed. Both flags therefore need the
    // view as well as the dialog — `cardsFor` alone says the dialog is open, not that this form is on
    // screen. An attach that fails during a detach confirmation would otherwise be suppressed as
    // "the form will show it" by a form that is not there, and Escape from the confirm view closes
    // the dialog and loses the outcome for good.
    attachOpen.current = cardsFor !== undefined && detaching === undefined;
    detachOpen.current = cardsFor !== undefined && detaching !== undefined;
  });

  // Two pieces rather than one `Notice | undefined` driving both, as `Events.tsx` records: MUI keeps a
  // Snackbar's children mounted through its closing fade, so clearing the notice to close it would
  // blank the text mid-fade and leave an empty bar on screen for the length of the transition.
  const [notice, setNotice] = useState<Notice | undefined>(undefined);
  const [announcing, setAnnouncing] = useState(false);

  const announce = (next: Notice) => {
    setNotice(next);
    setAnnouncing(true);
  };

  const filtered = useMemo(() => {
    if (rows === undefined) return [];
    const q = search.toLowerCase().trim();
    if (!q) return rows;
    return rows.filter(
      (s) => s.fullName.toLowerCase().includes(q) || s.studentNumber.includes(q),
    );
  }, [rows, search]);

  // ------------------------------------------------------------------------------------- opening
  //
  // Every one of these resets its mutation first. A failure from an attempt the user walked away from
  // is still in the hook, and without the reset it would greet them as though it were about the form
  // they have not filled in yet. Refused while a write is in flight — see `useApiMutation.reset` — in
  // which case the reopened form correctly shows that one still running.

  const openCreate = () => {
    create.reset();
    setCreating(true);
  };

  const openEdit = (student: Student) => {
    const editable = editabilityOf(student);
    if (!editable.can) {
      // Refused by *saying so*, rather than by a greyed button. The obvious alternative — disable it
      // and put the reason in a `title` — fails twice over: a disabled button is out of the tab order,
      // so a keyboard user can never reach the explanation, and a `title` on a disabled element is not
      // reliably shown by browsers either. There is nowhere in a grid row to print the sentence, so it
      // goes where every other thing this page has to say goes. `alert`, because the user pressed a
      // button and is owed an answer.
      announce({ severity: "error", text: editable.reason });
      return;
    }
    edit.reset();
    setEditing(student);
  };

  const openDelete = (student: Student) => {
    remove.reset();
    setDeleting(student);
  };

  const openCards = (student: Student) => {
    attach.reset();
    detach.reset();
    setDetaching(undefined);
    // Back to zero, so the attach form starts empty and does NOT take focus on this open — see
    // `AttachCardForm.focusOnMount`.
    setAttached(0);
    setCardsFor(student);
  };

  const closeCards = () => {
    setCardsFor(undefined);
    // Cleared with the dialog, or reopening it would land on a confirmation for a card the user has
    // since stopped thinking about.
    setDetaching(undefined);
  };

  // ------------------------------------------------------------------------------------ settling
  //
  // Each of these re-reads the list whichever way the write settled, and the failure case is the one
  // that matters: a write that failed on the way back may still have been applied, and a list that
  // goes on asserting the state from before it is what has someone send it a second time. `reload`
  // keeps the rows already rendered and raises `refreshing` rather than dropping back to `loading`.

  const submitCreate = (request: StudentWriteRequest) => {
    // `run` never rejects; it answers with an outcome. The floating promise is deliberate and marked.
    void create.run(request).then((settled) => {
      // `ignored` — a create was already in flight and this submit sent nothing. Nothing settled, so
      // there is nothing to report and no reason to re-read: the first one is still coming.
      if (settled.outcome === "ignored") return;
      students.reload();

      if (settled.outcome === "succeeded") {
        setCreating(false);
        announce({ severity: "success", text: `Added “${settled.data.fullName}”.` });
        return;
      }

      // The dialog renders the failure in context when it is still open — beside the draft that caused
      // it, with the heading derived from what the server may have done. Announcing it here as well
      // would double it, and a Snackbar sits above the modal. When the dialog is gone, this is the
      // only place left for it.
      if (!createOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const submitEdit = (studentId: string, request: StudentWriteRequest) => {
    void edit.run(studentId, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      students.reload();

      if (settled.outcome === "succeeded") {
        setEditing(undefined);
        announce({ severity: "success", text: `Saved changes to “${settled.data.fullName}”.` });
        return;
      }

      if (!editOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmDelete = (student: Student) => {
    void remove.run(student.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      students.reload();

      if (settled.outcome === "succeeded") {
        setDeleting(undefined);
        // What survived it is said here rather than left to be assumed, for the reason the
        // confirmation gives it in full: an admin who believes a delete erased the attendance goes
        // looking for someone with database access to undo something that did not happen.
        announce({
          severity: "success",
          text: `Deleted “${student.fullName}”. Their recorded attendance is kept.`,
        });
        return;
      }

      if (!deleteOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const submitAttach = (studentId: string, request: StudentCardRequest) => {
    void attach.run(studentId, request).then((settled) => {
      if (settled.outcome === "ignored") return;
      students.reload();

      if (settled.outcome === "succeeded") {
        // Remounts the attach form, which is what empties it. The card itself appears in the list
        // above once the re-read lands.
        setAttached((n) => n + 1);
        announce({ severity: "success", text: `Card ${settled.data.cardUid} is now active.` });
        return;
      }

      if (!attachOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  const confirmDetach = (studentId: string, card: Card) => {
    void detach.run(studentId, card.id).then((settled) => {
      if (settled.outcome === "ignored") return;
      students.reload();

      if (settled.outcome === "succeeded") {
        // Back to the list view. The card is still on it, marked Detached — which is the point, and
        // is why this says "detached" rather than "removed".
        setDetaching(undefined);
        announce({
          severity: "success",
          text: `Card ${card.cardUid} was detached. It is kept in the list as history.`,
        });
        return;
      }

      if (!detachOpen.current) {
        announce({ severity: "error", text: describeApiError(settled.error) });
      }
    });
  };

  // --------------------------------------------------------------------------------------- grid

  const cols: GridColDef<Student>[] = [
    { field: "studentNumber", headerName: "Student No.", width: 130 },
    { field: "fullName", headerName: "Name", flex: 1, minWidth: 180 },
    { field: "course", headerName: "Course", width: 90 },
    { field: "yearLevel", headerName: "Year", width: 110 },
    { field: "section", headerName: "Sec", width: 70 },
    {
      field: "cards",
      headerName: "RFID Card",
      width: 150,
      sortable: false,
      valueGetter: (_v, row) => activeCardSummary(row.cards),
    },
    {
      field: "status",
      headerName: "Status",
      width: 120,
      renderCell: (p) => (
        <Chip size="small" label={p.value} color={statusColor(String(p.value))} variant="outlined" />
      ),
    },
    {
      field: "actions",
      headerName: "Actions",
      width: 150,
      sortable: false,
      filterable: false,
      disableColumnMenu: true,
      renderCell: (p) => <RowActions student={p.row} onEdit={openEdit} onCards={openCards} onDelete={openDelete} />,
    },
  ];

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" sx={{ mb: 1 }}>
        <Typography variant="h5" fontWeight={700}>
          Students
        </Typography>
        <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate}>
          New student
        </Button>
      </Stack>

      {students.status === "loading" && <LoadingState label="Loading students…" />}

      {students.status === "error" && (
        <ErrorState subject="students" error={students.error} onRetry={students.reload} />
      )}

      {students.status === "ready" && (
        <>
          <Stack direction="row" spacing={2} sx={{ mb: 2 }} alignItems="center">
            <TextField
              size="small"
              label="Search name or student no."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              sx={{ width: 320 }}
            />
            {/* Mounted whether or not a re-read is running, so the live region exists in the DOM
                before anything is put into it — a region inserted and populated in the same commit is
                announced unreliably. */}
            <Box role="status" sx={{ minHeight: 24, display: "flex", alignItems: "center" }}>
              {students.refreshing && (
                <Typography variant="body2" color="text.secondary">
                  Refreshing…
                </Typography>
              )}
            </Box>
          </Stack>
          {filtered.length === 0 ? (
            // Two different nothings, and the user is told which: an empty roster is not the same fact
            // as a search that matched none of a full one.
            <EmptyState message={students.data.length === 0 ? NO_STUDENTS : NO_MATCH} />
          ) : (
            <div style={{ height: 520, width: "100%" }}>
              <DataGrid
                rows={filtered}
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
          alert it was about to render with it. */}
      {creating && (
        <NewStudentDialog
          onClose={() => setCreating(false)}
          onSubmit={submitCreate}
          running={create.status === "running"}
          failure={create.status === "failed" ? { error: create.error } : undefined}
        />
      )}

      {editing !== undefined && (
        <EditStudentDialog
          student={editing}
          onClose={() => setEditing(undefined)}
          onSubmit={(request) => submitEdit(editing.id, request)}
          running={edit.status === "running"}
          failure={edit.status === "failed" ? { error: edit.error } : undefined}
        />
      )}

      {deleting !== undefined && (
        <DeleteStudentDialog
          name={deleting.fullName}
          activeCards={deleting.cards.filter((card) => card.isActive).length}
          onClose={() => setDeleting(undefined)}
          onConfirm={() => confirmDelete(deleting)}
          running={remove.status === "running"}
          failure={remove.status === "failed" ? { error: remove.error } : undefined}
        />
      )}

      {managing !== undefined && (
        <StudentCardsDialog
          student={managing}
          onClose={closeCards}
          attach={{
            running: attach.status === "running",
            failure: attach.status === "failed" ? { error: attach.error } : undefined,
            succeeded: attached,
            submit: (request) => submitAttach(managing.id, request),
          }}
          detach={{
            card: detaching,
            running: detach.status === "running",
            failure: detach.status === "failed" ? { error: detach.error } : undefined,
            start: setDetaching,
            cancel: () => setDetaching(undefined),
            confirm: (card) => confirmDetach(managing.id, card),
          }}
        />
      )}

      {/* Outside the branches on purpose, for the reason `EventDetail` records: a re-read that fails
          replaces the ready branch, and a Snackbar living inside it would take the message it was just
          given down with it — at exactly the moment the message is a failure the user needs to read. */}
      <Snackbar
        open={announcing}
        autoHideDuration={notice?.severity === "error" ? NO_AUTO_HIDE : NOTICE_MS}
        onClose={() => setAnnouncing(false)}
        anchorOrigin={{ vertical: "bottom", horizontal: "center" }}
      >
        {/* Rendered from the notice rather than defaulted from it: a fallback severity would paint a
            failure green for one render if the two ever came apart. `status` for a confirmation —
            something the user caused is not an interruption — and `alert` for a failure, which is the
            one case where interrupting is the point, because the dialog that would have shown it is
            gone. */}
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
 * The three row actions.
 *
 * Icon-only, so every one of them carries an `aria-label` that names **both the action and the
 * student**: a screen reader reads a button's label out of context, and a grid of sixty rows would
 * otherwise announce "Edit, Cards, Delete" sixty times with nothing to tell them apart.
 *
 * None of the three is ever disabled — including Edit, which `openEdit` can still refuse. A disabled
 * control in a grid row is the worst version of this codebase's standing complaint: there is no space
 * beside it for the reason, it drops out of the tab order so a keyboard user cannot reach an
 * explanation even if one existed, and a `title` on a disabled element is not reliably shown. Pressing
 * it and being told why is strictly more accessible than not being able to press it at all.
 */
function RowActions({
  student,
  onEdit,
  onCards,
  onDelete,
}: {
  student: Student;
  onEdit: (student: Student) => void;
  onCards: (student: Student) => void;
  onDelete: (student: Student) => void;
}) {
  return (
    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ height: "100%" }}>
      <IconButton
        // 44 × 44, which is the smallest touch target that can be hit reliably; MUI's own default is
        // 40 and these three sit side by side in a narrow column.
        sx={{ p: 1.25 }}
        onClick={() => onEdit(student)}
        aria-label={`Edit ${student.fullName}`}
        title={`Edit ${student.fullName}`}
      >
        <EditIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        onClick={() => onCards(student)}
        aria-label={`RFID cards for ${student.fullName}`}
        title={`RFID cards for ${student.fullName}`}
      >
        <CreditCardIcon fontSize="small" />
      </IconButton>

      <IconButton
        sx={{ p: 1.25 }}
        color="error"
        onClick={() => onDelete(student)}
        aria-label={`Delete ${student.fullName}`}
        title={`Delete ${student.fullName}`}
      >
        <DeleteOutlineIcon fontSize="small" />
      </IconButton>
    </Stack>
  );
}
