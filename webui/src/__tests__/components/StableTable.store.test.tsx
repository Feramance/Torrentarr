import React from "react";
import { act, render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { StableTable } from "../../components/StableTable";
import { RowsStore } from "../../utils/rowsStore";
import type { AppColumnDef } from "../../tableCore";

it("updates a subscribed row without replacing unaffected DOM rows", () => {
  type Item = { id: string; title: string };
  const store = new RowsStore<Item>({
    getKey: (row) => row.id,
    hashFields: ["title"],
  });
  store.sync([
    { id: "a", title: "Dune" },
    { id: "b", title: "Foundation" },
  ]);
  const columns: AppColumnDef<Item>[] = [
    {
      id: "title",
      header: "Title",
      accessorFn: (row) => row.title,
      cell: (cell) => String(cell.getValue()),
    },
  ];
  render(
    <StableTable
      columns={columns}
      rowOrder={store.getSnapshot().rowOrder}
      rowsStore={store}
      getRowKey={(row) => row.id}
    />,
  );
  const unchanged = screen.getByText("Foundation").closest("tr");
  act(() => {
    store.sync([
      { id: "a", title: "Dune Messiah" },
      { id: "b", title: "Foundation" },
    ]);
  });
  expect(screen.getByText("Dune Messiah")).toBeInTheDocument();
  expect(screen.getByText("Foundation").closest("tr")).toBe(unchanged);
});
