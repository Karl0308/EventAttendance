// "Add audience" — attaching reusable audience definitions to an event, beside the sections picker.
//
// Presentational and controlled: the definitions list, the audience read and both writes belong to
// `EventAudienceDefinitionsSection`, and the chosen-but-not-yet-attached set is its state too. This file
// decides only what to show.
//
// ---------------------------------------------------------------------------------------------
// The two things this panel must not let a reader believe
// ---------------------------------------------------------------------------------------------
//
// 1. **That personnel are part of the expected number.** The denominator counts students. The
//    definitions may also resolve to personnel, and that figure is an advisory, printed in its own
//    labelled line inside this card rather than folded into the chip on the sections card.
//
// 2. **That an empty picker is a fault.** Definitions are filtered by the event's classification, so an
//    unclassified event has nothing to offer by design. Saying so beats an empty dropdown.

import {
  Alert,
  AlertTitle,
  Autocomplete,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import PlaylistAddIcon from "@mui/icons-material/PlaylistAdd";
import { describeApiError } from "../api";
import { advise } from "../apiGuidance";
import {
  AUDIENCE_LOCKED_ELSEWHERE,
  NO_CLASSIFICATION_HINT,
  advisoryPersonnelText,
  audienceEditability,
  isAudienceLocked,
  removeDefinitionLabel,
} from "../eventAudience";
import type { AudienceRead } from "./EventAudiencePanel";
import type { AudienceDefinition, EventAudienceDefinition } from "../types";

/** The definitions list for the event's classification, as the states this panel renders. */
export type DefinitionOptionsRead =
  | { status: "loading" }
  | { status: "ready"; options: readonly AudienceDefinition[] }
  | { status: "error"; error: unknown };

interface EventAudienceDefinitionsPanelProps {
  /** The event's status, for the same terminal-event gate the sections panel uses. */
  eventStatus: string;
  /** The event's classification, or `undefined` when it has none. */
  classification: { id: string; name: string | undefined } | undefined;
  options: DefinitionOptionsRead;
  onRetryOptions: () => void;
  read: AudienceRead;
  /** The picked-but-not-attached definitions. Controlled by the section. */
  selected: readonly AudienceDefinition[];
  onSelect: (next: AudienceDefinition[]) => void;
  attach: {
    submit: () => void;
    running: boolean;
    failure: { error: unknown } | undefined;
  };
  detach: {
    /** The `audienceDefinitionId` whose removal is running, so exactly one button shows a spinner. */
    running: string | undefined;
    failure: { error: unknown } | undefined;
    remove: (definition: EventAudienceDefinition) => void;
  };
}

const PICKER_LABEL = "Audience definitions";
const NO_MATCHING_DEFINITIONS =
  "No active audience definitions are left for this classification — they are all attached, or none exist yet.";

export default function EventAudienceDefinitionsPanel({
  eventStatus,
  classification,
  options,
  onRetryOptions,
  read,
  selected,
  onSelect,
  attach,
  detach,
}: EventAudienceDefinitionsPanelProps) {
  const editable = audienceEditability(eventStatus);
  const audience = read.status === "ready" ? read.audience : undefined;
  const attachedIds = new Set((audience?.definitions ?? []).map((d) => d.audienceDefinitionId));

  // Active-only and not already attached. The server list is already active-only by default; checking
  // `isActive` here too means a stale or differently-configured server cannot put an inactive
  // definition in front of someone about to attach it.
  const offered =
    options.status === "ready"
      ? options.options.filter((d) => d.isActive && !attachedIds.has(d.id))
      : [];

  const hasClassification = classification !== undefined;
  const pickerDisabled =
    !editable.can ||
    !hasClassification ||
    read.status !== "ready" ||
    audience === undefined ||
    attach.running;

  const helper = !hasClassification
    ? NO_CLASSIFICATION_HINT
    : !editable.can
      ? `This event is ${eventStatus}, so audience definitions can no longer be attached or removed.`
      : `Showing active definitions for the ${classification.name ?? "event’s"} classification.`;

  return (
    <Card sx={{ mb: 3 }}>
      <CardContent>
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" sx={{ mb: 1 }}>
          <PlaylistAddIcon color="primary" aria-hidden />
          <Typography variant="h6" component="h2">
            Add audience
          </Typography>
          {classification?.name !== undefined && (
            <Chip size="small" variant="outlined" label={classification.name} />
          )}
        </Stack>

        <Stack direction={{ xs: "column", sm: "row" }} spacing={1} alignItems="flex-start">
          <Autocomplete<AudienceDefinition, true>
            multiple
            disableCloseOnSelect
            fullWidth
            size="small"
            options={offered}
            value={[...selected]}
            onChange={(_event, next) => onSelect(next)}
            getOptionLabel={(definition) => definition.name}
            isOptionEqualToValue={(a, b) => a.id === b.id}
            disabled={pickerDisabled}
            loading={hasClassification && options.status === "loading"}
            noOptionsText={
              options.status === "error" ? "The definitions could not be loaded." : NO_MATCHING_DEFINITIONS
            }
            renderOption={(props, definition) => {
              // `key` is spread by MUI inside `props`; pulled out so it is not spread into the JSX.
              const { key, ...rest } = props;
              return (
                <li key={key} {...rest}>
                  <Box>
                    <Typography>{definition.name}</Typography>
                    <Typography variant="body2" color="text.secondary">
                      {definition.audienceType}
                    </Typography>
                  </Box>
                </li>
              );
            }}
            renderInput={(params) => (
              <TextField {...params} label={PICKER_LABEL} placeholder="Type to search" helperText={helper} />
            )}
          />
          <Button
            variant="contained"
            startIcon={
              attach.running ? <CircularProgress size={16} color="inherit" aria-hidden /> : undefined
            }
            onClick={attach.submit}
            disabled={pickerDisabled || selected.length === 0}
            sx={{ whiteSpace: "nowrap", flexShrink: 0 }}
          >
            {attach.running ? "Attaching…" : "Attach selected"}
          </Button>
        </Stack>

        {hasClassification && options.status === "error" && (
          <Alert
            severity="error"
            role="alert"
            sx={{ mt: 2 }}
            action={
              advise(options.error).retryable === "safe" ? (
                <Button color="inherit" size="small" onClick={onRetryOptions}>
                  Retry
                </Button>
              ) : undefined
            }
          >
            <AlertTitle>The audience definitions could not be loaded</AlertTitle>
            <Typography variant="body2">{describeApiError(options.error)}</Typography>
          </Alert>
        )}

        {attach.failure !== undefined && (
          <FailureAlert title="Those definitions were not attached" failure={attach.failure} />
        )}
        {detach.failure !== undefined && (
          <FailureAlert title="That definition was not removed" failure={detach.failure} />
        )}

        {read.status === "loading" && (
          <Box role="status" sx={{ display: "flex", alignItems: "center", gap: 1, mt: 2 }}>
            <CircularProgress size={16} aria-hidden />
            <Typography variant="body2" color="text.secondary">
              Loading attached definitions…
            </Typography>
          </Box>
        )}

        {read.status === "error" && (
          <Alert severity="error" role="alert" sx={{ mt: 2 }}>
            <AlertTitle>The attached definitions could not be loaded</AlertTitle>
            <Typography variant="body2">{describeApiError(read.error)}</Typography>
          </Alert>
        )}

        {audience !== undefined && (
          <Box sx={{ mt: 2 }}>
            <Typography variant="subtitle2" component="h3" gutterBottom>
              Attached definitions ({audience.definitions.length})
            </Typography>
            {audience.definitions.length === 0 ? (
              <Typography variant="body2" color="text.secondary">
                No audience definitions are attached.
              </Typography>
            ) : (
              <Stack component="ul" spacing={1} sx={{ listStyle: "none", p: 0, m: 0 }}>
                {audience.definitions.map((definition) => (
                  <DefinitionRow
                    key={definition.audienceDefinitionId}
                    definition={definition}
                    canEdit={editable.can}
                    running={detach.running === definition.audienceDefinitionId}
                    onRemove={() => detach.remove(definition)}
                  />
                ))}
              </Stack>
            )}

            {audience.definitions.length > 0 && (
              // Its own labelled line, outside any figure that reads as the denominator: personnel are
              // never in `expected`, and printing this beside the chip would invite adding the two.
              <Typography variant="body2" color="text.secondary" sx={{ mt: 1.5 }}>
                {advisoryPersonnelText(audience.advisoryPersonnelCount)}
              </Typography>
            )}
          </Box>
        )}
      </CardContent>
    </Card>
  );
}

function FailureAlert({ title, failure }: { title: string; failure: { error: unknown } }) {
  // A 409 here is the event having been closed or cancelled since the page read it, which is a fact
  // `advise()` cannot name — the same reasoning the sections panel records.
  return (
    <Alert severity="error" role="alert" sx={{ mt: 2 }}>
      <AlertTitle>{isAudienceLocked(failure.error) ? "This event’s audience is now fixed" : title}</AlertTitle>
      <Typography variant="body2">
        {isAudienceLocked(failure.error) ? AUDIENCE_LOCKED_ELSEWHERE : describeApiError(failure.error)}
      </Typography>
    </Alert>
  );
}

function DefinitionRow({
  definition,
  canEdit,
  running,
  onRemove,
}: {
  definition: EventAudienceDefinition;
  canEdit: boolean;
  running: boolean;
  onRemove: () => void;
}) {
  return (
    <Stack
      component="li"
      direction="row"
      spacing={2}
      alignItems="center"
      justifyContent="space-between"
      sx={{ border: 1, borderColor: "divider", borderRadius: 1, px: 2, py: 1 }}
    >
      <Box sx={{ minWidth: 0 }}>
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
          <Typography sx={{ wordBreak: "break-word" }}>{definition.name}</Typography>
          {!definition.isActive && <Chip size="small" label="Inactive" />}
        </Stack>
        <Typography variant="body2" color="text.secondary">
          {definition.audienceType} · {definition.studentCount}{" "}
          {definition.studentCount === 1 ? "student" : "students"} · {definition.personnelCount}{" "}
          personnel
        </Typography>
      </Box>

      {canEdit && (
        <Button
          size="small"
          color="error"
          onClick={onRemove}
          disabled={running}
          aria-label={removeDefinitionLabel(definition.name)}
          startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
        >
          {running ? "Removing…" : "Remove"}
        </Button>
      )}
    </Stack>
  );
}
