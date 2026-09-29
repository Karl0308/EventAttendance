// The two controls an event classification is described by, rendered once for both the create and the
// edit dialog. `TermFormFields`' reasoning applies unchanged: `POST` and `PUT` take the same body and
// are checked by the same server code, so copying these controls into a second dialog would put every
// label and helper sentence in two places.

import { Stack, TextField } from "@mui/material";
import type {
  EventClassificationDraft,
  ValidatedEventClassificationField,
} from "../eventClassificationDraft";

const NAME_HELP =
  "Required, and unique within this school. What kind of event this is — “Institutional Events”, " +
  "“Departmental Events”, “Organizational Events”, or one of your own. Stored exactly as typed.";

const DESCRIPTION_HELP =
  "Optional. A sentence describing which attendance this classification captures.";

interface Props {
  draft: EventClassificationDraft;
  onChange: (patch: Partial<EventClassificationDraft>) => void;
  ids: Record<ValidatedEventClassificationField, string>;
  errorFor: (field: ValidatedEventClassificationField) => string | undefined;
  onBlur: (field: ValidatedEventClassificationField) => void;
}

export function EventClassificationFormFields({ draft, onChange, ids, errorFor, onBlur }: Props) {
  const nameError = errorFor("name");

  return (
    <Stack spacing={2} sx={{ mt: 1 }}>
      <TextField
        id={ids.name}
        label="Name"
        value={draft.name}
        // No trimming or reformatting: the value in the box is the value that is sent and stored.
        onChange={(e) => onChange({ name: e.target.value })}
        onBlur={() => onBlur("name")}
        error={nameError !== undefined}
        helperText={nameError ?? NAME_HELP}
        required
        autoFocus
        autoComplete="off"
        fullWidth
      />

      <TextField
        id={ids.description}
        label="Description"
        value={draft.description}
        onChange={(e) => onChange({ description: e.target.value })}
        onBlur={() => onBlur("description")}
        error={errorFor("description") !== undefined}
        helperText={errorFor("description") ?? DESCRIPTION_HELP}
        multiline
        minRows={2}
        autoComplete="off"
        fullWidth
      />
    </Stack>
  );
}
