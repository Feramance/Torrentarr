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
              <tr key={stableKey} onClick={() => onRowClick?.(row.original)}>
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
  // Only re-render if data reference changed
  return (
    prevProps.data === nextProps.data && prevProps.columns === nextProps.columns
  );
}) as typeof StableTableInner;
