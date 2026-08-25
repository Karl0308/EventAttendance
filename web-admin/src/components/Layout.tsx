import { useState, type ReactNode } from "react";
import { Link, useLocation } from "react-router-dom";
import {
  AppBar,
  Box,
  Button,
  Drawer,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Toolbar,
  Typography,
} from "@mui/material";
import DashboardIcon from "@mui/icons-material/Dashboard";
import PeopleIcon from "@mui/icons-material/People";
import EventIcon from "@mui/icons-material/Event";
import RouterIcon from "@mui/icons-material/Router";
import CalendarMonthIcon from "@mui/icons-material/CalendarMonth";
import LogoutIcon from "@mui/icons-material/Logout";

import { useAuth, useSignedInUser } from "../authContext";
import PermissionGuard from "./PermissionGuard";
import { PERMISSIONS, type PermissionCode } from "../permissions";

const DRAWER_WIDTH = 240;

/** The landmark the skip link jumps to. */
const MAIN_CONTENT_ID = "main-content";

/**
 * A nav entry, and the permission the screen behind it needs.
 *
 * `permission` is the same code `App.tsx` gates the route on, and the pairing is the point: an entry
 * that led somewhere the user is refused is worse than no entry, because they have to click it to
 * find out. Dashboard has none — see the note on its route.
 */
interface NavEntry {
  readonly to: string;
  readonly label: string;
  readonly icon: ReactNode;
  readonly permission: PermissionCode | undefined;
}

const nav: readonly NavEntry[] = [
  { to: "/", label: "Dashboard", icon: <DashboardIcon />, permission: undefined },
  { to: "/students", label: "Students", icon: <PeopleIcon />, permission: PERMISSIONS.studentsRead },
  { to: "/events", label: "Events", icon: <EventIcon />, permission: PERMISSIONS.eventsRead },
  { to: "/devices", label: "Devices", icon: <RouterIcon />, permission: PERMISSIONS.devicesRead },
  // Last, and that is not an accident of appending: a term is created once a semester, where the
  // four above are visited daily. `isActive` matches on `startsWith`, and `/terms` shares no prefix
  // with any of them.
  {
    to: "/terms",
    label: "Terms",
    icon: <CalendarMonthIcon />,
    permission: PERMISSIONS.academicRead,
  },
];

export default function Layout({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const isActive = (to: string) => (to === "/" ? pathname === "/" : pathname.startsWith(to));

  return (
    <Box sx={{ display: "flex" }}>
      <SkipLink />

      <AppBar position="fixed" sx={{ zIndex: (t) => t.zIndex.drawer + 1 }}>
        <Toolbar>
          <Typography variant="h6" noWrap sx={{ flexGrow: 1, fontWeight: 700 }}>
            EAMS — Events Attendance Monitoring
          </Typography>
          {/* A "MOCK DATA" chip sat to the right of the title until `api.ts` stopped being a mock
              facade. Every method on it now calls the real backend, so the chip was telling an
              operator that live student PII and real device keys were fake. Removed rather than
              corrected: a banner stating the ordinary case is noise the next reader has to re-verify. */}
          <SignedInAs />
        </Toolbar>
      </AppBar>

      <Drawer
        variant="permanent"
        sx={{
          width: DRAWER_WIDTH,
          flexShrink: 0,
          [`& .MuiDrawer-paper`]: { width: DRAWER_WIDTH, boxSizing: "border-box" },
        }}
      >
        <Toolbar />
        {/* Named, because there are now two navigation landmarks on the page once the skip link is
            counted, and an unlabelled one is announced as "navigation" with nothing to tell it from
            the other. */}
        <List component="nav" aria-label="Main">
          {nav.map((n) => {
            const entry = (
              <ListItemButton
                key={n.to}
                component={Link}
                to={n.to}
                selected={isActive(n.to)}
                // `selected` is a colour. `aria-current` is the fact, and it is what a screen reader
                // announces — without it the highlighted entry is highlighted for sighted users only.
                aria-current={isActive(n.to) ? "page" : undefined}
              >
                <ListItemIcon>{n.icon}</ListItemIcon>
                <ListItemText primary={n.label} />
              </ListItemButton>
            );

            // No fallback: a menu is not the place to explain a screen you cannot open.
            return n.permission === undefined ? (
              entry
            ) : (
              <PermissionGuard key={n.to} permission={n.permission}>
                {entry}
              </PermissionGuard>
            );
          })}
        </List>
      </Drawer>

      {/* `id` and `tabIndex={-1}` together are what make the skip link land somewhere focus can
          actually go: an anchor to a non-focusable element moves the scroll position and leaves the
          keyboard where it was, so the next Tab goes back to the top of the page. */}
      <Box component="main" id={MAIN_CONTENT_ID} tabIndex={-1} sx={{ flexGrow: 1, p: 3, mt: 8 }}>
        {children}
      </Box>
    </Box>
  );
}

/**
 * Who is signed in, and the way out.
 *
 * The name is rendered beside the button rather than behind a menu: on a shared workstation — which
 * is what a registrar's desk is — "whose session is this" is a question worth answering without a
 * click, and it is the one thing that stops the next person recording attendance as the last one.
 */
function SignedInAs() {
  const user = useSignedInUser();
  const { signOut } = useAuth();

  // Local to this button rather than a hook's state, because a sign-out is not a write in the sense
  // `useApiMutation` models: it never fails in a way the user can act on (the local session ends
  // either way — see `api.signOut`), so there is no failure to render, only a moment to disable for.
  const [leaving, setLeaving] = useState(false);

  const press = () => {
    setLeaving(true);
    // `signOut` never rejects. The component unmounts when the session ends, which is why nothing
    // clears `leaving`: there is no state left to clear.
    void signOut();
  };

  return (
    <Box sx={{ display: "flex", alignItems: "center", gap: 2 }}>
      <Typography variant="body2" noWrap sx={{ display: { xs: "none", sm: "block" } }}>
        {user.fullName}
      </Typography>
      <Button
        color="inherit"
        size="small"
        startIcon={<LogoutIcon />}
        onClick={press}
        disabled={leaving}
      >
        {leaving ? "Signing out…" : "Sign out"}
      </Button>
    </Box>
  );
}

/**
 * The first focusable thing in the document, and invisible until it has focus.
 *
 * Every page on this app carries a permanent nav rail, so a keyboard user who does not have this
 * tabs through five links before reaching the grid they came for — on every single navigation.
 *
 * Visually hidden by being positioned off the top of the viewport rather than by `display: none`,
 * which would take it out of the focus order and defeat the whole thing.
 */
function SkipLink() {
  return (
    <Box
      component="a"
      href={`#${MAIN_CONTENT_ID}`}
      sx={{
        position: "fixed",
        top: 0,
        left: 0,
        zIndex: (t) => t.zIndex.tooltip + 1,
        p: 1.5,
        bgcolor: "background.paper",
        color: "text.primary",
        transform: "translateY(-200%)",
        "&:focus": { transform: "translateY(0)" },
      }}
    >
      Skip to main content
    </Box>
  );
}
