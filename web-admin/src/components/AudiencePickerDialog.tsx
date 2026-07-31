// Choosing which sections an event expects.
//
// ---------------------------------------------------------------------------------------------
// Why the term is a control and not a detail
// ---------------------------------------------------------------------------------------------
//
// A section name is reused every semester against an entirely different set of students. "BSFS 2-A"
// in 2025-2026-1 and "BSFS 2-A" in 2026-2027-1 are two distinct cohorts with almost the same label,
// and the ADR-001 D-1 projection writes a fresh row for each on every term it runs for. An unscoped
// picker therefore offers several near-identical rows of which exactly one is right, and choosing
// wrong is invisible: the event attaches, the count looks plausible, and the mistake surfaces at the
// close as a denominator made of the wrong people — permanently, because ADR-003 D-13 writes it down.
//
// So the list is always scoped to one term, the term is named on screen rather than assumed, and the
// scoping happens on the wire (`?termId=`) rather than in this component. `api.sectionChoices` owns
// that, including the default: `TermDto.isCurrent` is carried on the row precisely so a picker does
// not need a second request to find it.
//
// ---------------------------------------------------------------------------------------------
// Why the counts are shown, and why the total says "at most"
// ---------------------------------------------------------------------------------------------
//
// `memberCount` is the number the organizer is actually deciding on — "invite BSCRIM 2-A" is a
// different decision at 8 students than at 80 — and a derived group showing zero is the visible signal
// that the projection has not run for its term, which would otherwise arrive as an event that expects
// nobody for no stated reason.
//
// The selected total is a ceiling and says so: a student enrolled in two attached sections is counted
// once by the server (the live denominator is a SQL `UNION`), and 12 of this school's 52 students sit
// in 2+ sections. Adding the counts up and presenting the sum as *the* expected figure would be wrong
// by a material margin on real data. The exact number comes back on the attach.
//
// The write is the page's, as everywhere in this slice: a dialog that owns its own write has to block
// its own dismissal for as long as the request runs, and that lock traps a keyboard user without
// closing the hole it was built for.

import { useState } from "react";
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { api } from "../api";
import { isResendUnsafe } from "../apiGuidance";
import { AUDIENCE_LOCKED_ELSEWHERE, isAudienceLocked } from "../eventAudience";
import { useApiResource } from "../useApiResource";
import type { EventAudienceRequest, StudentGroup } from "../types";
import { ErrorState, LoadingState } from "./ResourceStates";
import { WriteFailureAlert } from "./WriteFailureAlert";

const DIALOG_TITLE_ID = "audience-picker-dialog-title";
const TERM_FIELD_ID = "audience-picker-term";

const HEADING_NOT_ATTACHED = "The sections were not attached";
const HEADING_MAYBE_ATTACHED = "The sections may have been attached";

/**
 * Withheld resend for the attach — and this endpoint deserves a sentence of its own rather than the
 * generic apology, because it is one of the two writes in the app that is genuinely safe to repeat.
 *
 * `POST /events/{id}/attendees` is idempotent, and ADR-003 D-12 makes that a property of the schema:
 * two filtered unique indexes mean a second post of the same sections cannot produce a second row even
 * if it races the first. `advise()` still withholds the one-click resend, because `shape` is a proxy
 * for idempotency and is exact only for POST — it errs safe. What is actually unknown is what the
 * sentence says, and the list behind this dialog is where it is answered.
 */
const ATTACH_RESEND_WITHHELD =
  "Attach is disabled because this build cannot tell whether the sections were attached. A second " +
  "attempt could not attach them twice — the API answers a section already on the audience by saying " +
  "so and changing nothing — but the audience list behind this dialog is the way to find out, and it " +
  "is re-read after every failed attempt.";

/** Said where the list would otherwise just be empty, because an empty list here has a cause. */
const NO_SECTIONS_IN_TERM =
  "This term has no derived Section groups. That usually means the roster import has not been run " +
  "for it yet — sections are projected from enrolments, not created by hand.";

const NO_TERMS_AT_ALL =
  "This school has no terms, so there are no sections to choose from. Terms and their sections come " +
  "from the roster import; until one has run there is no cohort to invite.";

/** A section whose projection has produced nobody. Attaching it is legal and expects zero students. */
const EMPTY_SECTION_NOTE = "0 members — the projection has not run for this section's term.";

