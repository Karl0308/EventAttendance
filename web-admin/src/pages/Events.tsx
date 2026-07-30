import { useNavigate } from "react-router-dom";
import { Box, Typography, Card, CardContent, Chip, Stack } from "@mui/material";
import LocationOnIcon from "@mui/icons-material/LocationOn";
import ScheduleIcon from "@mui/icons-material/Schedule";
import { api } from "../api";
import { useApiResource } from "../useApiResource";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";

// Module scope, so its identity is stable and the hook's effect runs once rather than every render.
const loadEvents = () => api.listEvents();

const NO_EVENTS = "No events have been created yet.";

const statusColor = (s: string) =>
  s === "Open" ? "success" : s === "Closed" ? "default" : s === "Cancelled" ? "error" : "warning";

export default function Events() {
  const events = useApiResource(loadEvents);
  const nav = useNavigate();

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} gutterBottom>
        Events
      </Typography>

      {events.status === "loading" && <LoadingState label="Loading events…" />}

      {events.status === "error" && (
        <ErrorState subject="events" error={events.error} onRetry={events.reload} />
      )}

      {events.status === "ready" &&
        (events.data.length === 0 ? (
          <EmptyState message={NO_EVENTS} />
        ) : (
          <Stack spacing={2}>
            {events.data.map((e) => (
              <Card
                key={e.id}
                sx={{ cursor: "pointer", "&:hover": { boxShadow: 4 } }}
                onClick={() => nav(`/events/${e.id}`)}
              >
                <CardContent>
                  <Stack direction="row" justifyContent="space-between" alignItems="flex-start">
                    <Box>
                      <Typography variant="h6">{e.name}</Typography>
                      <Stack direction="row" spacing={2} sx={{ mt: 1, color: "text.secondary" }}>
                        <Stack direction="row" spacing={0.5} alignItems="center">
                          <LocationOnIcon fontSize="small" />
                          <Typography variant="body2">{e.location ?? "—"}</Typography>
                        </Stack>
                        <Stack direction="row" spacing={0.5} alignItems="center">
                          <ScheduleIcon fontSize="small" />
                          <Typography variant="body2">
                            {new Date(e.startAt).toLocaleString()}
                          </Typography>
                        </Stack>
                      </Stack>
                    </Box>
                    <Stack spacing={1} alignItems="flex-end">
                      <Chip size="small" label={e.status} color={statusColor(e.status)} />
                      <Chip size="small" variant="outlined" label={e.attendanceMode} />
                    </Stack>
                  </Stack>
                </CardContent>
              </Card>
            ))}
          </Stack>
        ))}
    </Box>
  );
}
