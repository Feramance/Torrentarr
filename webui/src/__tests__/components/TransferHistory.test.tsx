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
      await vi.advanceTimersByTimeAsync(1);
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
    fireEvent.click(screen.getByRole("button", { name: "7d", exact: true }));
    await settle();
    expect(getTransferHistory).toHaveBeenLastCalledWith("two", "7d");
  });

  it("exposes coverage and readable sampled data", async () => {
    vi.mocked(getTransferHistory).mockResolvedValue(history("one"));
    render(<TransferHistory instance="one" active />);
    await settle();
    expect(screen.getByText(/5 \/ 10 minutes sampled/)).toBeInTheDocument();
    const details = screen.getByText("View sampled data").closest("details")!;
    details.open = true;
    fireEvent(details, new Event("toggle"));
    await settle();
    expect(screen.getByRole("table")).toHaveTextContent("Unavailable");
    expect(screen.getByRole("table")).toHaveTextContent("1.0 KiB/s");
  });
});