interface AudiencePickerDialogProps {
  /**
   * The sections already on the event, so they are shown as already there rather than offered again.
   *
   * Offering them again would not *break* anything — the post is idempotent and would answer
   * `alreadyAttached` — but a checkbox that can be ticked, submitted and change nothing is a control
   * that lies about what it does.
   */
  attachedGroupIds: ReadonlySet<string>;
  onClose: () => void;
  onSubmit: (request: EventAudienceRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function AudiencePickerDialog({
  attachedGroupIds,
  onClose,
  onSubmit,
  running,
  failure,
}: AudiencePickerDialogProps) {
  /**
   * The term the user has chosen, or `undefined` for "whichever the server says is current".
   *
   * `undefined` is deliberately not resolved into an id up here. The default is the answer to a read
   * this component has not made yet, and inventing a placeholder would mean the first render either
   * asks for a term that does not exist or asks for every term at once — which is the unscoped list
   * this dialog exists to avoid.
   */
  const [termId, setTermId] = useState<string | undefined>(undefined);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());

  // Re-read on term change, and the data on screen is a *different* term's rather than an older
  // version of this one — so `useApiResource` blanks to `loading` here rather than refreshing in
  // place, which is exactly right: keeping the previous term's sections visible under the new term's
  // label is the confusion the scoping exists to prevent.
  const choices = useApiResource(() => api.sectionChoices(termId), [termId]);

  const sections = choices.status === "ready" ? choices.data.sections : [];
  const terms = choices.status === "ready" ? choices.data.terms : [];
  // The term the rows actually belong to, which is the server's answer and not the local selection —
  // they differ on first open, and on a `termId` the server could not find.
  const shownTerm = choices.status === "ready" ? choices.data.term : undefined;

  const chosen = sections.filter((section) => selected.has(section.id));
  const atMost = chosen.reduce((total, section) => total + section.memberCount, 0);

  const toggle = (id: string) =>
    setSelected((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });

  const changeTerm = (id: string) => {
    setTermId(id);
    // Cleared on purpose. A selection is a set of ids from the term that was on screen, and carrying
    // it across would submit sections the user can no longer see — under a picker whose whole subject
    // is that two terms' sections look alike. Losing a selection is recoverable; attaching last
    // semester's cohort is not.
    setSelected(new Set());
  };

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);
  const locked = failure !== undefined && isAudienceLocked(failure.error);

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <DialogTitle id={DIALOG_TITLE_ID}>Add sections to this event’s audience</DialogTitle>

      <DialogContent>
        {/* The lock is rendered instead of the generic write failure, not beside it. A 409 here means
            the event's status moved under this screen, which `advise()` cannot say — to that taxonomy
            it is an ordinary 4xx whose advice is "send it again if the reason may have cleared", and
            this reason is one-way and cannot clear. */}
        {locked && (
          <Alert severity="error" role="alert" sx={{ mb: 2 }}>
            <AlertTitle>This event’s audience is now fixed</AlertTitle>
            <Typography variant="body2">{AUDIENCE_LOCKED_ELSEWHERE}</Typography>
          </Alert>
        )}

        {failure !== undefined && !locked && (
          <WriteFailureAlert
            error={failure.error}
            notApplied={HEADING_NOT_ATTACHED}
            mayHaveApplied={HEADING_MAYBE_ATTACHED}
            resendWithheld={ATTACH_RESEND_WITHHELD}
          />
        )}

        <TextField
          id={TERM_FIELD_ID}
          select
          label="Term"
          // The server's term, not the local selection: on first open the local one is `undefined`,
          // and rendering that as an empty select would show a blank box over a list that is very
          // definitely scoped to something.
          value={shownTerm?.id ?? ""}
          onChange={(e) => changeTerm(e.target.value)}
          helperText="Sections are listed for one term at a time — the same section name is reused every semester against a different cohort."
          disabled={choices.status !== "ready" || terms.length === 0}
          // The first meaningful control, and the one that decides what the rest of the dialog means.
          autoFocus
          fullWidth
          sx={{ mb: 2 }}
        >
          {terms.map((term) => (
            <MenuItem key={term.id} value={term.id}>
              {term.code}
              {term.isCurrent ? " (current)" : ""}
            </MenuItem>
          ))}
        </TextField>

