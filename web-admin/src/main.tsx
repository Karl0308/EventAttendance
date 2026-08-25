import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { ThemeProvider, CssBaseline } from "@mui/material";
import { theme } from "./theme";
import App from "./App";
import AuthProvider from "./components/AuthProvider";

const container = document.getElementById("root");
// Was a non-null assertion. The element is in `index.html` and the assertion was true — but it was
// true by a fact in a different file, and when it stops being true React's own error is
// "Target container is not a DOM element", which names neither the id nor where it should have been.
if (container === null) {
  throw new Error("index.html has no #root element for the SPA to mount into.");
}

createRoot(container).render(
  <StrictMode>
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <BrowserRouter basename={import.meta.env.BASE_URL}>
        {/* Inside the router because `RequireAuth` redirects and `Login` reads history state, and
            outside `App` because the session outranks routing: which route a visitor may see is
            decided by the session, not the other way round. */}
        <AuthProvider>
          <App />
        </AuthProvider>
      </BrowserRouter>
    </ThemeProvider>
  </StrictMode>,
);
