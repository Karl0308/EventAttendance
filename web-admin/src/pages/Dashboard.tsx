import { useEffect, useState } from "react";
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
import type { EventItem, Student } from "../types";

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

const statusColor = (s: string) =>
  s === "Open" ? "success" : s === "Closed" ? "default" : s === "Cancelled" ? "error" : "warning";

export default function Dashboard() {
  const [students, setStudents] = useState<Student[]>([]);
  const [events, setEvents] = useState<EventItem[]>([]);
  const [presentToday, setPresentToday] = useState(0);

  useEffect(() => {
    api.listStudents().then(setStudents);
    api.listEvents().then(async (evs) => {
      setEvents(evs);
      const open = evs.filter((e) => e.status === "Open");
      let total = 0;
      for (const e of open) {
        const s = await api.eventSummary(e.id);
        total += (s?.present ?? 0) + (s?.late ?? 0);
      }
      setPresentToday(total);
    });
  }, []);

  const openEvents = events.filter((e) => e.status === "Open");

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} gutterBottom>
        Dashboard
      </Typography>

      <Grid container spacing={2} sx={{ mb: 3 }}>
        <Grid size={{ xs: 12, sm: 4 }}>
          <Kpi label="Students" value={students.length} icon={<PeopleIcon fontSize="large" />} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <Kpi label="Open Events" value={openEvents.length} icon={<EventAvailableIcon fontSize="large" />} />
        </Grid>
        <Grid size={{ xs: 12, sm: 4 }}>
          <Kpi label="Checked-in (open events)" value={presentToday} icon={<HowToRegIcon fontSize="large" />} />
        </Grid>
      </Grid>

      <Card>
        <CardContent>
          <Typography variant="h6" gutterBottom>
            Events
          </Typography>
          <List>
            {events.map((e) => (
              <ListItemButton key={e.id} component={Link} to={`/events/${e.id}`}>
                <ListItemText
                  primary={e.name}
                  secondary={`${e.location ?? ""} · ${new Date(e.startAt).toLocaleString()}`}
                />
                <Chip size="small" label={e.status} color={statusColor(e.status) as never} />
              </ListItemButton>
            ))}
          </List>
        </CardContent>
      </Card>
    </Box>
  );
}
