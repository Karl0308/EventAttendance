/** @vitest-environment happy-dom */

// The login form — reached by keyboard, announced to a screen reader, and honest about failures.
//
// ---------------------------------------------------------------------------------------------
// WHAT THIS FILE PINS
// ---------------------------------------------------------------------------------------------
//
// Mostly accessibility, and that is deliberate: it is the half of a login screen that is invisible
// to whoever built it and that nobody notices breaking. Every query below is a *role* or a *label*
// query rather than a CSS selector, so the assertions fail for the same reason a screen-reader user
// would be stuck — a field whose `<label>` stopped being associated is a field `getByLabelText`
// cannot find either.
//
// The other half is what the form is allowed to say. Every failed sign-in on this API is one 401
// with `code: InvalidCredentials` — unknown address, wrong password and deactivated account
// indistinguishably, because any finer answer is an account-enumeration oracle. So the test that
// matters is the negative one: the screen renders the server's sentence and does not invent a
// finer-grained one of its own.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

import AuthProvider from "../src/components/AuthProvider";
import Login from "../src/pages/Login";
import { LOGIN_ROUTE } from "../src/components/RequireAuth";
import { endSession, resetSessionForTests, sessionState } from "../src/authSession";

const HOME_TEXT = "The dashboard";

/** Answers whatever the form and the provider ask for, and records what went out. */
const calls: { url: string; body: string | undefined }[] = [];

function serve(reply: () => Response): void {
  vi.stubGlobal("fetch", (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), body: typeof init?.body === "string" ? init.body : undefined });
    return Promise.resolve(reply());
  });
}

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const ISSUED = {
  accessToken: "access-token-1",
  tokenType: "Bearer",
  expiresAt: "2026-08-25T09:15:00Z",
  user: {
    id: "11111111-1111-1111-1111-111111111111",
    schoolId: "22222222-2222-2222-2222-222222222222",
    email: "registrar@usa.edu.ph",
    fullName: "Reg Istrar",
    permissions: ["students.read"],
  },
};

const REFUSAL = {
  status: 401,
  title: "Sign-in failed.",
  detail: "That e-mail address and password combination was not accepted.",
  traceId: "00-testtrace-0000000000000000-01",
  code: "InvalidCredentials",
};

