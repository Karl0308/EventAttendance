// The create/edit Event Audience form — one dialog for both, because `POST /event-audiences` and
// `PUT /event-audiences/{id}` take the same body. The write itself is NOT here: `EventAudiences.tsx` owns
// it, for the reason the sibling dialogs record — a dialog that owns its own write has to block its own
// dismissal while the request runs, which traps a keyboard user.
//
// The criteria section is dynamic: the audience type decides which inputs appear (a scope for
// University-wide, one autocomplete per dimension for the roster-based types, a person picker for Specific
// individuals, several for Custom). The options are the distinct Academic Community values the server
// offers, so the form references existing roster data rather than inventing master data.

import { useEffect, useMemo, useState } from "react";
import type { FormEvent } from "react";
import {
  Alert,
  AlertTitle,
  Autocomplete,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormHelperText,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from "@mui/material";
import { api, describeApiError } from "../api";
import { isResendUnsafe } from "../apiGuidance";
import {
  AUDIENCE_SCOPES,
  AUDIENCE_TYPES,
  CLASSIFICATION_GONE,
  EMPTY_AUDIENCE_DRAFT,
  NAME_TAKEN,
  NO_AUDIENCE_ERRORS,
  NO_SCHOOL_TO_FILE_UNDER,
  VALIDATED_AUDIENCE_FIELDS,
  draftFromAudience,
  isClassificationUnavailable,
  isNameConflict,
  isNoSchoolResolved,
  validateAudience,
} from "../eventAudienceDraft";
import type {
  AudienceDraft,
  AudienceFieldErrors,
  ValidatedAudienceField,
} from "../eventAudienceDraft";
import type {
  AudienceCriteria,
  AudienceDefinition,
  AudienceDefinitionWriteRequest,
  AudienceOptions,
  AudienceScope,
  AudienceType,
  Personnel,
  Student,
} from "../types";
import { WriteFailureAlert } from "./WriteFailureAlert";

const DIALOG_TITLE_ID = "event-audience-dialog-title";
const FIELD = {
  name: "event-audience-name",
  classification: "event-audience-classification",
  type: "event-audience-type",
  criteria: "event-audience-criteria",
} as const;

const HEADING_NOT_SAVED = "The event audience was not saved";
const HEADING_MAYBE_SAVED = "The event audience may have been saved";

const RESEND_WITHHELD =
  "Save is disabled because pressing it again could create the same audience twice. Close this dialog and " +
  "check the list, which is re-read after every failed attempt, before trying again.";

/** One selectable person, flattened from a Student or Personnel for the Specific-individuals picker. */
interface PersonOption {
  id: string;
  kind: "student" | "personnel";
  label: string;
}

interface Props {
  /** Present for edit, absent for create. */
  definition?: AudienceDefinition;
  /** The active classifications this audience may be filed under. */
  classifications: readonly { id: string; name: string }[];
  /** The definitions this one's name may not collide with. */
  others: readonly AudienceDefinition[];
  onClose: () => void;
  onSubmit: (request: AudienceDefinitionWriteRequest) => void;
  running: boolean;
  failure: { error: unknown } | undefined;
}

export default function EventAudienceFormDialog({
  definition,
  classifications,
  others,
  onClose,
  onSubmit,
  running,
  failure,
}: Props) {
  const editing = definition !== undefined;

  const [draft, setDraft] = useState<AudienceDraft>(
    definition ? draftFromAudience(definition) : EMPTY_AUDIENCE_DRAFT,
  );
  const [touched, setTouched] = useState<ReadonlySet<ValidatedAudienceField>>(new Set());
  const [submitAttempted, setSubmitAttempted] = useState(false);

  const [options, setOptions] = useState<AudienceOptions | undefined>(undefined);
  const [optionsError, setOptionsError] = useState<unknown>(undefined);

  // People are only needed by the Specific-individuals picker, so they are loaded lazily the first time
  // that type is chosen rather than on every open.
  const [people, setPeople] = useState<PersonOption[] | undefined>(undefined);
  const [peopleLoading, setPeopleLoading] = useState(false);
  const [peopleError, setPeopleError] = useState<unknown>(undefined);

  useEffect(() => {
    let live = true;
    api.listAudienceOptions().then(
      (o) => live && setOptions(o),
      (e: unknown) => live && setOptionsError(e),
    );
    return () => {
      live = false;
    };
  }, []);

  const needsPeople = draft.audienceType === "SpecificIndividuals";
  useEffect(() => {
    if (!needsPeople || people !== undefined || peopleLoading) return;
    setPeopleLoading(true);
    Promise.all([api.listStudents(), api.listPersonnel()]).then(
      ([students, personnel]: [Student[], Personnel[]]) => {
        setPeople([
          ...students.map((s) => person(s.id, "student", s.studentNumber, s.fullName)),
          ...personnel.map((p) => person(p.id, "personnel", p.personnelNumber, p.fullName)),
        ]);
        setPeopleLoading(false);
      },
      (e: unknown) => {
        setPeopleError(e);
        setPeopleLoading(false);
      },
    );
  }, [needsPeople, people, peopleLoading]);

  const checked = validateAudience(draft, others);
  const errors: AudienceFieldErrors = checked.ok ? NO_AUDIENCE_ERRORS : checked.errors;

  const serverNameRefusal = failure !== undefined && isNameConflict(failure.error);
  const noSchool = failure !== undefined && isNoSchoolResolved(failure.error);
  const classificationGone = failure !== undefined && isClassificationUnavailable(failure.error);

  const errorFor = (field: ValidatedAudienceField): string | undefined => {
    if (field === "name" && serverNameRefusal) return NAME_TAKEN;
    if (field === "eventClassificationId" && classificationGone) return CLASSIFICATION_GONE;
    return submitAttempted || touched.has(field) ? errors[field] : undefined;
  };

  const markTouched = (field: ValidatedAudienceField) =>
    setTouched((current) => new Set(current).add(field));

  const patch = (next: Partial<AudienceDraft>) => setDraft((d) => ({ ...d, ...next }));
  const patchCriteria = (next: Partial<AudienceCriteria>) => {
    markTouched("criteria");
    setDraft((d) => ({ ...d, criteria: { ...d.criteria, ...next } }));
  };

  const resendUnsafe = failure !== undefined && isResendUnsafe(failure.error);

  const submit = (formEvent: FormEvent<HTMLFormElement>) => {
    formEvent.preventDefault();
    setSubmitAttempted(true);

    const now = validateAudience(draft, others);
    if (!now.ok) {
      const firstInvalid = VALIDATED_AUDIENCE_FIELDS.find((f) => now.errors[f] !== undefined);
      if (firstInvalid !== undefined && firstInvalid in FIELD) {
        document.getElementById(FIELD[firstInvalid as keyof typeof FIELD])?.focus();
      }
      return;
    }

    onSubmit(now.request);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" aria-labelledby={DIALOG_TITLE_ID}>
      <form onSubmit={submit} noValidate>
        <DialogTitle id={DIALOG_TITLE_ID}>
          {editing ? `Edit “${definition.name}”` : "Create an event audience"}
        </DialogTitle>

        <DialogContent>
          {noSchool && (
            <Alert severity="error" role="alert" sx={{ mb: 2 }}>
              <AlertTitle>{HEADING_NOT_SAVED}</AlertTitle>
              <Typography variant="body2">{describeApiError(failure?.error)}</Typography>
              <Typography variant="body2" sx={{ mt: 1 }}>
                {NO_SCHOOL_TO_FILE_UNDER}
              </Typography>
            </Alert>
          )}

          {failure !== undefined && !serverNameRefusal && !noSchool && !classificationGone && (
            <WriteFailureAlert
              error={failure.error}
              notApplied={HEADING_NOT_SAVED}
              mayHaveApplied={HEADING_MAYBE_SAVED}
              resendWithheld={RESEND_WITHHELD}
            />
          )}

          <Stack spacing={2.5} sx={{ mt: 1 }}>
            <TextField
              id={FIELD.name}
              label="Name"
              required
              fullWidth
              value={draft.name}
              onChange={(e) => patch({ name: e.target.value })}
              onBlur={() => markTouched("name")}
              error={errorFor("name") !== undefined}
              helperText={errorFor("name") ?? "A short, descriptive name, such as “All BSIT Students”."}
            />

            <FormControl fullWidth required error={errorFor("eventClassificationId") !== undefined}>
              <InputLabel id={`${FIELD.classification}-label`}>Event classification</InputLabel>
              <Select
                id={FIELD.classification}
                labelId={`${FIELD.classification}-label`}
                label="Event classification"
                value={draft.eventClassificationId}
                onChange={(e) => patch({ eventClassificationId: e.target.value })}
                onBlur={() => markTouched("eventClassificationId")}
              >
                {classifications.map((c) => (
                  <MenuItem key={c.id} value={c.id}>
                    {c.name}
                  </MenuItem>
                ))}
              </Select>
              <FormHelperText>
                {errorFor("eventClassificationId") ??
                  "The kind of event this audience is offered for."}
              </FormHelperText>
            </FormControl>

            <FormControl fullWidth>
              <InputLabel id={`${FIELD.type}-label`}>Audience type</InputLabel>
              <Select
                id={FIELD.type}
                labelId={`${FIELD.type}-label`}
                label="Audience type"
                value={draft.audienceType}
                onChange={(e) => patch({ audienceType: e.target.value as AudienceType })}
              >
                {AUDIENCE_TYPES.map((t) => (
                  <MenuItem key={t.value} value={t.value}>
                    {t.label}
                  </MenuItem>
                ))}
              </Select>
              <FormHelperText>
                {AUDIENCE_TYPES.find((t) => t.value === draft.audienceType)?.hint}
              </FormHelperText>
            </FormControl>

            <CriteriaFields
              type={draft.audienceType}
              criteria={draft.criteria}
              options={options}
              optionsError={optionsError}
              people={people}
              peopleLoading={peopleLoading}
              peopleError={peopleError}
              error={errorFor("criteria")}
              onChange={patchCriteria}
            />
          </Stack>
        </DialogContent>

        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            type="submit"
            variant="contained"
            disabled={running || resendUnsafe}
            startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
          >
            {running ? "Saving…" : editing ? "Save changes" : "Create audience"}
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}

const person = (
  id: string,
  kind: PersonOption["kind"],
  number: string,
  fullName: string,
): PersonOption => ({ id, kind, label: `${number} — ${fullName}` });

// ----------------------------------------------------------------------------------- criteria fields

function CriteriaFields({
  type,
  criteria,
  options,
  optionsError,
  people,
  peopleLoading,
  peopleError,
  error,
  onChange,
}: {
  type: AudienceType;
  criteria: AudienceCriteria;
  options: AudienceOptions | undefined;
  optionsError: unknown;
  people: PersonOption[] | undefined;
  peopleLoading: boolean;
  peopleError: unknown;
  error: string | undefined;
  onChange: (next: Partial<AudienceCriteria>) => void;
}) {
  if (type === "UniversityWide") {
    return (
      <FormControl fullWidth error={error !== undefined}>
        <InputLabel id={`${FIELD.criteria}-scope-label`}>Who it covers</InputLabel>
        <Select
          id={`${FIELD.criteria}-scope`}
          labelId={`${FIELD.criteria}-scope-label`}
          label="Who it covers"
          value={criteria.scope ?? "Both"}
          onChange={(e) => onChange({ scope: e.target.value as AudienceScope })}
        >
          {AUDIENCE_SCOPES.map((s) => (
            <MenuItem key={s.value} value={s.value}>
              {s.label}
            </MenuItem>
          ))}
        </Select>
        {error !== undefined && <FormHelperText>{error}</FormHelperText>}
      </FormControl>
    );
  }

  if (type === "SpecificIndividuals") {
    return (
      <PeoplePicker
        people={people}
        loading={peopleLoading}
        loadError={peopleError}
        studentIds={criteria.studentIds ?? []}
        personnelIds={criteria.personnelIds ?? []}
        error={error}
        onChange={(studentIds, personnelIds) => onChange({ studentIds, personnelIds })}
      />
    );
  }

  // Every remaining type is one or more free-text-plus-suggestions dimension pickers.
  const dimensions = DIMENSIONS_FOR[type];

  return (
    <Stack spacing={2}>
      {optionsError !== undefined && (
        <Alert severity="warning" role="status">
          The suggestion lists could not be loaded ({describeApiError(optionsError)}). You can still type
          values by hand.
        </Alert>
      )}
      {dimensions.map((dim, index) => (
        <DimensionPicker
          key={dim.key}
          label={dim.label}
          suggestions={options?.[dim.optionKey] ?? []}
          values={criteria[dim.key] ?? []}
          onChange={(values) => onChange({ [dim.key]: values })}
          // The whole-section error rides on the first field only, so it is not repeated per row.
          error={index === 0 ? error : undefined}
        />
      ))}
    </Stack>
  );
}

type DimensionKey =
  | "departments"
  | "programs"
  | "yearLevels"
  | "sections"
  | "classifications"
  | "organizations";

interface Dimension {
  key: DimensionKey;
  optionKey: keyof AudienceOptions;
  label: string;
}

const DIM: Record<DimensionKey, Dimension> = {
  departments: { key: "departments", optionKey: "departments", label: "Departments" },
  programs: { key: "programs", optionKey: "programs", label: "Programs" },
  yearLevels: { key: "yearLevels", optionKey: "yearLevels", label: "Year levels" },
  sections: { key: "sections", optionKey: "sections", label: "Sections" },
  classifications: {
    key: "classifications",
    optionKey: "classifications",
    label: "Employee classifications",
  },
  organizations: { key: "organizations", optionKey: "organizations", label: "Organizations" },
};

const DIMENSIONS_FOR: Record<AudienceType, Dimension[]> = {
  UniversityWide: [],
  Department: [DIM.departments],
  Program: [DIM.programs],
  YearLevel: [DIM.yearLevels],
  Section: [DIM.sections],
  EmployeeClassification: [DIM.classifications],
  Organization: [DIM.organizations],
  SpecificIndividuals: [],
  Custom: [
    DIM.departments,
    DIM.programs,
    DIM.yearLevels,
    DIM.sections,
    DIM.classifications,
    DIM.organizations,
  ],
};

function DimensionPicker({
  label,
  suggestions,
  values,
  onChange,
  error,
}: {
  label: string;
  suggestions: readonly string[];
  values: readonly string[];
  onChange: (values: string[]) => void;
  error: string | undefined;
}) {
  return (
    <Autocomplete
      multiple
      freeSolo
      options={suggestions as string[]}
      value={values as string[]}
      onChange={(_e, next) => onChange(next.map((v) => v.trim()).filter((v) => v.length > 0))}
      renderTags={(tagValues, getTagProps) =>
        tagValues.map((value, index) => {
          const { key, ...rest } = getTagProps({ index });
          return <Chip key={key} label={value} size="small" {...rest} />;
        })
      }
      renderInput={(params) => (
        <TextField
          {...params}
          label={label}
          error={error !== undefined}
          helperText={error ?? "Pick from the list or type a value and press Enter."}
        />
      )}
    />
  );
}

function PeoplePicker({
  people,
  loading,
  loadError,
  studentIds,
  personnelIds,
  error,
  onChange,
}: {
  people: PersonOption[] | undefined;
  loading: boolean;
  loadError: unknown;
  studentIds: readonly string[];
  personnelIds: readonly string[];
  error: string | undefined;
  onChange: (studentIds: string[], personnelIds: string[]) => void;
}) {
  const all = people ?? [];
  const selected = useMemo(() => {
    const wanted = new Set([...studentIds, ...personnelIds]);
    return (people ?? []).filter((p) => wanted.has(p.id));
  }, [people, studentIds, personnelIds]);

  if (loadError !== undefined) {
    return (
      <Alert severity="error" role="alert">
        The people list could not be loaded ({describeApiError(loadError)}). Close this and try again.
      </Alert>
    );
  }

  return (
    <Autocomplete
      multiple
      loading={loading}
      options={all}
      value={selected}
      getOptionLabel={(o) => o.label}
      isOptionEqualToValue={(a, b) => a.id === b.id}
      groupBy={(o) => (o.kind === "student" ? "Students" : "Personnel")}
      onChange={(_e, next) =>
        onChange(
          next.filter((p) => p.kind === "student").map((p) => p.id),
          next.filter((p) => p.kind === "personnel").map((p) => p.id),
        )
      }
      renderTags={(tagValues, getTagProps) =>
        tagValues.map((value, index) => {
          const { key, ...rest } = getTagProps({ index });
          return <Chip key={key} label={value.label} size="small" {...rest} />;
        })
      }
      renderInput={(params) => (
        <TextField
          {...params}
          label="Students and employees"
          error={error !== undefined}
          helperText={
            error ??
            (loading
              ? "Loading the roster…"
              : "Search by ID number or name; students and employees are listed separately.")
          }
        />
      )}
    />
  );
}
