// The permission codes this SPA asks about, named once.
//
// They are a **restatement of `EamsClaimTypes`' constants on the server** (`students.read`,
// `events.write`, …), and the server is authoritative: nothing here grants anything, and a screen
// that this file says is reachable is still refused by the API if the token does not carry the code.
// What these buy is the other half — not offering a link to a page the user will only be told off
// for opening.
//
// **Named here rather than written out at each call site**, because the failure mode of a scattered
// string literal is silent in exactly one direction. A typo in `<PermissionGuard permission="student.read">`
// does not throw and does not fail to compile; it produces a guard that no token ever satisfies, so
// the nav entry simply never appears and the page it led to becomes unreachable for everyone. A
// constant that is spelt wrong is spelt wrong in one place, and `PermissionCode` makes the call sites
// that use it a compile error rather than a shrug.
//
// Not every code the server can grant is here — `attendance.capture` belongs to a capture device and
// no admin screen asks for it. `AuthUser.permissions` stays `readonly string[]` for the same reason:
// the token may carry a code this build has never heard of, and that must not fail a sign-in.

/**
 * The codes named below, as a type. `PermissionGuard` takes this rather than `string`, so a call site
 * cannot invent one.
 */
export type PermissionCode = (typeof PERMISSIONS)[keyof typeof PERMISSIONS];

export const PERMISSIONS = {
  studentsRead: "students.read",
  studentsWrite: "students.write",
  eventsRead: "events.read",
  eventsWrite: "events.write",
  attendanceRead: "attendance.read",
  attendanceWrite: "attendance.write",
  devicesRead: "devices.read",
  devicesWrite: "devices.write",
  sisImport: "sis.import",
  academicRead: "academic.read",
  academicWrite: "academic.write",
} as const;

/**
 * Whether a token carries a code.
 *
 * A plain `includes` over a small array rather than a `Set` built per render: the permission list is
 * a handful of strings and this runs while rendering a nav item, so the allocation would cost more
 * than the scan it saves.
 *
 * @param granted `AuthUser.permissions` — what the *presented token* carries. See its own note for
 *   why that is not a fresh read of the database and why the difference matters.
 */
export const grants = (granted: readonly string[], permission: PermissionCode): boolean =>
  granted.includes(permission);
