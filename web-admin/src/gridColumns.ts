// One rule for every grid that ends in a WRITE-ONLY Actions column.
//
// Only for columns whose every action writes. A grid whose "actions" column holds a read button (the
// attendance analytics grid's "View event" link) must not be passed through here — it would vanish for
// the very role that needs it.
//
// A role without the write permission the column's buttons need (Viewer) gets the grid without the
// column at all, not a column of disabled icons: nothing to click, nothing to explain, and no empty
// header over blank cells. A convenience, not a control — the endpoints refuse without the permission
// regardless. See `PermissionGuard`.

import type { GridColDef } from "@mui/x-data-grid";

/** The `field` the write-action grids in this SPA give their row-action column. */
export const ACTIONS_FIELD = "actions";

/**
 * @param canAct whether the signed-in token carries the permission the column's actions need.
 * @returns `columns` unchanged when it does, otherwise without the Actions column.
 */
export function withActionsFor<R extends object>(
  columns: GridColDef<R>[],
  canAct: boolean,
): GridColDef<R>[] {
  return canAct ? columns : columns.filter((column) => column.field !== ACTIONS_FIELD);
}
