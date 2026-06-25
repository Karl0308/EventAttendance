import { createTheme } from "@mui/material/styles";

// University of San Agustin — deep red / gold palette.
export const theme = createTheme({
  palette: {
    primary: { main: "#8B1A1A" },
    secondary: { main: "#C8A24B" },
    background: { default: "#f5f5f7" },
  },
  shape: { borderRadius: 10 },
  typography: { fontFamily: "Inter, Roboto, system-ui, sans-serif" },
});
