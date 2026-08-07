import { type ReactNode } from "react";
import { Link, useLocation } from "react-router-dom";
import {
  AppBar,
  Box,
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

const DRAWER_WIDTH = 240;

const nav = [
  { to: "/", label: "Dashboard", icon: <DashboardIcon /> },
  { to: "/students", label: "Students", icon: <PeopleIcon /> },
  { to: "/events", label: "Events", icon: <EventIcon /> },
  { to: "/devices", label: "Devices", icon: <RouterIcon /> },
  // Last, and that is not an accident of appending: a term is created once a semester, where the
  // four above are visited daily. `isActive` matches on `startsWith`, and `/terms` shares no prefix
  // with any of them.
  { to: "/terms", label: "Terms", icon: <CalendarMonthIcon /> },
];

export default function Layout({ children }: { children: ReactNode }) {
  const { pathname } = useLocation();
  const isActive = (to: string) => (to === "/" ? pathname === "/" : pathname.startsWith(to));

  return (
    <Box sx={{ display: "flex" }}>
      <AppBar position="fixed" sx={{ zIndex: (t) => t.zIndex.drawer + 1 }}>
        <Toolbar>
          <Typography variant="h6" noWrap sx={{ flexGrow: 1, fontWeight: 700 }}>
            EAMS — Events Attendance Monitoring
          </Typography>
          {/* A "MOCK DATA" chip sat to the right of the title until `api.ts` stopped being a mock
              facade. Every method on it now calls the real backend, so the chip was telling an
              operator that live student PII and real device keys were fake. Removed rather than
              corrected: a banner stating the ordinary case is noise the next reader has to re-verify. */}
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
        <List>
          {nav.map((n) => (
            <ListItemButton key={n.to} component={Link} to={n.to} selected={isActive(n.to)}>
              <ListItemIcon>{n.icon}</ListItemIcon>
              <ListItemText primary={n.label} />
            </ListItemButton>
          ))}
        </List>
      </Drawer>

      <Box component="main" sx={{ flexGrow: 1, p: 3, mt: 8 }}>
        {children}
      </Box>
    </Box>
  );
}
