// The gate every route but `/login` sits behind.
//
// It renders three things and the middle one is the one that is easy to leave out: a signed-in user
// gets the app, an anonymous one gets redirected, and a session whose state is still `unknown` gets a
// *loading* screen. Collapsing that third case into the second is the bug where pressing F5 on
// `/events/{id}` bounces a perfectly signed-in operator to the login form and loses the route they
// were on — the access token is held in memory only, so `unknown` is where every single page load
// begins. See `authSession.ts`.

import { type ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { Box, CircularProgress, Typography } from "@mui/material";

import { useAuth } from "../authContext";

/** Where an anonymous visitor is sent, and the only route that is not behind this guard. */
export const LOGIN_ROUTE = "/login";

/**
 * What `RequireAuth` puts in the redirect's history state so `Login` can send the user back.
 *
 * A `pathname`/`search` pair rather than react-router's `Location` object: history state is
 * structured-cloned by the browser, and the two strings are all the return trip needs.
 */
export interface SignInReturn {
  readonly from: string;
}

export default function RequireAuth({ children }: { children: ReactNode }) {
  const { session } = useAuth();
  const location = useLocation();

  if (session.status === "unknown") return <RestoringSession />;

  if (session.status === "anonymous") {
    const state: SignInReturn = { from: `${location.pathname}${location.search}` };
    // `replace`, so Back from the login form does not land on the guarded route that just bounced
    // them — which would bounce them again, and make Back look broken.
    return <Navigate to={LOGIN_ROUTE} replace state={state} />;
  }

  return <>{children}</>;
}

/**
 * The first paint of every page load, for as long as `POST /auth/refresh` takes.
 *
 * `role="status"` rather than silence: this replaces the whole app, and a screen-reader user who
 * heard nothing would be told the page had finished loading with nothing on it.
 */
export function RestoringSession() {
  return (
    <Box
      role="status"
      sx={{
        minHeight: "100vh",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        gap: 2,
      }}
    >
      <CircularProgress aria-hidden />
      <Typography color="text.secondary">Checking whether you are still signed in…</Typography>
    </Box>
  );
}
