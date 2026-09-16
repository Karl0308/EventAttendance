// "Whose card is this?" — Task 2 (QA Q5/Q6), as its own dialog rather than a mode of the roster's own
// search box.
//
// ---------------------------------------------------------------------------------------------
// Why this is a separate surface instead of reusing "Search name or student no."
// ---------------------------------------------------------------------------------------------
//
// `GET /students?search=` already matches a card fragment — the backend extended it for exactly this
// requirement — so a fragment typed into the roster's own search box does find the holder. What it
// cannot do is answer the two things QA actually asked for:
//
//   - **which card matched.** `StudentDto.cards` on a matched row is that student's *whole* card
//     history, active and withdrawn alike, with nothing marking which one the fragment matched or
//     even confirming a card matched at all (the same student may have matched on name).
//   - **more than one card, more than one student.** ADR-001 D-3 leaves inactive `CardUid`s
//     unconstrained on purpose — a withdrawn serial can legitimately have been issued to several
//     people over time — and a search that returns *students* can show at most one row per person,
//     which quietly collapses "two different people once held this serial" into whichever of them the
//     roster grid happens to render.
//
// `GET /cards?cardUid=` is shaped for exactly this question instead: one row per matching *card*, the
// serial that matched, whether it is active, and who holds it — so "here's the card I found on the
// floor, whose is it and is it still live" is answerable directly rather than inferred. Keeping the
// two surfaces apart also keeps the roster's own search box doing one job: the requirement explicitly
// allows either shape, and a dedicated dialog is what lets the withdrawn-card and multi-match cases be
// shown without overloading a 320px-wide grid column with them.

import { useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { ApiError, api, describeApiError } from "../api";
import { advise } from "../apiGuidance";
import { useApiResource } from "../useApiResource";
import { useDebounced } from "../useDebounced";
import { LoadingState } from "./ResourceStates";
import type { CardMatch } from "../types";

const DIALOG_TITLE_ID = "card-lookup-dialog-title";
const FIELD_ID = "card-lookup-fragment";

/** How long the fragment box must hold still before it becomes a request — same budget as the roster's own search. */
const SEARCH_SETTLE_MS = 300;

/** Enough for a lookup dialog to hold in memory; past this the caller is told to narrow the search. */
const PAGE_SIZE = 50;
const FIRST_PAGE = 1;

/**
 * Visually-hidden but still in the accessible tree — for the results count below, which exists to be
 * *heard* and not seen: the chips and rows it summarizes are already on screen for a sighted user.
 */
const SR_ONLY_SX = {
  position: "absolute",
  width: 1,
  height: 1,
  overflow: "hidden",
  clip: "rect(0 0 0 0)",
  whiteSpace: "nowrap",
} as const;

/**
 * The `code` `CardsController.Failure` stamps on the one 400 this endpoint declares — a fragment that
 * normalizes to empty (`-`, `::`, whitespace). Branched on because `title`/`detail` are prose the
 * server rewords freely; this token is what `CardSearchOutcome.FragmentUnusable` promises stays fixed.
 */
const FRAGMENT_UNUSABLE_CODE = "FragmentUnusable";

interface CardLookupDialogProps {
  onClose: () => void;
  /**
   * "Show me this student in the roster" — closes the dialog and hands the roster's search box the
   * student number, so the two surfaces meet at the one place both already understand: the grid.
   */
  onViewStudent: (studentNumber: string) => void;
}

export default function CardLookupDialog({ onClose, onViewStudent }: CardLookupDialogProps) {
  const [fragment, setFragment] = useState("");
  const query = useDebounced(fragment.trim(), SEARCH_SETTLE_MS);

  // `undefined` while there is nothing typed yet — the load never runs the API for an empty box, which
  // would otherwise cost a guaranteed 400 on every dialog open. `useApiResource`'s `deps` is `[query]`,
  // so clearing the box back to empty is itself a "different question" and correctly blanks the result.
  const result = useApiResource(
    () => (query === "" ? Promise.resolve(undefined) : api.searchCards(query, FIRST_PAGE, PAGE_SIZE)),
    [query],
  );

  const fragmentUnusable =
    result.status === "error" &&
    result.error instanceof ApiError &&
    result.error.code === FRAGMENT_UNUSABLE_CODE;

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    // Enter does not search early — the query is still only ever `useDebounced(fragment.trim(), ...)`
    // above, on its own timer regardless of this handler. All this does is stop Enter from submitting
    // the `<form>` as a page navigation, which the browser would otherwise do by default and which
    // this single-field dialog has nowhere useful to go.
    formEvent.preventDefault();
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <DialogTitle id={DIALOG_TITLE_ID}>Find a person by card</DialogTitle>

      <DialogContent>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Type any part of a card serial, in any reader format — 04:a7:b8:c9, 04-A7-B8-C9 and 04a7b8c9
          are one search. A withdrawn card still finds its holder, shown here as “Deactivated”.
        </Typography>

        <form onSubmit={submit} noValidate>
          <TextField
            id={FIELD_ID}
            label="Card serial or fragment"
            value={fragment}
            onChange={(e) => setFragment(e.target.value)}
            autoComplete="off"
            autoFocus
            fullWidth
          />
        </form>

        <Box sx={{ mt: 2 }}>
          {query === "" ? (
            <Typography variant="body2" color="text.secondary" role="status">
              Start typing to search the card registry.
            </Typography>
          ) : result.status === "loading" ? (
            <LoadingState label="Searching cards…" />
          ) : result.status === "error" ? (
            fragmentUnusable ? (
              // A message about the input, not "no results" — an empty-after-normalization fragment
              // would otherwise match every card in the school, which is why the API refuses it rather
              // than answering an (honest but misleading) empty page.
              <Alert severity="info" role="status">
                <Typography variant="body2">{describeApiError(result.error)}</Typography>
              </Alert>
            ) : (
              <Alert severity="error" role="alert">
                <AlertTitle>Could not search cards</AlertTitle>
                <Typography variant="body2">{describeApiError(result.error)}</Typography>
                <Typography variant="body2" sx={{ mt: 1 }}>
                  {advise(result.error).message}
                </Typography>
              </Alert>
            )
          ) : result.data === undefined ? null : result.data.cards.length === 0 ? (
            <Typography role="status" color="text.secondary">
              No card matches “{query}”.
            </Typography>
          ) : (
            <Stack spacing={1}>
              {result.data.total > result.data.cards.length ? (
                // Doubles as the live announcement for this branch — a screen-reader user hears the
                // count here, so the hidden summary below is skipped rather than read twice.
                <Alert severity="info" role="status">
                  Showing the first {result.data.cards.length} of {result.data.total} matching cards.
                  Narrow the search to see the rest.
                </Alert>
              ) : (
                // The one branch with no live region otherwise (W4): loading, empty, and both error
                // states all announce themselves, but a successful, un-truncated result previously read
                // as silence — "Searching cards…" and then nothing. Hidden rather than a visible line,
                // since the chips and rows below already say this to a sighted user.
                <Typography role="status" sx={SR_ONLY_SX}>
                  {result.data.cards.length} matching {result.data.cards.length === 1 ? "card" : "cards"}{" "}
                  found.
                </Typography>
              )}
              {result.data.cards.map((card) => (
                <CardMatchRow
                  key={card.cardId}
                  card={card}
                  onViewStudent={() => {
                    onViewStudent(card.studentNumber);
                    onClose();
                  }}
                />
              ))}
            </Stack>
          )}
        </Box>
      </DialogContent>

      <DialogActions>
        <Button onClick={onClose}>Close</Button>
      </DialogActions>
    </Dialog>
  );
}

/** One matching card and the person who holds it — active or withdrawn, shown the same way either time. */
function CardMatchRow({
  card,
  onViewStudent,
}: {
  card: CardMatch;
  onViewStudent: () => void;
}) {
  return (
    <Stack
      direction="row"
      spacing={2}
      alignItems="center"
      justifyContent="space-between"
      sx={{ border: 1, borderColor: "divider", borderRadius: 1, px: 2, py: 1 }}
    >
      <Box sx={{ minWidth: 0 }}>
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography fontFamily="monospace" sx={{ wordBreak: "break-all" }}>
            {card.cardUid}
          </Typography>
          {card.isActive ? (
            <Chip size="small" label="Active" color="success" variant="outlined" />
          ) : (
            <Chip size="small" label="Deactivated" />
          )}
        </Stack>
        <Typography variant="body2" color="text.secondary">
          {card.fullName} · {card.studentNumber} · {card.studentStatus}
        </Typography>
        {!card.isActive && card.deactivatedAt !== undefined && (
          <Typography variant="caption" color="text.secondary">
            Withdrawn {new Date(card.deactivatedAt).toLocaleDateString()}
          </Typography>
        )}
      </Box>

      <Button
        size="small"
        onClick={onViewStudent}
        aria-label={`View ${card.fullName} in the roster`}
      >
        View in roster
      </Button>
    </Stack>
  );
}
