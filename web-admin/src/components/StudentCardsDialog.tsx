// One student's RFID cards: what they hold, attaching another, and detaching one.
//
// ---------------------------------------------------------------------------------------------
// Why detaching gets a confirmation and deleting an event gets the same treatment
// ---------------------------------------------------------------------------------------------
//
// A student who cannot tap is a student who is marked **Absent** — the close of an event materialises
// the expected roster as Absent rows, so a card detached by mistake does not fail loudly, it produces
// a plausible attendance record for someone who was there. There is no soft flag to undo it with, and
// the consequence lands later, on a different screen, to a different person. That is the shape of
// destructive this dialog has to describe.
//
// ---------------------------------------------------------------------------------------------
// Why the confirmation is a view of this dialog rather than a second dialog over it
// ---------------------------------------------------------------------------------------------
//
// Stacking a second `Dialog` on an open one means two focus traps, a restore path between them, and an
// Escape that has to unwind the right one — none of which can be *observed* in this environment (no
// browser and no screen reader; see the standing gaps in MDVault #206). One dialog with two views has
// one trap, one Escape and one restore, and the confirmation gets the whole body rather than a
// cramped inline panel. The cost is that "which view" is state, and it is the page's — see the props —
// so that a settled write can put the dialog back on the list without reaching into it.
//
// The write itself is `Students.tsx`'s, as everywhere in this slice.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { isResendUnsafe } from "../apiGuidance";
import {
  CARD_LABEL_MAX_LENGTH,
  CARD_UID_MAX_LENGTH,
  EMPTY_CARD_DRAFT,
  NO_CARD_ERRORS,
  VALIDATED_CARD_FIELDS,
  activeCardWithUid,
  cardFieldIdsFor,
  normalizeCardUid,
  validateCard,
} from "../studentDraft";
import type { CardDraft, CardFieldErrors, ValidatedCardField } from "../studentDraft";
import type { Card, Student, StudentCardRequest } from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const FIELD_ID = cardFieldIdsFor("student");
const DIALOG_TITLE_ID = "student-cards-dialog-title";
const DIALOG_BODY_ID = "student-cards-dialog-body";

const HEADING_NOT_ASSIGNED = "The card was not assigned";
const HEADING_MAYBE_ASSIGNED = "The card may have been assigned";
const HEADING_NOT_DETACHED = "The card was not detached";
const HEADING_MAYBE_DETACHED = "The card may have been detached";

/**
 * Withheld resend for the attach.
 *
 * Worth being precise rather than reciting the generic sentence, because this POST is the one write in
 * the app that is genuinely safe to repeat *for this exact request*: `AddCardAsync` answers a UID the
 * same student already holds with the card they already have, so a second send cannot produce a second
 * card. `advise()` still withholds the one-click resend — `shape` is a proxy for idempotency and is
 * exact only for POST, so it errs safe — and what is actually unknown is the thing the sentence says.
 */
const ATTACH_RESEND_WITHHELD =
  "Attach is disabled because this build cannot tell whether the card was assigned. A second attempt " +
  "could not create a duplicate — the API answers a card this student already holds with that same " +
  "card — but the list above is the way to find out, and it is re-read after every failed attempt.";

const DETACH_RESEND_WITHHELD =
  "Detach is disabled because this build cannot tell whether the card was detached. Close this and " +
  "look at the list — a detached card is still listed, marked “Detached”.";

/** Said where the list would otherwise just be empty, because an empty list here has a consequence. */
const NO_CARDS =
  "This student holds no active card, so they cannot tap in. Any event closed while that is true " +
  "records them as Absent.";

/** Why a detached card is still on the screen — otherwise it reads as a delete that did not work. */
const HISTORY_NOTE =
  "Detached cards are kept rather than deleted, so the issuance history survives and every tap " +
  "already recorded against one still resolves to the physical card that produced it (ADR-001 D-3).";

interface StudentCardsDialogProps {
  /**
   * The student, as freshly as the page can supply them — **not** a snapshot, unlike the edit form's.
   * The whole content of this dialog is the card list, so it has to follow a write; the one thing that
   * must not be re-derived from it is the attach draft, and that is local state below.
   */
  student: Student;
  onClose: () => void;
  attach: {
    running: boolean;
    failure: { error: unknown } | undefined;
    /** How many attaches have succeeded on this dialog. Keys the form, so a success empties it. */
    succeeded: number;
    submit: (request: StudentCardRequest) => void;
  };
  detach: {
    /** The card being confirmed, or `undefined` for the list view. The page owns which view this is. */
    card: Card | undefined;
    running: boolean;
    failure: { error: unknown } | undefined;
    start: (card: Card) => void;
    cancel: () => void;
    confirm: (card: Card) => void;
  };
}

