// The three ways a screen can be, rendered once instead of three times.
//
// Loading, empty and failed are kept visually and semantically distinct on purpose: an empty list is
// an ordinary answer and says so calmly, a failure says what failed and offers a way to try again,
// and neither is allowed to look like the other.

import { useEffect, useRef } from "react";
import { Alert, AlertTitle, Box, Button, CircularProgress, Typography } from "@mui/material";
import { describeApiError } from "../api";
import { advise } from "../apiGuidance";

export function LoadingState({ label }: { label: string }) {
  return (
    <Box role="status" sx={{ display: "flex", alignItems: "center", gap: 2, py: 4 }}>
      <CircularProgress size={24} aria-hidden />
      <Typography color="text.secondary">{label}</Typography>
    </Box>
  );
}

/**
 * `role="status"` rather than plain text: an empty result usually arrives after a loading state, so
 * a screen reader user needs it announced, not silently painted where the spinner was.
 */
export function EmptyState({ message }: { message: string }) {
  return (
    <Box role="status" sx={{ py: 4, textAlign: "center", color: "text.secondary" }}>
      <Typography>{message}</Typography>
    </Box>
  );
}

/**
 * `role="alert"` so the failure is announced and not merely painted, and Retry is a real `<button>`
 * — reachable by Tab, activated by Enter and Space, with MUI's focus ring intact.
 *
 * The alert takes focus on mount, which is what makes Retry survivable. Pressing Retry unmounts this
 * component (the resource goes back to `loading`) while the button still holds focus, so focus falls
 * to `document.body`; if the retry then fails, a fresh alert appears with the keyboard user stranded
 * at the top of the document and the screen-reader user's place lost — on the *second* failure, when
 * they are already having a bad time (WCAG 2.4.3). Focusing the container puts them back on the thing
 * that just changed. It also makes the announcement independent of live-region insertion, which
 * VoiceOver/Safari handles unreliably: focused content is read whether or not the insertion was.
 */
export function ErrorState({
  subject,
  error,
  onRetry,
}: {
  /** What could not be loaded, in the user's words: "students", "the dashboard". */
  subject: string;
  error: unknown;
  onRetry: () => void;
}) {
  const container = useRef<HTMLDivElement>(null);
  const { message, retryable } = advise(error);

  // Keyed on `error`, not `[]`, and the difference only shows on the SECOND failure. Retry unmounts
  // this component; if React ever coalesces that into a re-render instead — which it is free to do,
  // and which a future refactor could cause without touching this file — a mount-only effect stops
  // firing and focus silently stays on the Retry button that just vanished from under it. Keying on
  // the error makes the announcement follow the thing being announced either way.
  useEffect(() => {
    container.current?.focus();
  }, [error]);

  return (
    <Alert
      ref={container}
      // -1 keeps it out of the Tab order — it is a focus *destination*, not a stop on the way.
      tabIndex={-1}
      severity="error"
      role="alert"
      sx={{ my: 2 }}
      action={
        // `=== "safe"`, not truthiness. `retryable` is three-valued and `"may-duplicate"` is truthy,
        // so `retryable ? …` would put a Retry button on a request that may already have been
        // applied. Nothing this component renders for is a write — every caller passes a
        // `useApiResource` failure — so the rendered behaviour is unchanged; the strict test is what
        // keeps it unchanged if a write error is ever handed here by mistake, instead of the button
        // reappearing silently.
        retryable === "safe" ? (
          <Button color="inherit" size="small" onClick={onRetry}>
            Retry
          </Button>
        ) : undefined
      }
    >
      <AlertTitle>Could not load {subject}</AlertTitle>
      <Typography variant="body2">{describeApiError(error)}</Typography>
      <Typography variant="body2" sx={{ mt: 1 }}>
        {message}
      </Typography>
    </Alert>
  );
}
