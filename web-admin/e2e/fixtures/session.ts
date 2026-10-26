// The body of `POST /auth/refresh` (and `/auth/login`): an access token plus the person it speaks for.
// Typed against `AuthUser`; `tokenType` and `expiresAt` are wire-only members `api.ts` reads or ignores.

import type { AuthUser } from "../../src/types";

export const PERMISSIONS = {
  studentsRead: "students.read",
  sisImport: "sis.import",
  eventsRead: "events.read",
  eventsWrite: "events.write",
} as const;

export const fixtureUser = (permissions: readonly string[]): AuthUser => ({
  id: "11111111-1111-4111-8111-111111111111",
  schoolId: "22222222-2222-4222-8222-222222222222",
  email: "registrar@usa.edu.ph",
  fullName: "Reg Istrar",
  permissions,
});

export const issuedSession = (permissions: readonly string[]) => ({
  accessToken: "e2e-access-token",
  tokenType: "Bearer",
  expiresAt: "2099-01-01T00:00:00Z",
  user: fixtureUser(permissions),
});