export default function StudentCardsDialog({
  student,
  onClose,
  attach,
  detach,
}: StudentCardsDialogProps) {
  const active = student.cards.filter((card) => card.isActive);
  const detached = student.cards.filter((card) => !card.isActive);

  return (
    <Dialog
      open
      onClose={onClose}
      fullWidth
      maxWidth="sm"
      aria-labelledby={DIALOG_TITLE_ID}
      // Described as well as labelled while a detach is being confirmed: there the consequences *are*
      // the content, and a confirmation announced by its title alone is the one that gets agreed to.
      aria-describedby={detach.card !== undefined ? DIALOG_BODY_ID : undefined}
    >
      {detach.card !== undefined ? (
        <ConfirmDetach
          card={detach.card}
          isOnlyActiveCard={active.length === 1}
          onCancel={detach.cancel}
          onConfirm={() => {
            // Narrowed here rather than inside the handler prop, so the card the user read about is
            // provably the card that gets sent.
            if (detach.card !== undefined) detach.confirm(detach.card);
          }}
          running={detach.running}
          failure={detach.failure}
        />
      ) : (
        <>
          <DialogTitle id={DIALOG_TITLE_ID}>RFID cards — {student.fullName}</DialogTitle>

          <DialogContent>
            {active.length === 0 ? (
              // `role="status"`: it describes the screen the user just opened rather than something
              // that went wrong, and it is on screen before they touch anything.
              <Alert severity="warning" role="status" sx={{ mb: 2 }}>
                <Typography variant="body2">{NO_CARDS}</Typography>
              </Alert>
            ) : (
              <Stack spacing={1} sx={{ mb: 2 }}>
                {active.map((card) => (
                  <CardRow key={card.id} card={card} onDetach={() => detach.start(card)} />
                ))}
              </Stack>
            )}

            {detached.length > 0 && (
              <Box sx={{ mb: 2 }}>
                <Typography variant="subtitle2" component="h3" gutterBottom>
                  Previously issued
                </Typography>
                <Stack spacing={1}>
                  {detached.map((card) => (
                    <CardRow key={card.id} card={card} onDetach={undefined} />
                  ))}
                </Stack>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  {HISTORY_NOTE}
                </Typography>
              </Box>
            )}

            {/* Keyed on the number of successful attaches, so a success empties the two boxes by
                remounting the form rather than by an effect watching for the moment they settle. The
                remount is also what returns focus to the UID box — see `focusOnMount`. */}
            <AttachCardForm
              key={attach.succeeded}
              focusOnMount={attach.succeeded > 0}
              cards={student.cards}
              running={attach.running}
              failure={attach.failure}
              onSubmit={attach.submit}
            />
          </DialogContent>

          <DialogActions>
            <Button onClick={onClose}>Done</Button>
          </DialogActions>
        </>
      )}
    </Dialog>
  );
}

/**
 * One card. The UID is monospaced because it is a serial that gets compared character by character
 * against a physical card, and a proportional font makes `0`/`O` and `1`/`l` a coin toss.
 */
function CardRow({ card, onDetach }: { card: Card; onDetach: (() => void) | undefined }) {
  return (
    <Stack
      direction="row"
      spacing={2}
      alignItems="center"
      justifyContent="space-between"
      sx={{ border: 1, borderColor: "divider", borderRadius: 1, px: 2, py: 1 }}
    >
      <Box sx={{ minWidth: 0 }}>
        <Typography fontFamily="monospace" sx={{ wordBreak: "break-all" }}>
          {card.cardUid}
        </Typography>
        {card.label !== undefined && (
          <Typography variant="body2" color="text.secondary">
            {card.label}
          </Typography>
        )}
      </Box>

      {onDetach === undefined ? (
        <Chip size="small" label="Detached" />
      ) : (
        <Button
          size="small"
          color="error"
          onClick={onDetach}
          // The visible label is "Detach"; a screen reader reads a button's label out of context, and
          // a dialog with three of them would announce "Detach, Detach, Detach". Naming the UID is
          // what makes them tell apart.
          aria-label={`Detach card ${card.cardUid}`}
        >
          Detach
        </Button>
      )}
    </Stack>
  );
}

/** What the user is agreeing to, at the size the consequences deserve. */
function ConfirmDetach({
  card,
  isOnlyActiveCard,
  onCancel,
  onConfirm,
  running,
  failure,
}: {
  card: Card;
  isOnlyActiveCard: boolean;
  onCancel: () => void;
  onConfirm: () => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}) {
  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  return (
    <>
      <DialogTitle id={DIALOG_TITLE_ID}>Detach card {card.cardUid}?</DialogTitle>

      <DialogContent>
        <DialogContentText id={DIALOG_BODY_ID}>
          The card stops working straight away: it will no longer identify this student at a reader.
          {isOnlyActiveCard
            ? " It is their only active card, so until another is attached they have no way to tap in — and an event closed while that is true records them as Absent."
            : " They keep their other active cards."}
        </DialogContentText>

        <DialogContentText sx={{ mt: 2 }}>
          The card is <strong>not deleted</strong>. Its row is kept and stays listed as “Detached”, so
          the issuance history and every tap already recorded against it survive — which is why this
          is a detach rather than a delete. The UID becomes free to issue to someone else, and
          attaching this same card to this student again is possible: it is issued afresh, and the
          detached row stays as history.
        </DialogContentText>

        {failure !== undefined && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_DETACHED}
            mayHaveApplied={HEADING_MAYBE_DETACHED}
            resendWithheld={DETACH_RESEND_WITHHELD}
          />
        )}
      </DialogContent>

      <DialogActions>
        {/* Keep takes focus, not Detach — the destructive control is one Tab away rather than under a
            keyboard user's finger, so an Enter pressed out of habit backs out. `autoFocus` lands
            because this button is newly mounted when the view switches, not merely re-rendered. */}
        <Button onClick={onCancel} autoFocus>
          Keep card
        </Button>
        <Button
          onClick={onConfirm}
          variant="contained"
          color="error"
          disabled={running || resendUnsafe}
        >
          {running ? "Detaching…" : "Detach card"}
        </Button>
      </DialogActions>
    </>
  );
}

