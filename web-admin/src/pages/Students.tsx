import { useEffect, useMemo, useState } from "react";
import { Box, Typography, TextField, Chip, Stack } from "@mui/material";
import { DataGrid, type GridColDef } from "@mui/x-data-grid";
import { api } from "../api";
import type { Student } from "../types";

export default function Students() {
  const [rows, setRows] = useState<Student[]>([]);
  const [search, setSearch] = useState("");

  useEffect(() => {
    api.listStudents().then(setRows);
  }, []);

  const filtered = useMemo(() => {
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
      <Stack direction="row" spacing={2} sx={{ mb: 2 }}>
        <TextField
          size="small"
          label="Search name or student no."
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          sx={{ width: 320 }}
        />
      </Stack>
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
    </Box>
  );
}
