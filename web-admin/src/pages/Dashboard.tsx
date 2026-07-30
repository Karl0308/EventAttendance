import { Link } from "react-router-dom";
import {
  Card,
  CardContent,
  Typography,
  Box,
  Chip,
  List,
  ListItemButton,
  ListItemText,
} from "@mui/material";
import PeopleIcon from "@mui/icons-material/People";
import EventAvailableIcon from "@mui/icons-material/EventAvailable";
import HowToRegIcon from "@mui/icons-material/HowToReg";
import Grid from "@mui/material/Grid2";
import { api } from "../api";
import { useApiResource } from "../useApiResource";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import type { EventItem } from "../types";

const EVENT_STATUS_OPEN = "Open";
const NO_EVENTS = "No events have been created yet.";

interface DashboardData {
  studentCount: number;
  events: EventItem[];
  openEvents: EventItem[];
  checkedIn: number;
}

/**
 * The whole dashboard as one read, so its three tiles cannot disagree: previously the KPI counters
 * and the event list settled independently, and a failure in the middle of the summary loop left the
 * earlier tiles showing numbers while the rest silently stayed at zero.
 *
 * Module scope keeps its identity stable for the hook's effect dependency.
 */
async function loadDashboard(): Promise<DashboardData> {
  // `countStudents()` rather than `listStudents().length`: the tile needs one integer, and the
  // roster it used to walk was fetched, mapped and then discarded down to that integer.
  const [studentCount, events] = await Promise.all([api.countStudents(), api.listEvents()]);
  const openEvents = events.filter((e) => e.status === EVENT_STATUS_OPEN);
  const summaries = await Promise.all(openEvents.map((e) => api.eventSummary(e.id)));
  const checkedIn = summaries.reduce((total, s) => total + (s?.present ?? 0) + (s?.late ?? 0), 0);
  return { studentCount, events, openEvents, checkedIn };
}

function Kpi({ label, value, icon }: { label: string; value: number; icon: React.ReactNode }) {
  return (
    <Card>
      <CardContent>
        <Box sx={{ display: "flex", alignItems: "center", gap: 2 }}>
          <Box sx={{ color: "primary.main" }}>{icon}</Box>
          <Box>
            <Typography variant="h4" fontWeight={700}>
              {value}
            </Typography>
            <Typography color="text.secondary">{label}</Typography>
          </Box>
        </Box>
      </CardContent>
    </Card>
  );
}

// Left as literals, and identical to the copy in `Events.tsx`: this is a presentation lookup table
// over the whole status set, where naming each key restates it. `EVENT_STATUS_OPEN` above earns its
// name by driving a decision — which events count as open — rather than picking a colour.
const statusColor = (s: string) =>
  s === "Open" ? "success" : s === "Closed" ? "default" : s === "Cancelled" ? "error" : "warning";

export default function Dashboard() {
  const dashboard = useApiResource(loadDashboard);

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} gutterBottom>
        Dashboard
      </Typography>

      {dashboard.status === "loading" && <LoadingState label="Loading dashboard…" />}

      {/* Nothing below renders on failure: a KPI row of zeros is indistinguishable from a real
          zero, and it is the reading a user is most likely to believe. */}
      {dashboard.status === "error" && (
        <ErrorState subject="the dashboard" error={dashboard.error} onRetry={dashboard.reload} />
      )}

      {dashboard.status === "ready" && (
        <>
          <Grid container spacing={2} sx={{ mb: 3 }}>
            <Grid size={{ xs: 12, sm: 4 }}>
              <Kpi
                label="Students"
                value={dashboard.data.studentCount}
                icon={<PeopleIcon fontSize="large" />}
              />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <Kpi
                label="Open Events"
                value={dashboard.data.openEvents.length}
                icon={<EventAvailableIcon fontSize="large" />}
              />
            </Grid>
            <Grid size={{ xs: 12, sm: 4 }}>
              <Kpi
                label="Checked-in (open events)"
                value={dashboard.data.checkedIn}
                icon={<HowToRegIcon fontSize="large" />}
              />
            </Grid>
          </Grid>

          <Card>
            <CardContent>
              <Typography variant="h6" gutterBottom>
                Events
              </Typography>
              {dashboard.data.events.length === 0 ? (
                <EmptyState message={NO_EVENTS} />
              ) : (
                <List>
                  {dashboard.data.events.map((e) => (
                    <ListItemButton key={e.id} component={Link} to={`/events/${e.id}`}>
                      <ListItemText
                        primary={e.name}
                        secondary={`${e.location ?? ""} · ${new Date(e.startAt).toLocaleString()}`}
                      />
                      <Chip size="small" label={e.status} color={statusColor(e.status) as never} />
                    </ListItemButton>
                  ))}
                </List>
              )}
            </CardContent>
          </Card>
        </>
      )}
    </Box>
  );
}