/**
 * The attach form, as its own component so that a successful attach can empty it by remounting.
 *
 * Its own `<form>` rather than one wrapping the whole dialog: the detach buttons above sit outside it,
 * and Enter from the UID box should attach a card rather than do whatever the first button in a
 * dialog-wide form happens to be.
 */
function AttachCardForm({
  focusOnMount,
  cards,
  running,
  failure,
  onSubmit,
}: {
  focusOnMount: boolean;
  cards: readonly Card[];
  running: boolean;
  failure: { error: unknown } | undefined;
  onSubmit: (request: StudentCardRequest) => void;
}) {
  const [draft, setDraft] = useState<CardDraft>(EMPTY_CARD_DRAFT);
  const [touched, setTouched] = useState<ReadonlySet<ValidatedCardField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const checked = validateCard(draft);
  const errors: CardFieldErrors = checked.ok ? NO_CARD_ERRORS : checked.errors;

  const errorFor = (field: ValidatedCardField): string | undefined =>
    submitAttempted || touched.has(field) ? errors[field] : undefined;

  const markTouched = (field: ValidatedCardField) =>
    setTouched((current) => new Set(current).add(field));

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  // What will actually be stored, shown as the user types. A reader emits `04:a7:b8:c9` and the column
  // holds `04A7B8C9`; without this the value in the list afterwards is a surprise rather than a
  // confirmation, and "is this the card I just added" becomes a question.
  const normalized = normalizeCardUid(draft.cardUid);
  const alreadyHeld = activeCardWithUid(cards, normalized);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validateCard(draft);
    if (!now.ok) {
      const firstInvalid = VALIDATED_CARD_FIELDS.find((field) => now.errors[field] !== undefined);
      if (firstInvalid !== undefined) document.getElementById(FIELD_ID[firstInvalid])?.focus();
      return;
    }

    onSubmit(now.request);
  };

  return (
    <form onSubmit={submit} noValidate>
      <Typography variant="subtitle2" component="h3" gutterBottom>
        Attach a card
      </Typography>

      {failure !== undefined && (
        <WriteFailureAlert
          error={failure.error}
          notApplied={HEADING_NOT_ASSIGNED}
          mayHaveApplied={HEADING_MAYBE_ASSIGNED}
          resendWithheld={ATTACH_RESEND_WITHHELD}
        />
      )}

      <Stack spacing={2}>
        <TextField
          id={FIELD_ID.cardUid}
          label="Card UID"
          value={draft.cardUid}
          onChange={(e) => setDraft((d) => ({ ...d, cardUid: e.target.value }))}
          onBlur={() => markTouched("cardUid")}
          error={errorFor("cardUid") !== undefined}
          helperText={
            errorFor("cardUid") ??
            (normalized === ""
              ? `Any reader format — 04:a7:b8:c9, 04-A7-B8-C9 and 04 a7 b8 c9 are one card. Up to ${CARD_UID_MAX_LENGTH} characters once separators are stripped.`
              : `Stored as ${normalized}`)
          }
          required
          // The card is read by a scanner that types, so this is where the caret should be after the
          // dialog re-opens the form on a successful attach. Not on first open: the list above is what
          // the user came to see, and stealing focus into a form past it is a jump they did not ask
          // for.
          autoFocus={focusOnMount}
          autoComplete="off"
          fullWidth
        />

        {/* Not an error: the API answers this case with a 201 and the card they already hold, so
            attaching would succeed and appear to do nothing. `role="status"` because it appears as
            the user types rather than in response to a submit. */}
        {alreadyHeld !== undefined && (
          <Alert severity="info" role="status">
            <AlertTitle>This student already holds that card</AlertTitle>
            <Typography variant="body2">
              {alreadyHeld.cardUid} is already active for them. Attaching it again is accepted and
              changes nothing — it does not create a second card.
            </Typography>
          </Alert>
        )}

        <TextField
          id={FIELD_ID.label}
          label="Label"
          value={draft.label}
          onChange={(e) => setDraft((d) => ({ ...d, label: e.target.value }))}
          onBlur={() => markTouched("label")}
          error={errorFor("label") !== undefined}
          helperText={
            errorFor("label") ??
            `Optional. What to call this card — “ID 2026”, “replacement”. Up to ${CARD_LABEL_MAX_LENGTH} characters.`
          }
          autoComplete="off"
          fullWidth
        />

        <Box>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Attaching…" : "Attach card"}
          </Button>
        </Box>
      </Stack>
    </form>
  );
}