        {choices.status === "loading" && <LoadingState label="Loading sections…" />}

        {choices.status === "error" && (
          <ErrorState subject="the sections" error={choices.error} onRetry={choices.reload} />
        )}

        {choices.status === "ready" && (
          <>
            {terms.length === 0 && (
              <Typography variant="body2" color="text.secondary" role="status">
                {NO_TERMS_AT_ALL}
              </Typography>
            )}

            {terms.length > 0 && sections.length === 0 && (
              <Typography variant="body2" color="text.secondary" role="status">
                {NO_SECTIONS_IN_TERM}
              </Typography>
            )}

            {sections.length > 0 && (
              <Stack component="ul" spacing={0} sx={{ listStyle: "none", p: 0, m: 0 }}>
                {sections.map((section) => (
                  <SectionChoice
                    key={section.id}
                    section={section}
                    alreadyAttached={attachedGroupIds.has(section.id)}
                    checked={selected.has(section.id)}
                    onToggle={() => toggle(section.id)}
                    disabled={running}
                  />
                ))}
              </Stack>
            )}
          </>
        )}

        {/* Mounted whether or not anything is selected, so the live region exists in the DOM before
            anything is put into it — a region inserted and populated in the same commit is announced
            unreliably. */}
        <Box role="status" sx={{ minHeight: 24, mt: 2 }}>
          {chosen.length > 0 && (
            <Typography variant="body2" color="text.secondary">
              {chosen.length === 1 ? "1 section" : `${chosen.length} sections`} selected — at most{" "}
              {atMost} {atMost === 1 ? "student" : "students"}. A student in two of them is expected
              once, so the event’s own count can be lower.
            </Typography>
          )}
        </Box>
      </DialogContent>

      <DialogActions>
        {/* Dismissal is the plain button and the attach is the accented one; unlike the status and
            delete confirmations, neither is one-way — a section attached by mistake can be removed
            from the panel behind this dialog — so the destructive-control-takes-focus rule does not
            apply and focus stays where MUI put it, on the term select. */}
        <Button onClick={onClose}>Leave the audience as it is</Button>
        <Button
          onClick={() => onSubmit({ studentGroupIds: chosen.map((section) => section.id) })}
          variant="contained"
          disabled={running || resendUnsafe || chosen.length === 0}
          startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {running
            ? "Attaching…"
            : `Attach ${chosen.length === 1 ? "1 section" : `${chosen.length} sections`}`}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * One section, as a choice.
 *
 * An already-attached section renders as a checked, disabled box with a chip rather than being left
 * out of the list. Hiding it would make the picker disagree with the panel behind it — the organizer
 * looking for "BSFS 2-A" would not find it and would have no way to tell whether it is missing from
 * the term or already invited.
 */
function SectionChoice({
  section,
  alreadyAttached,
  checked,
  onToggle,
  disabled,
}: {
  section: StudentGroup;
  alreadyAttached: boolean;
  checked: boolean;
  onToggle: () => void;
  disabled: boolean;
}) {
  return (
    <Box component="li" sx={{ borderBottom: 1, borderColor: "divider", py: 0.5 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" spacing={1}>
        <FormControlLabel
          control={
            <Checkbox
              checked={alreadyAttached || checked}
              onChange={onToggle}
              disabled={disabled || alreadyAttached}
              // The visible label is the section name; a screen reader reads a control's name out of
              // context, and the member count is half the decision being made.
              inputProps={{
                "aria-label": `${section.name} — ${section.memberCount} ${
                  section.memberCount === 1 ? "student" : "students"
                }${alreadyAttached ? ", already attached" : ""}`,
              }}
            />
          }
          label={
            <Box sx={{ minWidth: 0 }}>
              <Typography sx={{ wordBreak: "break-word" }}>{section.name}</Typography>
              <Typography variant="body2" color="text.secondary">
                {section.memberCount === 0
                  ? EMPTY_SECTION_NOTE
                  : `${section.memberCount} ${section.memberCount === 1 ? "student" : "students"}`}
              </Typography>
            </Box>
          }
          sx={{ flex: 1, minWidth: 0, mr: 0 }}
        />
        {alreadyAttached && <Chip size="small" label="Already attached" />}
      </Stack>
    </Box>
  );
}
