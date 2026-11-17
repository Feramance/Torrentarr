import { act, renderHook } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import {
  STATISTICS_UPDATED,
  useStatisticsUpdates,
} from "../hooks/useStatisticsUpdates";

it("refreshes visible database statistics independently of live Arr polling and cleans up", async () => {
  const refresh = vi.fn();
  const { rerender, unmount } = renderHook(
    ({ active }) => useStatisticsUpdates(active, refresh),
    { initialProps: { active: true } },
  );
  await act(async () => {
    window.dispatchEvent(new Event(STATISTICS_UPDATED));
  });
  expect(refresh).toHaveBeenCalledTimes(1);
  rerender({ active: false });
  await act(async () => {
    window.dispatchEvent(new Event(STATISTICS_UPDATED));
  });
  expect(refresh).toHaveBeenCalledTimes(1);
  rerender({ active: true });
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
  expect(refresh).toHaveBeenCalledTimes(2);
  unmount();
  window.dispatchEvent(new Event(STATISTICS_UPDATED));
  expect(refresh).toHaveBeenCalledTimes(2);
});

it("fetches again when a database change arrives during an in-flight refresh", async () => {
  let complete!: () => void;
  const refresh = vi
    .fn<() => Promise<void>>()
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          complete = resolve;
        }),
    )
    .mockResolvedValue(undefined);
  renderHook(() => useStatisticsUpdates(true, refresh));
  act(() => {
    window.dispatchEvent(new Event(STATISTICS_UPDATED));
    window.dispatchEvent(new Event(STATISTICS_UPDATED));
    window.dispatchEvent(new Event(STATISTICS_UPDATED));
  });
  expect(refresh).toHaveBeenCalledTimes(1);
  await act(async () => {
    complete();
  });
  expect(refresh).toHaveBeenCalledTimes(2);
});
