import { useMemo, useState } from "react";
import { Box, Typography, TextField, Chip, Stack } from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import { api } from "../api";
import { useApiResource } from "../useApiResource";
import { EmptyState, ErrorState, LoadingState } from "../components/ResourceStates";
import type { Student } from "../types";

// Module scope, so its identity is stable and the hook's effect runs once rather than every render.
const loadStudents = () => api.listStudents();

const NO_STUDENTS = "No students are on file yet.";
const NO_MATCH = "No student matches that search.";

export default function Students() {
  const students = useApiResource(loadStudents);
  const [search, setSearch] = useState("");

  const rows = students.data;

  const filtered = useMemo(() => {
    if (rows === undefined) return [];
    const q = search.toLowerCase().trim();
    if (!q) return rows;
    return rows.filter(
      (s) => s.fullName.toLowerCase().includes(q) || s.studentNumber.includes(q),
    );
  }, [rows, search]);

  const cols: GridColDef<Student>[] = [
    { field: "studentNumber", headerName: "Student No.", width: 130 },
    { field: "fullName", headerName: "Name", flex: 1, minWidth: 180 },
    { field: "course", headerName: "Course", width: 90 },
    { field: "yearLevel", headerName: "Year", width: 110 },
    { field: "section", headerName: "Sec", width: 70 },
    {
      field: "cards",
      headerName: "RFID Card",
      width: 150,
      sortable: false,
      valueGetter: (_v, row) => row.cards[0]?.cardUid ?? "—",
    },
    {
      field: "status",
      headerName: "Status",
      width: 110,
      renderCell: (p) => <Chip size="small" label={p.value} color="success" variant="outlined" />,
    },
  ];

  return (
    <Box>
      <Typography variant="h5" fontWeight={700} gutterBottom>
        Students
      </Typography>

      {students.status === "loading" && <LoadingState label="Loading students…" />}

      {students.status === "error" && (
        <ErrorState subject="students" error={students.error} onRetry={students.reload} />
      )}

      {students.status === "ready" && (
        <>
          <Stack direction="row" spacing={2} sx={{ mb: 2 }}>
            <TextField
              size="small"
              label="Search name or student no."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              sx={{ width: 320 }}
            />
          </Stack>
          {filtered.length === 0 ? (
            // Two different nothings, and the user is told which: an empty roster is not the same
            // fact as a search that matched none of a full one.
            <EmptyState message={students.data.length === 0 ? NO_STUDENTS : NO_MATCH} />
          ) : (
            <div style={{ height: 520, width: "100%" }}>
              <DataGrid
                rows={filtered}
                columns={cols}
                getRowId={(r) => r.id}
                disableRowSelectionOnClick
                pageSizeOptions={[10, 25]}
                initialState={{ pagination: { paginationModel: { pageSize: 10 } } }}
              />
            </div>
          )}
        </>
      )}
    </Box>
  );
}
