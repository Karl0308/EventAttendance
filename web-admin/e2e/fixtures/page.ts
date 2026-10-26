// The paged envelope `asPage` in src/api.ts reads: `{ items, page, pageSize, total, hasMore }`.

export interface PageBody<T> {
  items: readonly T[];
  page: number;
  pageSize: number;
  total: number;
  hasMore: boolean;
}

export const onePage = <T>(items: readonly T[], total: number = items.length): PageBody<T> => ({
  items,
  page: 1,
  pageSize: 25,
  total,
  hasMore: false,
});
