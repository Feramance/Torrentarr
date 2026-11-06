import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ArrActivityHistory, ArrActivityPoint } from "../../api/types";
import {
  groupActivity,
  activityPath,
  activityColor,
  workerHue,
  activityValue,
} from "../../utils/arrActivityChart";
import { ArrActivityCharts } from "../../components/ArrActivityCharts";
import { getArrActivity } from "../../api/client";
const settings = vi.hoisted(() => ({ liveArr: true }));
vi.mock("../../context/WebUIContext", () => ({ useWebUI: () => settings }));
vi.mock("../../api/client", async (original) => ({
  ...(await original<typeof import("../../api/client")>()),
  getArrActivity: vi.fn(),
}));
const point = (
  queueDepth: number | null,
  queueSamples = 5,
): ArrActivityPoint => ({
  timestamp: "2026-11-04T11:55:00Z",
  queueDepth,
  queueSamples,
  completionSamples: 5,
  expectedSamples: 5,
  searchesCompleted: 0,
  importsConfirmed: 1,
});
const history: ArrActivityHistory = {
  range: "24h",
  bucketMinutes: 5,
  generatedAt: "2026-11-04T12:00:00Z",
  series: [
    { instance: "Sonarr-one", type: "sonarr", points: [point(3)] },
    { instance: "Sonarr-two", type: "sonarr", points: [point(4)] },
    { instance: "Radarr-one", type: "radarr", points: [point(2)] },
  ],
};
beforeEach(() => {
  vi.mocked(getArrActivity).mockReset();
  settings.liveArr = true;
});
afterEach(() => vi.useRealTimers());
const settle = () =>
  act(async () => {
    await Promise.resolve();
  });
describe("Arr activity", () => {
  it("aggregates workers by type while retaining missing coverage and observed zero", () => {
    const rows = groupActivity(history.series, "type");
    const sonarr = rows.find((s) => s.type === "sonarr")!;
    expect(sonarr.points[0]!.queueDepth).toBe(7);
    expect(sonarr.points[0]!.searchesCompleted).toBe(0);
    expect(sonarr.points[0]!.expectedSamples).toBe(10);
    const partial = groupActivity(
      [{ ...history.series[0]!, points: [point(null, 0)] }, history.series[1]!],
      "type",
    )[0]!;
    expect(activityValue(partial.points[0], "queueDepth")).toBe("4 (partial)");
    expect(activityPath(partial.points, "queueDepth", 10)).toBe("");
    expect(
      activityPath(
        [point(0), point(null, 0), point(3)],
        "queueDepth",
        10,
      ).match(/M/g),
    ).toHaveLength(2);
  });
  it("keeps worker colors deterministic and uses distinct type colors", () => {
    expect(workerHue("Sonarr-one")).toBe(workerHue("Sonarr-one"));
    expect(activityColor(history.series[0]!, "worker")).not.toBe(
      activityColor(history.series[1]!, "worker"),
    );
    expect(activityColor(history.series[0]!, "type")).toBe("var(--arr-sonarr)");
    expect(activityColor(history.series[2]!, "type")).toBe("var(--arr-radarr)");
  });
  it("supports grouping, separate throughput metrics, legend toggles, and accessible data", async () => {
    vi.mocked(getArrActivity).mockResolvedValue(history);
    render(<ArrActivityCharts active />);
    await settle();
    const section = screen.getByRole("region", {
      name: "Arr activity history",
    });
    expect(getArrActivity).toHaveBeenCalledWith("24h");
    expect(
      within(section).getByRole("button", { name: "Sonarr", exact: true }),
    ).toHaveAttribute("aria-pressed", "true");
    fireEvent.change(screen.getByLabelText("Group activity by"), {
      target: { value: "worker" },
    });
    const toggle = within(section).getByRole("button", {
      name: "Sonarr-one",
      exact: true,
    });
    fireEvent.click(toggle);
    expect(toggle).toHaveAttribute("aria-pressed", "false");
    fireEvent.change(screen.getByLabelText("Completed activity metric"), {
      target: { value: "importsConfirmed" },
    });
    expect(
      screen.getByText("Torrent imports confirmed · per 5 min"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("slider", { name: "Inspect activity time" }),
    ).toBeInTheDocument();
    const details = screen.getByText("View activity data").closest("details")!;
    details.open = true;
    fireEvent(details, new Event("toggle"));
    expect(screen.getByRole("table")).toHaveTextContent("Sonarr-two");
    expect(screen.getByRole("table")).not.toHaveTextContent("Sonarr-one");
  });
  it("polls only while visible, active and live, with manual refresh support", async () => {
    vi.useFakeTimers();
    vi.mocked(getArrActivity).mockResolvedValue(history);
    const result = render(<ArrActivityCharts active />);
    await settle();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(60000);
    });
    expect(getArrActivity).toHaveBeenCalledTimes(2);
    settings.liveArr = false;
    result.rerender(<ArrActivityCharts active />);
    await settle();
    const count = vi.mocked(getArrActivity).mock.calls.length;
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120000);
    });
    expect(getArrActivity).toHaveBeenCalledTimes(count);
    window.dispatchEvent(new Event("torrentarr:statistics-refresh"));
    await settle();
    expect(getArrActivity).toHaveBeenCalledTimes(count + 1);
    result.rerender(<ArrActivityCharts active={false} />);
    window.dispatchEvent(new Event("torrentarr:statistics-refresh"));
    await settle();
    expect(getArrActivity).toHaveBeenCalledTimes(count + 1);
  });
  it("shows an error without fabricating a zero timeline", async () => {
    vi.mocked(getArrActivity).mockRejectedValue(new Error("offline"));
    render(<ArrActivityCharts active />);
    await settle();
    expect(screen.getByRole("status")).toHaveTextContent(
      "Arr activity unavailable",
    );
    expect(screen.queryByRole("img")).not.toBeInTheDocument();
  });
});
