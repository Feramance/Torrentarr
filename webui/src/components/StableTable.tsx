import { memo } from "react";
import { flexRender } from "@tanstack/react-table";
import {
  useLegacyTable,
  getCoreRowModel,
  type LegacyColumnDef,
} from "@tanstack/react-table/legacy";

interface StableTableProps<TData extends object> {
  data: TData[];
  columns: LegacyColumnDef<TData, unknown>[];
  getRowKey?: (row: TData) => string;
  onRowClick?: (row: TData) => void;
}

function StableTableInner<TData extends object>({
  data,
  columns,
  getRowKey,
  onRowClick,
}: StableTableProps<TData>) {
  // TanStack Table returns unstable function refs; React Compiler skips memoization by design
  /* eslint-disable-next-line react-hooks/incompatible-library */
  const table = useLegacyTable({
    data,
    columns,
    getCoreRowModel: getCoreRowModel(),
  });

  return (
    <div className="table-wrapper">
      <table className="responsive-table">
        <thead>
          {table.getHeaderGroups().map((headerGroup) => (
            <tr key={headerGroup.id}>
              {headerGroup.headers.map((header) => (
                <th key={header.id}>
                  {header.isPlaceholder
                    ? null
                    : flexRender(
                        header.column.columnDef.header,
                        header.getContext(),
                      )}
                </th>
              ))}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.map((row) => {
            const stableKey = getRowKey ? getRowKey(row.original) : row.id;
            return (
              <tr
                key={stableKey}
                tabIndex={onRowClick ? 0 : undefined}
                role={onRowClick ? "button" : undefined}
                aria-label={onRowClick ? `Select row ${stableKey}` : undefined}
                onClick={() => onRowClick?.(row.original)}
                onKeyDown={(event) => {
                  if (
                    onRowClick &&
                    (event.key === "Enter" || event.key === " ")
                  ) {
                    event.preventDefault();
                    onRowClick(row.original);
                  }
                }}
              >
                {row.getVisibleCells().map((cell) => (
                  <td
                    key={cell.id}
                    data-label={String(cell.column.columnDef.header)}
                  >
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                ))}
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

export const StableTable = memo(StableTableInner, (prevProps, nextProps) => {
  return (
    prevProps.data === nextProps.data &&
    prevProps.columns === nextProps.columns &&
    prevProps.getRowKey === nextProps.getRowKey &&
    prevProps.onRowClick === nextProps.onRowClick
  );
}) as typeof StableTableInner;
