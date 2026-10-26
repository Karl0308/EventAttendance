// Event-audience wire shapes: the classification the audience is filed under, the (empty) existing
// list, and the suggestion lists behind the criteria pickers. Typed against `src/types.ts`.

import type { AudienceOptions, EventClassification } from "../../src/types";

export const classification: EventClassification = {
  id: "c-1",
  name: "Convocation",
  nameKey: "CONVOCATION",
  isActive: true,
};

export const options: AudienceOptions = {
  departments: ["College of Computer Studies"],
  programs: ["BSIT"],
  yearLevels: ["1st Year"],
  sections: ["A"],
  classifications: ["Faculty"],
  organizations: ["Red Cross Youth"],
};
