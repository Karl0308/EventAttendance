// The only anonymous route in the SPA.
//
// ---------------------------------------------------------------------------------------------
// WHAT THE SERVER WILL AND WILL NOT TELL THIS FORM
// ---------------------------------------------------------------------------------------------
//
// **Every failed sign-in is one 401 with `code: InvalidCredentials`.** Unknown address, wrong
// password and deactivated account all produce that exact status, that exact code and that exact
// prose — deliberately, because any finer answer is an account-enumeration oracle, and the server
// goes as far as verifying against a decoy hash so the response *time* does not answer it either. So
// this screen has nothing to branch on and must not invent something: it renders the server's own
// sentence and its `traceId`, and says nothing about which half was wrong.
//
// The one refusal that is genuinely different is `429`, which is the rate limiter rather than the
// credential — and `describeApiError` already renders the server's sentence for it, including how
// long to wait.

import { useEffect, useRef, useState, type FormEvent } from "react";
import { Navigate, useLocation } from "react-router-dom";
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  CircularProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from "@mui/material";

import { api, describeApiError } from "../api";
import { useAuth } from "../authContext";
import { useApiMutation } from "../useApiMutation";
import { RestoringSession } from "../components/RequireAuth";

/** Where a sign-in with no remembered destination lands. */
const DEFAULT_ROUTE = "/";

/** `id`s, so `aria-describedby` on the two fields points at something that exists. */
const EMAIL_FIELD_ID = "login-email";
const PASSWORD_FIELD_ID = "login-password";

