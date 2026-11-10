import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { invalidateGetCache, watchStatistics } from "../api/client";
import { useStatisticsStream } from "../hooks/useStatisticsStream";
import { STATISTICS_UPDATED } from "../hooks/useStatisticsUpdates";

vi.mock("../api/client", () => ({
  watchStatistics: vi.fn(),
  invalidateGetCache: vi.fn(),
}));
afterEach(() => {
  vi.useRealTimers();
  vi.resetAllMocks();
});

it("shares one subscription per shell and coalesces updates before invalidating data", async () => {
  vi.useFakeTimers();
  let receive!: () => void;
  let signal!: AbortSignal;
  vi.mocked(watchStatistics).mockImplementation((next, abort) => {
    receive = next;
    signal = abort;
    return new Promise((resolve) =>
      abort.addEventListener("abort", () => resolve()),
    );
  });
  const changed = vi.fn();
  window.addEventListener(STATISTICS_UPDATED, changed);
  const { rerender, unmount } = renderHook(() => useStatisticsStream());
  rerender();
  expect(watchStatistics).toHaveBeenCalledTimes(1);
  receive();
  receive();
  receive();
  await act(async () => {
    await vi.advanceTimersByTimeAsync(150);
  });
  expect(changed).toHaveBeenCalledTimes(1);
  expect(invalidateGetCache).toHaveBeenCalledWith([
    "/web/processes",
    "/web/arr",
    "/web/status",
    "/web/statistics",
  ]);
  unmount();
  expect(signal.aborted).toBe(true);
  window.removeEventListener(STATISTICS_UPDATED, changed);
});

it("reconnects after a failed stream and cancels reconnect work on unmount", async () => {
  vi.useFakeTimers();
  vi.mocked(watchStatistics)
    .mockRejectedValueOnce(new Error("offline"))
    .mockImplementation(
      (_next, abort) =>
        new Promise((resolve) =>
          abort.addEventListener("abort", () => resolve()),
        ),
    );
  const { unmount } = renderHook(() => useStatisticsStream());
  await act(async () => {
    await vi.advanceTimersByTimeAsync(1000);
  });
  expect(watchStatistics).toHaveBeenCalledTimes(2);
  unmount();
  await vi.advanceTimersByTimeAsync(60000);
  expect(watchStatistics).toHaveBeenCalledTimes(2);
});
