// The container for "Add audience": it owns the definitions read, the picked set, and the two writes,
// and hands `EventAudienceDefinitionsPanel` everything as props.
//
// A component of its own rather than more state in `EventDetail`, which already carries four reads and
// five writes. The audience *read* is still the page's (the sections panel and the stat cards depend on
// it too), so it arrives as a prop, and a settled write asks the page to re-read via `onChanged`.
//
// ---------------------------------------------------------------------------------------------
// Three kinds of state, kept apart
// ---------------------------------------------------------------------------------------------
//
// - Server state: the definitions for the event's classification (`useApiResource`, keyed on the
//   classification id so a change re-reads), and the audience read from the page.
// - Write state: `useApiMutation` for attach and for detach.
// - UI state: the ids picked but not yet attached. Held as **ids**, and the picked objects are derived
//   from whatever the list currently offers — so when the classification changes and the list is
//   replaced, a pick that no longer exists drops out at render with no effect to synchronise it.

import { useState } from "react";
import { api } from "../api";
import { useApiMutation } from "../useApiMutation";
import { useApiResource } from "../useApiResource";
import { attachSettledText } from "../eventAudience";
import EventAudienceDefinitionsPanel from "./EventAudienceDefinitionsPanel";
import type { AudienceRead } from "./EventAudiencePanel";
import type { AudienceDefinition, EventAudienceDefinition } from "../types";

interface EventAudienceDefinitionsSectionProps {
  eventId: string;
  eventStatus: string;
  /** From `EventItem`: unset for an unclassified event. */
  eventClassificationId: string | undefined;
  eventClassificationName: string | undefined;
  read: AudienceRead;
  /** A write settled, successfully or not — re-read the audience (and whatever else shows `expected`). */
  onChanged: () => void;
  /** A confirmation, for the page's Snackbar. Failures are rendered inline by the panel instead. */
  onAnnounce: (text: string) => void;
}

const NO_DEFINITIONS: readonly AudienceDefinition[] = [];

export default function EventAudienceDefinitionsSection({
  eventId,
  eventStatus,
  eventClassificationId,
  eventClassificationName,
  read,
  onChanged,
  onAnnounce,
}: EventAudienceDefinitionsSectionProps) {
  // No classification means nothing to filter by and nothing to fetch — an unfiltered list would be
  // every classification's definitions, which is exactly what this picker must not offer.
  const definitions = useApiResource<readonly AudienceDefinition[]>(
    () =>
      eventClassificationId === undefined
        ? Promise.resolve(NO_DEFINITIONS)
        : api.listAudienceDefinitions({ eventClassificationId, includeInactive: false }),
    [eventClassificationId],
  );

  const [selectedIds, setSelectedIds] = useState<readonly string[]>([]);
  const [detaching, setDetaching] = useState<string | undefined>(undefined);

  const attach = useApiMutation((ids: readonly string[]) =>
    api.attachEventAudience(eventId, { audienceDefinitionIds: [...ids] }),
  );
  const detach = useApiMutation((definitionId: string) =>
    api.detachAudienceDefinition(eventId, definitionId),
  );

  const options =
    definitions.status === "ready"
      ? { status: "ready" as const, options: definitions.data }
      : definitions.status === "error"
        ? { status: "error" as const, error: definitions.error }
        : { status: "loading" as const };

  const selected = (definitions.status === "ready" ? definitions.data : NO_DEFINITIONS).filter((d) =>
    selectedIds.includes(d.id),
  );

  const submitAttach = () => {
    const ids = selected.map((d) => d.id);
    if (ids.length === 0) return;
    void attach.run(ids).then((settled) => {
      if (settled.outcome === "ignored") return;
      // Either way: a failed attach may still have landed, and the post is idempotent, so the re-read
      // is the honest way to find out.
      onChanged();
      if (settled.outcome === "succeeded") {
        setSelectedIds([]);
        onAnnounce(attachSettledText(settled.data));
      }
      // A failure renders inline in the panel, beside the picker it is about.
    });
  };

  const removeDefinition = (definition: EventAudienceDefinition) => {
    setDetaching(definition.audienceDefinitionId);
    void detach.run(definition.audienceDefinitionId).then((settled) => {
      setDetaching(undefined);
      if (settled.outcome === "ignored") return;
      onChanged();
      if (settled.outcome === "succeeded") {
        onAnnounce(`${definition.name} is no longer part of this event's audience.`);
      }
      // A failure renders inline in the panel (role="alert"), beside the row it is about.
    });
  };

  return (
    <EventAudienceDefinitionsPanel
      eventStatus={eventStatus}
      classification={
        eventClassificationId === undefined
          ? undefined
          : { id: eventClassificationId, name: eventClassificationName }
      }
      options={options}
      onRetryOptions={definitions.reload}
      read={read}
      selected={selected}
      onSelect={(next) => {
        // A failure from the last attempt is about the last selection, not this one.
        attach.reset();
        setSelectedIds(next.map((d) => d.id));
      }}
      attach={{
        submit: submitAttach,
        running: attach.status === "running",
        failure: attach.status === "failed" ? { error: attach.error } : undefined,
      }}
      detach={{
        running: detaching,
        failure: detach.status === "failed" ? { error: detach.error } : undefined,
        remove: removeDefinition,
      }}
    />
  );
}