export default function Login() {
  const { session } = useAuth();
  const location = useLocation();

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  /** Refused before anything was sent — an empty field. Cleared on the next submit, never on typing. */
  const [incomplete, setIncomplete] = useState<"email" | "password" | undefined>(undefined);

  const emailField = useRef<HTMLInputElement>(null);
  const passwordField = useRef<HTMLInputElement>(null);
  const failure = useRef<HTMLDivElement>(null);

  const signIn = useApiMutation(api.signIn);

  // The address field takes focus on arrival. A login form is the one screen where the user's next
  // action is never in doubt, and a keyboard user who lands on `document.body` has to Tab past the
  // heading to reach it every single time.
  useEffect(() => {
    emailField.current?.focus();
  }, []);

  // Keyed on the error rather than on `[]`, for the reason `ErrorState` records: the second failure
  // must move focus too, and a mount-only effect would leave a screen-reader user sitting on the
  // submit button with an alert above it they were never told about.
  useEffect(() => {
    if (signIn.status === "failed") failure.current?.focus();
  }, [signIn.status, signIn.error]);

  // The session store is the authority on where this screen sends people, and reading it here covers
  // both ways someone arrives already signed in: submitting the form (which sets the session before
  // `run` resolves) and typing `/login` in a tab that has one. No imperative navigate, so there is no
  // window in which a signed-in user is looking at a login form.
  if (session.status === "unknown") return <RestoringSession />;
  if (session.status === "signedIn") return <Navigate to={returnPathFrom(location.state)} replace />;

  const running = signIn.status === "running";

  const submit = (event: FormEvent<HTMLFormElement>) => {
    // The form is a real `<form>` and this is its `onSubmit`, which is what makes Enter in either
    // field submit it without a keydown handler of this file's own.
    event.preventDefault();

    // Checked here rather than left to the server: an empty field cannot be a correct credential, and
    // sending it spends one of the account limiter's permits — of which there are deliberately few —
    // on a request whose answer is already known.
    if (email.trim() === "") {
      setIncomplete("email");
      emailField.current?.focus();
      return;
    }
    if (password === "") {
      setIncomplete("password");
      passwordField.current?.focus();
      return;
    }

    setIncomplete(undefined);
    // The outcome is deliberately dropped: success is observed through the session store above, and
    // failure is the hook's rendered state. `run` never rejects, so there is no unhandled rejection.
    void signIn.run(email.trim(), password);
  };

  return (
    <Box
      component="main"
      sx={{ minHeight: "100vh", display: "grid", placeItems: "center", p: 2 }}
    >
      <Paper sx={{ p: 4, width: "100%", maxWidth: 420 }} elevation={3}>
        <Stack spacing={3}>
          <Box>
            <Typography variant="h5" component="h1" sx={{ fontWeight: 700 }}>
              EAMS
            </Typography>
            <Typography color="text.secondary">
              Events Attendance Monitoring — sign in to continue
            </Typography>
          </Box>

          <SessionNotice endedBecause={session.endedBecause} />

          {/* `noValidate` hands validation to the checks above rather than to the browser's bubble,
              which is not announced by every screen reader and cannot be styled or focused. */}
          <form onSubmit={submit} noValidate>
            <Stack spacing={2}>
              <TextField
                id={EMAIL_FIELD_ID}
                inputRef={emailField}
                // A real `<label>`, which is what `TextField`'s `label` renders and wires by `id`. A
                // placeholder is not one: it disappears the moment there is text in the field, which
                // is exactly when someone re-reading the form needs it.
                label="E-mail address"
                type="email"
                // `username`, not `email`. This is the identifier half of a credential pair, and it is
                // the value a password manager needs to file the entry it saves under.
                autoComplete="username"
                required
                fullWidth
                disabled={running}
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                error={incomplete === "email"}
                helperText={incomplete === "email" ? "Enter the address for your EAMS account." : " "}
              />

              <TextField
                id={PASSWORD_FIELD_ID}
                inputRef={passwordField}
                label="Password"
                type="password"
                autoComplete="current-password"
                required
                fullWidth
                disabled={running}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                error={incomplete === "password"}
                helperText={incomplete === "password" ? "Enter your password." : " "}
              />

              {signIn.status === "failed" && (
                <Alert
                  ref={failure}
                  // A focus destination, not a Tab stop — the same treatment `ErrorState` gives its
                  // alert, and for the same reason.
                  tabIndex={-1}
                  severity="error"
                  role="alert"
                >
                  <AlertTitle>Sign-in failed</AlertTitle>
                  <Typography variant="body2">{describeApiError(signIn.error)}</Typography>
                </Alert>
              )}

              <Button
                type="submit"
                variant="contained"
                size="large"
                // For the user's benefit, not for correctness: `useApiMutation`'s in-flight ref is
                // what actually drops a second submit, because `disabled` only takes effect on the
                // next commit and two events in one batch both see the old one.
                disabled={running}
                startIcon={running ? <CircularProgress size={16} color="inherit" /> : undefined}
              >
                {running ? "Signing in…" : "Sign in"}
              </Button>
            </Stack>
          </form>
        </Stack>
      </Paper>
    </Box>
  );
}

/**
 * Why this screen is being shown, when there is a reason worth saying.
 *
 * `never` says nothing at all: a first-time visitor being told "you have been signed out" is a small
 * lie that makes people check whether something is wrong. The other two are worth an announcement —
 * a user who was working a moment ago and is now looking at a login form needs to know it was the
 * session and not their last action.
 */
function SessionNotice({ endedBecause }: { endedBecause: "expired" | "signedOut" | "never" }) {
  if (endedBecause === "never") return null;

  return (
    <Alert severity="info" role="status">
      {endedBecause === "signedOut"
        ? "You have been signed out."
        : "Your session ended and could not be renewed. Sign in again to continue."}
    </Alert>
  );
}

/**
 * The route `RequireAuth` remembered, or the dashboard.
 *
 * Narrowed rather than trusted: history state is arbitrary — anyone can push any value into it from
 * the console or a crafted link — so it is read as `unknown` and checked. The leading-slash test is
 * what keeps it a route in this app: `//evil.example` is a protocol-relative URL that a router will
 * happily treat as a destination, and a login form that redirects to an attacker's host after a
 * successful sign-in is the classic shape of this bug.
 */
function returnPathFrom(state: unknown): string {
  if (typeof state !== "object" || state === null || !("from" in state)) return DEFAULT_ROUTE;

  const from = state.from;
  if (typeof from !== "string" || !from.startsWith("/") || from.startsWith("//")) {
    return DEFAULT_ROUTE;
  }

  return from;
}
