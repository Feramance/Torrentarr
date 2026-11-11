import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ArrActivityHistory, ArrActivityPoint } from "../../api/types";
import {
  groupActivity,
  activityPath,
  activityColor,
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
  queueSamples = 60,
): ArrActivityPoint => ({
  timestamp: "2026-11-04T11:55:00Z",
  queueDepth,
  queueSamples,
  completionSamples: 60,
  expectedSamples: 60,
  searchesCompleted: 0,
  importsConfirmed: 1,
});
const history: ArrActivityHistory = {
  range: "24h",
  bucketMinutes: 60,
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
    expect(sonarr.points[0]!.expectedSamples).toBe(120);
    const partial = groupActivity(
      [{ ...history.series[0]!, points: [point(null, 0)] }, history.series[1]!],
      "type",
    )[0]!;
    expect(activityValue(partial.points[0], "queueDepth")).toBe("4");
    expect(activityValue(point(4.6), "queueDepth")).toBe("5");
    expect(activityPath(partial.points, "queueDepth", 10)).toMatch(/^M/);
    const relaxed = activityPath(
      [point(0), point(1, 30), point(3)],
      "queueDepth",
      10,
    );
    expect(relaxed.match(/M/g)).toHaveLength(1);
    expect(relaxed.match(/L/g)).toHaveLength(2);
    expect(
      activityPath(
        [point(0), point(null, 0), point(3)],
        "queueDepth",
        10,
      ).match(/M/g),
    ).toHaveLength(2);
  });
  it.each(["type", "worker"])(
    "plots captured partial hours and preserves genuine gaps by %s",
    async (grouping) => {
      vi.mocked(getArrActivity).mockResolvedValue({
        ...history,
        series: [
          {
            instance: "Sonarr-4K",
            type: "sonarr",
            points: [
              { ...point(3), timestamp: "2026-11-11T06:00:00Z" },
              {
                ...point(0, 57),
                completionSamples: 57,
                timestamp: "2026-11-11T07:00:00Z",
              },
              {
                ...point(null, 0),
                searchesCompleted: null,
                completionSamples: 0,
                timestamp: "2026-11-11T08:00:00Z",
              },
              { ...point(4), timestamp: "2026-11-11T09:00:00Z" },
            ],
          },
        ],
      });
      render(<ArrActivityCharts active />);
      await settle();
      fireEvent.change(screen.getByLabelText("Group activity by"), {
        target: { value: grouping },
      });
      for (const name of [/^Queue depth/, /^Searches completed/]) {
        const plot = screen.getByRole("img", { name });
        expect(plot.querySelectorAll("circle")).toHaveLength(3);
        expect(
          within(plot).getByRole("img", { name: /0 \(57\/60 samples\)/ }),
        ).toBeInTheDocument();
        const path = plot.querySelector("path")?.getAttribute("d");
        expect(path?.match(/M/g)).toHaveLength(2);
        expect(path?.match(/L/g)).toHaveLength(1);
      }
    },
  );
  it("keeps worker colors deterministic and uses distinct type colors", () => {
    expect(activityColor(history.series[0]!, "worker", 0)).toBe(
      activityColor(history.series[0]!, "worker", 0),
    );
    expect(activityColor(history.series[0]!, "worker", 0)).not.toBe(
      activityColor(history.series[1]!, "worker", 1),
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
      screen.getByText("Torrent imports confirmed · per hour"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("slider", { name: "Inspect activity time" }),
    ).toBeInTheDocument();
    expect(screen.queryByText("View activity data")).not.toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
  });
  it("refreshes active graphs from database notifications independently of Live", async () => {
    vi.useFakeTimers();
    vi.mocked(getArrActivity).mockResolvedValue(history);
    const result = render(<ArrActivityCharts active />);
    await settle();
    await act(async () => {
      window.dispatchEvent(new Event("torrentarr:statistics-updated"));
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
  it("restores the latest interval when hover ends, including after manual inspection", async () => {
    const twoIntervals = {
      ...history,
      series: history.series.map((series) => ({
        ...series,
        points: [
          { ...point(1), timestamp: "2026-11-04T11:50:00Z" },
          ...series.points,
        ],
      })),
    };
    vi.mocked(getArrActivity).mockResolvedValue(twoIntervals);
    render(<ArrActivityCharts active />);
    await settle();
    const slider = screen.getByRole("slider", {
      name: "Inspect activity time",
    });
    const plot = screen.getByRole("img", { name: /^Queue depth/ });
    vi.spyOn(plot, "getBoundingClientRect").mockReturnValue({
      left: 0,
      width: 800,
    } as DOMRect);
    expect(slider).toHaveValue("1");
    fireEvent.change(slider, { target: { value: "0" } });
    expect(slider).toHaveValue("0");
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 776 });
    expect(slider).toHaveValue("1");
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 24 });
    expect(slider).toHaveValue("0");
    fireEvent.pointerLeave(plot, { pointerType: "mouse" });
    expect(slider).toHaveValue("1");
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 24 });
    fireEvent.pointerCancel(plot, { pointerType: "mouse" });
    expect(slider).toHaveValue("1");
    vi.mocked(getArrActivity).mockResolvedValue({
      ...twoIntervals,
      series: twoIntervals.series.map((series) => ({
        ...series,
        points: [
          ...series.points,
          { ...point(9), timestamp: "2026-11-04T12:00:00Z" },
        ],
      })),
    });
    window.dispatchEvent(new Event("torrentarr:statistics-updated"));
    await settle();
    expect(slider).toHaveValue("2");
  });

  it("clears keyboard, mouse, touch, and obsolete activity inspections", async () => {
    vi.mocked(getArrActivity).mockResolvedValue({
      ...history,
      series: history.series.map((series) => ({
        ...series,
        points: [
          { ...point(1), timestamp: "2026-11-04T11:50:00Z" },
          ...series.points,
        ],
      })),
    });
    const view = render(<ArrActivityCharts active />);
    await settle();
    const slider = screen.getByRole("slider", {
      name: "Inspect activity time",
    });
    const plot = () => screen.getByRole("img", { name: /^Queue depth/ });
    const marker = () => plot().querySelector("circle")!;
    fireEvent.focus(marker());
    expect(slider).toHaveValue("0");
    fireEvent.blur(marker());
    expect(slider).toHaveValue("1");
    fireEvent.pointerDown(marker(), { pointerType: "mouse" });
    fireEvent.focus(marker());
    fireEvent.click(marker());
    fireEvent.pointerLeave(plot(), { pointerType: "mouse" });
    expect(slider).toHaveValue("1");
    fireEvent.pointerDown(marker(), { pointerType: "touch" });
    fireEvent.pointerLeave(plot(), { pointerType: "touch" });
    expect(slider).toHaveValue("0");
    fireEvent.pointerDown(document.body, { pointerType: "touch" });
    expect(slider).toHaveValue("1");
    fireEvent.focus(marker());
    fireEvent.keyDown(marker(), { key: "Escape" });
    expect(slider).toHaveValue("1");
    fireEvent.focus(marker());
    fireEvent.change(screen.getByLabelText("Group activity by"), {
      target: { value: "worker" },
    });
    expect(slider).toHaveValue("1");
    fireEvent.focus(marker());
    fireEvent.change(screen.getByLabelText("Completed activity metric"), {
      target: { value: "importsConfirmed" },
    });
    expect(slider).toHaveValue("1");
    fireEvent.focus(marker());
    view.rerender(<ArrActivityCharts active={false} />);
    expect(slider).toHaveValue("1");
  });
});
