import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { RADARR_DEFINITION } from "../../pages/arrCatalog/radarrDefinition";
import { useAggregateCatalogLoader } from "../../pages/arrCatalog/useAggregateCatalogLoader";
import type { ArrInfo, RadarrMoviesResponse } from "../../api/types";

function deferred() {
  let resolve!: (value: RadarrMoviesResponse) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<RadarrMoviesResponse>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}
const instances: ArrInfo[] = [
  { category: "movies", name: "Radarr", type: "radarr" },
];
const empty: RadarrMoviesResponse = {
  category: "movies",
  movies: [],
  total: 0,
  page: 0,
  page_size: 100,
  counts: { available: 0, monitored: 0, missing: 0 },
};
function setup(
  fetchPage: typeof RADARR_DEFINITION.aggregate.fetchPage,
  filters = RADARR_DEFINITION.initialFilters,
  current = instances,
) {
  const adapter = { ...RADARR_DEFINITION.aggregate, fetchPage };
  const pushToast = vi.fn();
  const hook = renderHook(
    ({ current }: { current: ArrInfo[] }) =>
      useAggregateCatalogLoader({
        active: true,
        selection: "aggregate",
        instances: current,
        liveArr: true,
        globalSearch: "",
        filters,
        initialFilters: RADARR_DEFINITION.initialFilters,
        adapter,
        aggregatePageSize: 50,
        pushToast,
      }),
    { initialProps: { current } },
  );
  return { ...hook, pushToast };
}
afterEach(() => vi.useRealTimers());

it.each([false, true])(
  "clears loading when instances disappear and ignores stale failure=%s",
  async (failure) => {
    const old = deferred();
    const { result, rerender, pushToast } = setup(vi.fn(() => old.promise));
    expect(result.current.loading).toBe(true);
    rerender({ current: [] });
    expect(result.current.loading).toBe(false);
    expect(result.current.rows).toEqual([]);
    expect(result.current.summary.total).toBe(0);
    expect(result.current.emptyStateReady).toBe(true);
    await act(async () => {
      if (failure) old.reject(new Error("stale"));
      else
        old.resolve({
          ...empty,
          total: 1,
          movies: [{ id: 1, title: "Stale", monitored: true }],
        });
    });
    expect(result.current.loading).toBe(false);
    expect(pushToast).not.toHaveBeenCalled();
    expect(result.current.rows).toEqual([]);
    expect(result.current.summary.total).toBe(0);
  },
);

it("does not let stale completion clear a replacement spinner", async () => {
  const old = deferred();
  const replacement = deferred();
  const fetch = vi
    .fn()
    .mockReturnValueOnce(old.promise)
    .mockReturnValueOnce(replacement.promise);
  const { result } = setup(fetch);
  act(() => result.current.refresh());
  await act(async () => old.resolve(empty));
  expect(result.current.loading).toBe(true);
  await act(async () => replacement.resolve(empty));
  expect(result.current.loading).toBe(false);
});

it("polls after replacement completion while an obsolete request is still pending", async () => {
  vi.useFakeTimers();
  const old = deferred();
  const fetch = vi
    .fn()
    .mockReturnValueOnce(old.promise)
    .mockResolvedValue(empty);
  const { result, rerender } = setup(fetch);
  rerender({ current: [] });
  rerender({ current: instances });
  await act(async () => {});
  expect(result.current.loading).toBe(false);
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15000);
  });
  expect(fetch).toHaveBeenCalledTimes(3);
  await act(async () => old.resolve(empty));
});

it("releases the current load after an error", async () => {
  const current = deferred();
  const { result, pushToast } = setup(vi.fn(() => current.promise));
  await act(async () => current.reject(new Error("failed")));
  expect(result.current.loading).toBe(false);
  expect(pushToast).toHaveBeenCalledWith("failed", "error");
});

it("filters aggregate matches across instances and later fetched pages", async () => {
  const fetch = vi.fn(async (category: string, page: number) => ({
    ...empty,
    category,
    page,
    total: category === "Radarr" ? 101 : 1,
    movies:
      category === "Radarr" && page === 0
        ? Array.from({ length: 100 }, (_, id) => ({
            id,
            title: `Available ${id}`,
            hasFile: true,
            monitored: true,
            reason: "Quality",
          }))
        : [
            {
              id: 101,
              title: `${category} match`,
              hasFile: false,
              monitored: true,
              reason: "Missing",
            },
          ],
  }));
  const { result } = setup(
    fetch,
    { onlyMissing: true, reasonFilter: "Missing" },
    [...instances, { category: "other", name: "Other", type: "radarr" }],
  );
  await act(async () => {});
  expect(fetch.mock.calls.map(([category, page]) => [category, page])).toEqual([
    ["Radarr", 0],
    ["Radarr", 1],
    ["Other", 0],
  ]);
  expect(result.current.visibleRows.map((row) => row.title)).toEqual([
    "Radarr match",
    "Other match",
  ]);
  expect(result.current.summary.total).toBe(102);
});
