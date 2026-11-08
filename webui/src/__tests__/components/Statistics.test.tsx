import { describe, expect, it } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { BreakdownBar } from "../../components/Statistics";
import { transferPath } from "../../utils/transferChart";
import type { TransferBucket } from "../../api/types";

const point = (downloadRate: number | null, samples = 5): TransferBucket => ({
  timestamp: "2026-11-04T12:00:00Z",
  downloadRate,
  uploadRate: 0,
  samples,
  expectedSamples: 5,
});
describe("statistics", () => {
  it("breaks lines for missing and incomplete buckets while preserving actual zero", () => {
    const path = transferPath(
      [
        point(0),
        point(100),
        point(null, 0),
        point(30),
        point(40, 2),
        point(50),
      ],
      "downloadRate",
      100,
    );
    expect(path.match(/M/g)).toHaveLength(3);
    expect(path.match(/L/g)).toHaveLength(1);
    expect(path).toContain("M20.00,130.00");
  });
  it("shows text equivalents for breakdowns including empty results", () => {
    render(
      <BreakdownBar
        label="Outcomes"
        values={[{ label: "Failed", value: 0, tone: "danger" }]}
      />,
    );
    expect(screen.getByRole("group", { name: "Outcomes" })).toHaveTextContent(
      "Failed 0",
    );
    expect(screen.getByText("No data yet")).toBeInTheDocument();
  });
  it("shows exact values, percentages and units on focus and touch", () => {
    render(
      <BreakdownBar
        label="Availability"
        unit="movies"
        values={[
          { label: "Available", value: 3, tone: "teal" },
          { label: "Missing", value: 1, tone: "muted" },
        ]}
      />,
    );
    fireEvent.focus(
      screen.getByRole("button", { name: /Available: 3 movies/ }),
    );
    expect(screen.getByRole("tooltip")).toHaveTextContent(
      "Available: 3 movies (75.0%)",
    );
    const missing = screen.getByRole("button", { name: /Missing: 1 movies/ });
    fireEvent.pointerDown(missing, { pointerType: "touch" });
    fireEvent.pointerLeave(missing, { pointerType: "touch" });
    expect(screen.getByRole("tooltip")).toHaveTextContent(
      "Missing: 1 movies (25.0%)",
    );
    fireEvent.pointerDown(document.body, { pointerType: "touch" });
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
    fireEvent.pointerEnter(missing, { pointerType: "mouse" });
    expect(screen.getByRole("tooltip")).toHaveTextContent("Missing: 1 movies");
    fireEvent.pointerLeave(missing, { pointerType: "mouse" });
    expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
  });
});
