import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

describe("api client GET TTL cache", () => {
  beforeEach(() => {
    vi.resetModules();
    vi.useFakeTimers();
    vi.stubGlobal("location", {
      pathname: "/ui",
      search: "",
      href: "http://localhost/ui",
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  async function loadClient() {
    return import("./client");
  }

  function mockFetch(sequentialResponses: unknown[]) {
    const fetchMock = vi.fn();
    for (const body of sequentialResponses) {
      fetchMock.mockResolvedValueOnce({
        ok: true,
        status: 200,
        headers: new Headers({ "Content-Type": "application/json" }),
        json: async () => body,
        text: async () => JSON.stringify(body),
      });
    }
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  it("reuses /web/status within TTL and refetches after expiry", async () => {
    const fetchMock = mockFetch([{ ok: true }, { ok: true, refreshed: true }]);
    const { getStatus } = await loadClient();

    const first = await getStatus();
    const second = await getStatus();
    expect(first).toEqual({ ok: true });
    expect(second).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    vi.advanceTimersByTime(2_001);
    const third = await getStatus();
    expect(third).toEqual({ ok: true, refreshed: true });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("never TTL-caches Arr catalog paths", async () => {
    const fetchMock = mockFetch([{ rows: [1] }, { rows: [2] }]);
    const { getRadarrMovies } = await loadClient();
    await getRadarrMovies("movies", 1, 10, "");
    await getRadarrMovies("movies", 1, 10, "");
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("invalidateGetCache drops matching entries", async () => {
    const fetchMock = mockFetch([
      { Settings: {} },
      { Settings: { LoopSleepTimer: 5 } },
    ]);
    const { getConfig, invalidateGetCache } = await loadClient();

    await getConfig();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await getConfig();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    invalidateGetCache(["/web/config"]);
    await getConfig();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("updateConfig invalidates config cache", async () => {
    const fetchMock = mockFetch([
      { Settings: {} },
      { configReloaded: true, reloadType: "live" },
      { Settings: { LoopSleepTimer: 10 } },
    ]);
    const { getConfig, updateConfig } = await loadClient();

    await getConfig();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await updateConfig({ changes: { "Settings.LoopSleepTimer": 10 } });
    await getConfig();
    // POST + second GET (cache invalidated)
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });
  it("detaches invalidated requests and never caches their late responses", async () => {
    const { getConfig, invalidateGetCache } = await loadClient();
    let old!: (response: Response) => void;
    let fresh!: (response: Response) => void;
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise<Response>((resolve) => {
            old = resolve;
          }),
      )
      .mockImplementationOnce(
        () =>
          new Promise<Response>((resolve) => {
            fresh = resolve;
          }),
      );
    vi.stubGlobal("fetch", fetchMock);
    const first = getConfig();
    invalidateGetCache(["/web/config"]);
    const second = getConfig();
    old(Response.json({ Settings: { value: "old" } }));
    await first;
    const third = getConfig();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    fresh(Response.json({ Settings: { value: "new" } }));
    expect(await second).toEqual({ Settings: { value: "new" } });
    expect(await third).toEqual(await getConfig());
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
  it("uses a session token when local storage is blocked", async () => {
    const { getStatus } = await loadClient();
    sessionStorage.setItem("token", "session-token");
    vi.spyOn(window, "localStorage", "get").mockImplementation(() => {
      throw new Error("denied");
    });
    const fetchMock = mockFetch([{ ready: true }]);
    await getStatus();
    expect(fetchMock.mock.calls[0][1].headers.get("Authorization")).toBe(
      "Bearer session-token",
    );
    sessionStorage.clear();
  });
  it("serializes Readarr's missing filter as a boolean", async () => {
    const fetchMock = mockFetch([{ authors: [] }]);
    const { getReadarrAuthors } = await loadClient();
    await getReadarrAuthors("Readarr", 1, 20, "", { missing: true });
    expect(fetchMock.mock.calls[0][0]).toContain("missing=true");
  });
  it("surfaces a rejected update instead of reporting that it started", async () => {
    mockFetch([
      {
        success: false,
        message: "Docker installations update by recreating the container.",
      },
    ]);
    const { triggerUpdate } = await loadClient();
    await expect(triggerUpdate()).rejects.toThrow("Docker installations");
  });
  it("retains a query token in memory when neither browser store is usable", async () => {
    vi.unstubAllGlobals();
    window.history.replaceState({}, "", "/ui?token=query-secret");
    for (const store of ["localStorage", "sessionStorage"] as const)
      vi.spyOn(window, store, "get").mockImplementation(() => {
        throw new Error("blocked");
      });
    const fetchMock = mockFetch([{ ready: true }, { ready: true }]);
    const { getStatus, invalidateGetCache } = await loadClient();
    await getStatus();
    expect(window.location.search).not.toContain("token");
    invalidateGetCache(["/web/status"]);
    await getStatus();
    expect(fetchMock.mock.calls[1][1].headers.get("Authorization")).toBe(
      "Bearer query-secret",
    );
    window.history.replaceState({}, "", "/");
  });
});
