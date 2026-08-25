// Shows a thing only to a user whose token carries the permission it needs.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS IS AND IS NOT
// ---------------------------------------------------------------------------------------------
//
// **This is a convenience, not a control.** The server decides; every endpoint behind these screens
// checks the same permission again and refuses without it, and it would still refuse if this file
// were deleted. What it buys is the other half of the experience — not offering a link to a page that
// will only answer 403, and not rendering a Save button whose only outcome is a refusal.
//
// It gates on `AuthUser.permissions`, which is **what the presented token carries**, not a fresh read
// of the database. That is deliberate and it is what keeps this honest: the UI's model of what it may
// do is exactly the server's model for exactly as long as the token lives. Gating on anything fresher
// would show a button the server refuses, or hide one it would have allowed. See `AuthUserDto`.

import { type ReactNode } from "react";
import { Alert, AlertTitle, Typography } from "@mui/material";

import { useSignedInUser } from "../authContext";
import { grants, type PermissionCode } from "../permissions";

/**
 * @param permission the code the wrapped thing needs, from `PERMISSIONS`. Typed as `PermissionCode`
 *   rather than `string` because a mistyped literal is otherwise silent in the worst direction: it
 *   compiles, no token ever satisfies it, and the nav entry simply never appears for anyone.
 * @param fallback what to render instead. Defaults to nothing, which is right for a nav entry — an
 *   explanation of a page you cannot reach, in a menu, is noise. A *route* passes
 *   `<PermissionDenied>`: arriving at a URL and being shown nothing at all is indistinguishable from
 *   the app being broken.
 */
export default function PermissionGuard({
  permission,
  children,
  fallback = null,
}: {
  permission: PermissionCode;
  children: ReactNode;
  fallback?: ReactNode;
}) {
  const user = useSignedInUser();
  return <>{grants(user.permissions, permission) ? children : fallback}</>;
}

/**
 * The fallback for a whole route.
 *
 * It names the permission. That looks like a detail to leak and it is the opposite: the person
 * reading it cannot grant it to themselves, and the alternative — "you do not have permission" —
 * sends them to an administrator who then has to guess which of eleven codes was meant.
 *
 * `role="alert"` and focusable for the same reason `ErrorState` is: this replaces the page a user
 * deliberately navigated to, so it has to be announced rather than quietly painted.
 */
export function PermissionDenied({ permission }: { permission: PermissionCode }) {
  return (
    <Alert severity="warning" role="alert" tabIndex={-1} sx={{ my: 2 }}>
      <AlertTitle>This screen is not available to your account</AlertTitle>
      <Typography variant="body2">
        It needs the <code>{permission}</code> permission, which your account does not have. Ask an
        EAMS administrator to grant it; a change takes effect the next time your session renews.
      </Typography>
    </Alert>
  );
}
