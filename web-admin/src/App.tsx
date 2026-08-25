import { type ReactNode } from "react";
import { Outlet, Routes, Route } from "react-router-dom";
import Layout from "./components/Layout";
import PermissionGuard, { PermissionDenied } from "./components/PermissionGuard";
import RequireAuth, { LOGIN_ROUTE } from "./components/RequireAuth";
import { PERMISSIONS, type PermissionCode } from "./permissions";
import Dashboard from "./pages/Dashboard";
import Login from "./pages/Login";
import Students from "./pages/Students";
import StudentsImport from "./pages/StudentsImport";
import Events from "./pages/Events";
import EventDetail from "./pages/EventDetail";
import Devices from "./pages/Devices";
import Terms from "./pages/Terms";

/**
 * A screen, behind the permission the API will ask it for anyway.
 *
 * The fallback is `PermissionDenied` rather than the nav entry's `null`: someone reaching a URL and
 * being shown an empty page cannot tell "you may not see this" from "the app is broken", and the two
 * deserve very different next actions.
 */
const gated = (permission: PermissionCode, page: ReactNode) => (
  <PermissionGuard permission={permission} fallback={<PermissionDenied permission={permission} />}>
    {page}
  </PermissionGuard>
);

/**
 * The signed-in half of the app: the guard, then the chrome, then whichever route matched.
 *
 * A pathless layout route rather than the wrapper `App` used to be. It puts `RequireAuth` above the
 * chrome, so an anonymous visitor never paints an app bar and a nav rail before being redirected —
 * and it keeps the route paths absolute and readable rather than relative to a splat.
 */
function Authenticated() {
  return (
    <RequireAuth>
      <Layout>
        <Outlet />
      </Layout>
    </RequireAuth>
  );
}

export default function App() {
  return (
    <Routes>
      {/* The only anonymous route, and the only one outside `Authenticated`. */}
      <Route path={LOGIN_ROUTE} element={<Login />} />

      <Route element={<Authenticated />}>
        {/* Ungated on purpose. It is where every sign-in lands, so gating it would drop an operator
            whose role happens not to include `events.read` onto a refusal with no menu entry to move
            on to. Its own reads are still answered by the server according to the token, and a 403
            among them renders as one — which is the honest version of the same fact. */}
        <Route path="/" element={<Dashboard />} />

        <Route path="/students" element={gated(PERMISSIONS.studentsRead, <Students />)} />
        {/* Declared before nothing and after `/students` only for readability — the paths are
            distinct, so order does not decide the match. No nav entry of its own: `Layout`'s
            `isActive` matches on `startsWith`, so Students stays highlighted while the import runs,
            which is where the operator came from and where they go back to.

            Gated on `sis.import` rather than on `students.write`: the roster importer is a distinct
            grant on the server (§11), and an account that may correct one student's card is not
            thereby an account that may replace the roster. */}
        <Route path="/students/import" element={gated(PERMISSIONS.sisImport, <StudentsImport />)} />

        <Route path="/events" element={gated(PERMISSIONS.eventsRead, <Events />)} />
        {/* `events.read` and not `attendance.read`, even though the live board is most of the screen:
            the route is an event, the attendance panel is a read inside it, and a token that lacks
            `attendance.read` gets a refusal in that panel rather than being kept off the page. */}
        <Route path="/events/:id" element={gated(PERMISSIONS.eventsRead, <EventDetail />)} />

        <Route path="/devices" element={gated(PERMISSIONS.devicesRead, <Devices />)} />
        {/* Its own nav entry rather than a corner of the import page: a term has to exist before a
            roster can be imported against one, so the screen that creates one cannot be reached only
            from the screen that is already blocked for want of it. */}
        <Route path="/terms" element={gated(PERMISSIONS.academicRead, <Terms />)} />
      </Route>
    </Routes>
  );
}
