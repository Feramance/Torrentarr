import React from "react";
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ColumnDef } from "@tanstack/react-table";
import { StableTable } from "../../components/StableTable";

// ── Shared fixtures ────────────────────────────────────────────────────────────

interface Item {
  id: number;
  name: string;
}

const columns: ColumnDef<Item, unknown>[] = [
  {
    id: "id",
    header: "ID",
    accessorFn: (row) => row.id,
    cell: (info) => String(info.getValue()),
  },
  {
    id: "name",
    header: "Name",
    accessorFn: (row) => row.name,
    cell: (info) => info.getValue() as string,
  },
];

const data: Item[] = [
  { id: 1, name: "Alpha" },
  { id: 2, name: "Beta" },
];

// ── Column headers ─────────────────────────────────────────────────────────────

describe("StableTable – headers", () => {
  it("renders all column headers", () => {
    render(<StableTable data={data} columns={columns} />);

    expect(screen.getByText("ID")).toBeInTheDocument();
    expect(screen.getByText("Name")).toBeInTheDocument();
  });

  it("renders one <th> per column", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    expect(container.querySelectorAll("th")).toHaveLength(columns.length);
  });
});

// ── Cell values ────────────────────────────────────────────────────────────────

describe("StableTable – cell values", () => {
  it("renders cell text for every row", () => {
    render(<StableTable data={data} columns={columns} />);

    expect(screen.getByText("1")).toBeInTheDocument();
    expect(screen.getByText("Alpha")).toBeInTheDocument();
    expect(screen.getByText("2")).toBeInTheDocument();
    expect(screen.getByText("Beta")).toBeInTheDocument();
  });

  it("renders the correct number of body rows", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    expect(container.querySelectorAll("tbody tr")).toHaveLength(data.length);
  });

  it("renders the correct number of cells per row", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    container.querySelectorAll("tbody tr").forEach((row) => {
      expect(row.querySelectorAll("td")).toHaveLength(columns.length);
    });
  });
});

// ── Empty data ─────────────────────────────────────────────────────────────────

describe("StableTable – empty data", () => {
  it("renders no body rows for empty data array", () => {
    const { container } = render(<StableTable data={[]} columns={columns} />);

    expect(container.querySelectorAll("tbody tr")).toHaveLength(0);
  });

  it("still renders headers when data is empty", () => {
    render(<StableTable data={[]} columns={columns} />);

    expect(screen.getByText("ID")).toBeInTheDocument();
    expect(screen.getByText("Name")).toBeInTheDocument();
  });
});

// ── data-label attribute ──────────────────────────────────────────────────────

describe("StableTable – data-label attribute", () => {
  it("sets data-label on each <td> matching the column header", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    const firstRowCells = container.querySelectorAll("tbody tr:first-child td");
    expect(firstRowCells[0]).toHaveAttribute("data-label", "ID");
    expect(firstRowCells[1]).toHaveAttribute("data-label", "Name");
  });
});

// ── getRowKey ──────────────────────────────────────────────────────────────────

describe("StableTable – getRowKey", () => {
  it("renders correctly when getRowKey is provided", () => {
    const { container } = render(
      <StableTable
        data={data}
        columns={columns}
        getRowKey={(row) => `item-${row.id}`}
      />,
    );

    expect(container.querySelectorAll("tbody tr")).toHaveLength(data.length);
  });

  it("renders correctly without getRowKey (falls back to row.id)", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    expect(container.querySelectorAll("tbody tr")).toHaveLength(data.length);
  });
});

// ── DOM structure ──────────────────────────────────────────────────────────────

describe("StableTable – DOM structure", () => {
  it("wraps table in .table-wrapper div", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    expect(container.querySelector(".table-wrapper")).toBeInTheDocument();
  });

  it("applies .responsive-table class to the <table> element", () => {
    const { container } = render(<StableTable data={data} columns={columns} />);

    expect(
      container.querySelector("table.responsive-table"),
    ).toBeInTheDocument();
  });
});

// ── Custom cell renderer ───────────────────────────────────────────────────────

describe("StableTable – custom cell renderer", () => {
  it("renders output from a custom cell function", () => {
    const customColumns: ColumnDef<Item, unknown>[] = [
      {
        id: "badge",
        header: "Badge",
        accessorFn: (row) => row.name,
        cell: (info) => `★ ${info.getValue()}`,
      },
    ];

    render(
      <StableTable data={[{ id: 1, name: "Test" }]} columns={customColumns} />,
    );

    expect(screen.getByText("★ Test")).toBeInTheDocument();
  });

  it("renders JSX from a custom cell function", () => {
    const customColumns: ColumnDef<Item, unknown>[] = [
      {
        id: "pill",
        header: "Status",
        accessorFn: (row) => row.id,
        cell: (info) => <span data-testid={`pill-${info.getValue()}`}>ok</span>,
      },
    ];

    render(<StableTable data={data} columns={customColumns} />);

    expect(screen.getByTestId("pill-1")).toBeInTheDocument();
    expect(screen.getByTestId("pill-2")).toBeInTheDocument();
  });
});
