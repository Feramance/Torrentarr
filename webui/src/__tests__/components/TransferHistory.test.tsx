import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TransferHistory } from "../../components/TransferHistory";
import { getTransferHistory } from "../../api/client";
import type { TransferHistoryResponse } from "../../api/types";

const settings = vi.hoisted(() => ({ liveArr: true }));
vi.mock("../../context/WebUIContext", () => ({ useWebUI: () => settings }));
vi.mock("../../api/client", async (original) => ({
  ...(await original<typeof import("../../api/client")>()),
  getTransferHistory: vi.fn(),
}));
const history = (instance: string): TransferHistoryResponse => ({
  range: "24h",
  bucketMinutes: 5,
  generatedAt: "2026-11-04T12:00:00Z",
  series: [
    {
      instance,
      points: [
        {
          timestamp: "2026-11-04T11:50:00Z",
          downloadRate: 1024,
          uploadRate: 2048,
          samples: 5,
          expectedSamples: 5,
        },
        {
          timestamp: "2026-11-04T11:55:00Z",
          downloadRate: null,
          uploadRate: null,
          samples: 0,
          expectedSamples: 5,
        },
      ],
    },
  ],
});
beforeEach(() => {
  vi.useFakeTimers();
  vi.mocked(getTransferHistory).mockReset();
  settings.liveArr = true;
});
afterEach(() => vi.useRealTimers());
const settle = () =>
  act(async () => {
    await Promise.resolve();
  });

describe("transfer history", () => {
  it("polls at minute intervals only while active and live", async () => {
    vi.mocked(getTransferHistory).mockResolvedValue(history("one"));
    const view = render(<TransferHistory instance="one" active />);
    await settle();
    expect(getTransferHistory).toHaveBeenCalledWith("one", "24h");
    await act(async () => {
      await vi.advanceTimersByTimeAsync(59999);
    });
    expect(getTransferHistory).toHaveBeenCalledTimes(1);
    await act(async () => {
      window.dispatchEvent(new Event("torrentarr:statistics-updated"));
    });
    expect(getTransferHistory).toHaveBeenCalledTimes(2);
    view.rerender(<TransferHistory instance="one" active={false} />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120000);
    });
    expect(getTransferHistory).toHaveBeenCalledTimes(2);
    settings.liveArr = false;
    view.rerender(<TransferHistory instance="one" active />);
    await settle();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120000);
    });
    expect(getTransferHistory).toHaveBeenCalledTimes(3);
    window.dispatchEvent(new Event("torrentarr:statistics-refresh"));
    await settle();
    expect(getTransferHistory).toHaveBeenCalledTimes(4);
  });

  it("changes range without mixing clients and rejects stale responses", async () => {
    let finish!: (value: TransferHistoryResponse) => void;
    vi.mocked(getTransferHistory)
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finish = resolve;
          }),
      )
      .mockResolvedValue(history("two"));
    const view = render(<TransferHistory instance="one" active />);
    view.rerender(<TransferHistory instance="two" active />);
    await settle();
    finish(history("one"));
    await settle();
    expect(screen.queryByText("one")).not.toBeInTheDocument();
    expect(screen.getByText("two")).toBeInTheDocument();
    fireEvent.focus(
      screen.getByRole("img", { name: /two.*Download: 1.0 KiB/ }),
    );
    expect(screen.getByRole("status")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "7d", exact: true }));
    await settle();
    expect(getTransferHistory).toHaveBeenLastCalledWith("two", "7d");
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("exposes coverage, readable sampled data and rate labels", async () => {
    vi.mocked(getTransferHistory).mockResolvedValue(history("one"));
    render(<TransferHistory instance="one" active />);
    await settle();
    expect(screen.getByText(/5 \/ 10 minutes sampled/)).toBeInTheDocument();
    expect(screen.queryByText("View sampled data")).not.toBeInTheDocument();
    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    expect(screen.getByText("2.0 KiB/s")).toBeInTheDocument();
    expect(screen.getByText("1.0 KiB/s")).toBeInTheDocument();
    expect(screen.getByText("0 B/s")).toBeInTheDocument();
    const point = screen.getByRole("img", { name: /one.*Download: 1.0 KiB/ });
    fireEvent.focus(point);
    expect(screen.getByRole("status")).toHaveTextContent("Inspecting");
    expect(screen.getByRole("status")).toHaveAttribute(
      "aria-label",
      expect.stringContaining("Download 1.0 KiB/s"),
    );
  });
  it("keeps the inspected point stable across gaps until leaving the plot", async () => {
    vi.mocked(getTransferHistory).mockResolvedValue(history("one"));
    const view = render(<TransferHistory instance="one" active />);
    await settle();
    const marker = screen.getByRole("img", { name: /one.*Download: 1.0 KiB/ });
    const plot = screen.getByRole("img", { name: /^one download and upload/ });
    vi.spyOn(plot, "getBoundingClientRect").mockReturnValue({
      x: 0,
      y: 0,
      width: 800,
      height: 155,
      top: 0,
      left: 0,
      right: 800,
      bottom: 155,
      toJSON: () => ({}),
    } as DOMRect);
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 70 });
    expect(screen.getByRole("status")).toHaveAttribute(
      "aria-label",
      expect.stringContaining("Download 1.0 KiB/s"),
    );
    expect(screen.getByRole("status").tagName).toBe("SPAN");
    expect(
      screen.queryByText("Latest complete interval", { exact: false }),
    ).not.toBeInTheDocument();
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 780 });
    expect(screen.getByRole("status")).toHaveAttribute(
      "aria-label",
      expect.stringContaining("No complete sample"),
    );
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 70 });
    expect(screen.getByRole("status")).toHaveAttribute(
      "aria-label",
      expect.stringContaining("Download 1.0 KiB/s"),
    );
    fireEvent.pointerLeave(plot, { pointerType: "mouse" });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(
      screen.getByText("Latest complete interval", { exact: false }),
    ).toBeInTheDocument();
    fireEvent.pointerDown(marker, { pointerType: "mouse" });
    fireEvent.focus(marker);
    fireEvent.click(marker);
    fireEvent.pointerLeave(plot, { pointerType: "mouse" });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    fireEvent.focus(marker);
    fireEvent.blur(marker);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    fireEvent.pointerDown(marker, { pointerType: "touch" });
    fireEvent.pointerLeave(marker, { pointerType: "touch" });
    expect(screen.getByRole("status")).toBeInTheDocument();
    fireEvent.pointerDown(document.body, { pointerType: "touch" });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    fireEvent.pointerMove(plot, { pointerType: "mouse", clientX: 70 });
    fireEvent.pointerCancel(plot, { pointerType: "mouse" });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    fireEvent.focus(marker);
    fireEvent.keyDown(marker, { key: "Escape" });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    fireEvent.focus(marker);
    view.rerender(<TransferHistory instance="one" active={false} />);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(
      screen.getByText("Latest complete interval", { exact: false }),
    ).toBeInTheDocument();
  });
});