function renderLogin() {
  return render(
    <MemoryRouter initialEntries={[LOGIN_ROUTE]}>
      <AuthProvider>
        <Routes>
          <Route path={LOGIN_ROUTE} element={<Login />} />
          <Route path="/" element={<p>{HOME_TEXT}</p>} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

const emailField = () => screen.getByLabelText(/e-mail address/i);
const passwordField = () => screen.getByLabelText(/password/i);
const submitButton = () => screen.getByRole("button", { name: /sign in/i });

/** The form is a real `<form>`, so this is what Enter in a field does — no keydown handler needed. */
function submitForm(): void {
  const form = submitButton().closest("form");
  if (form === null) throw new Error("The submit button is not inside a <form>.");
  act(() => {
    form.requestSubmit();
  });
}

const typeInto = (field: HTMLElement, value: string) => {
  if (!(field instanceof HTMLInputElement)) throw new Error("Expected an <input>.");
  act(() => {
    // The value is set and an `input` event dispatched, which is what React's onChange listens for.
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set?.call(field, value);
    field.dispatchEvent(new Event("input", { bubbles: true }));
  });
};

beforeEach(() => {
  calls.length = 0;
  resetSessionForTests();
  // Anonymous from the start, so `AuthProvider`'s silent renewal does not run and the form is what
  // renders. The startup path itself is covered in `apiSession.test.ts`.
  endSession("never");
  serve(() => json(200, ISSUED));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the form itself", () => {
  it("has a real label on each field, so a screen reader announces what it is", () => {
    renderLogin();

    // Not a placeholder: a placeholder disappears the moment there is text in the field, which is
    // exactly when someone re-reading the form needs it.
    expect(emailField().tagName).toBe("INPUT");
    expect(passwordField().tagName).toBe("INPUT");
  });

  it("tells a password manager which field is which", () => {
    renderLogin();

    // `username` and not `email` on the first: it is the identifier half of a credential pair, and it
    // is what a manager files the saved entry under.
    expect(emailField().getAttribute("autocomplete")).toBe("username");
    expect(passwordField().getAttribute("autocomplete")).toBe("current-password");
    expect(passwordField().getAttribute("type")).toBe("password");
  });

  it("puts the cursor in the address field on arrival", () => {
    renderLogin();

    // A login form is the one screen where the user's next action is never in doubt, and a keyboard
    // user landing on `document.body` tabs past the heading every single time.
    expect(document.activeElement).toBe(emailField());
  });

  it("submits on Enter, through the form rather than a keydown handler of its own", () => {
    renderLogin();
    typeInto(emailField(), "registrar@usa.edu.ph");
    typeInto(passwordField(), "correct horse");

    submitForm();

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toContain("/auth/login");
  });

  it("does not spend a rate-limit permit on an empty field", () => {
    renderLogin();
    submitForm();

    // The account limiter deliberately has few permits, and an empty field cannot be a correct
    // credential — so the answer is already known without asking.
    expect(calls).toHaveLength(0);
    expect(emailField().getAttribute("aria-invalid")).toBe("true");
    expect(document.activeElement).toBe(emailField());
  });
});

describe("a refused sign-in", () => {
  it("announces the server's own sentence and its traceId, and says nothing finer", async () => {
    serve(() => json(401, REFUSAL));
    renderLogin();
    typeInto(emailField(), "registrar@usa.edu.ph");
    typeInto(passwordField(), "wrong");

    submitForm();
    await act(async () => {});

    const alert = screen.getByRole("alert");
    expect(alert.textContent).toContain("was not accepted");
    expect(alert.textContent).toContain("00-testtrace-0000000000000000-01");

    // The negative half, and the one worth having: the screen must not have worked out — or guessed —
    // which of the three reasons it was. The server refuses to say, on purpose.
    expect(alert.textContent).not.toMatch(/unknown|no such|deactivated|wrong password/i);

    // Focus moves to the alert, so the failure is read rather than painted below a button the user is
    // still sitting on.
    expect(document.activeElement).toBe(alert);
    expect(sessionState().status).toBe("anonymous");
  });

  it("re-enables the button, so a corrected password can be sent", async () => {
    serve(() => json(401, REFUSAL));
    renderLogin();
    typeInto(emailField(), "registrar@usa.edu.ph");
    typeInto(passwordField(), "wrong");

    submitForm();
    await act(async () => {});

    expect(submitButton().hasAttribute("disabled")).toBe(false);
  });
});

describe("a successful sign-in", () => {
  it("leaves the form for the route the visitor was heading to", async () => {
    renderLogin();
    typeInto(emailField(), "registrar@usa.edu.ph");
    typeInto(passwordField(), "correct horse");

    submitForm();
    await act(async () => {});

    // Observed through the session store rather than by an imperative navigate, so there is no window
    // in which a signed-in user is looking at a login form.
    expect(screen.getByText(HOME_TEXT)).toBeTruthy();
    expect(sessionState().status).toBe("signedIn");
  });

  it("sends the trimmed address and the password exactly as typed", async () => {
    renderLogin();
    typeInto(emailField(), "  Registrar@USA.edu.ph  ");
    typeInto(passwordField(), "  spaces matter  ");

    submitForm();
    await act(async () => {});

    // The address is trimmed because a paste brings whitespace and the server lower-cases it anyway.
    // The password is NOT touched: leading and trailing spaces are characters in it, and a client
    // that quietly trimmed them would refuse a correct password with the server's "not accepted".
    expect(calls[0].body).toBe(
      JSON.stringify({ email: "Registrar@USA.edu.ph", password: "  spaces matter  " }),
    );
  });
});
