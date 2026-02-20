import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import {
  getQbitCategories,
  getStatus,
  getConfig,
  updateConfig,
} from "../../api/client";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// ── getQbitCategories ────────────────────────────────────────────────────────

describe("getQbitCategories", () => {
  it("deserializes QbitCategoriesResponse correctly", async () => {
    server.use(
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({
          categories: [
            {
              category: "radarr",
              instance: "qBit",
              managedBy: "arr",
              torrentCount: 3,
              seedingCount: 1,
              totalSize: 1024,
              avgRatio: 1.5,
              avgSeedingTime: 86400,
              seedingConfig: { maxRatio: 2, maxTime: -1, removeMode: 3, downloadLimit: -1, uploadLimit: -1 },
            },
          ],
          ready: true,
        })
      )
    );

    const result = await getQbitCategories();

    expect(result.categories).toHaveLength(1);
    expect(result.categories[0].category).toBe("radarr");
    expect(result.categories[0].instance).toBe("qBit");
    expect(result.categories[0].managedBy).toBe("arr");
    expect(result.categories[0].torrentCount).toBe(3);
    expect(result.ready).toBe(true);
  });
});

// ── getStatus ────────────────────────────────────────────────────────────────

describe("getStatus", () => {
  it("deserializes StatusResponse correctly", async () => {
    server.use(
      http.get("/web/status", () =>
        HttpResponse.json({
          qbit: { alive: false, host: null, port: null, version: null },
          qbitInstances: {},
          arrs: [],
          ready: true,
        })
      )
    );

    const result = await getStatus();

    expect(result.qbit.alive).toBe(false);
    expect(result.arrs).toHaveLength(0);
  });
});

// ── getConfig ────────────────────────────────────────────────────────────────

describe("getConfig", () => {
  it("returns plain config when no warning field", async () => {
    const mockConfig = { Settings: { LoopSleepTimer: 5 }, WebUI: { Port: 6969 } };
    server.use(
      http.get("/web/config", () => HttpResponse.json(mockConfig))
    );

    const result = await getConfig();

    expect(result).toEqual(mockConfig);
  });

  it("unwraps config when response has {warning, config} structure", async () => {
    const mockConfig = { Settings: { LoopSleepTimer: 5 } };
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          warning: { message: "Config version mismatch" },
          config: mockConfig,
        })
      )
    );

    const result = await getConfig();

    expect(result).toEqual(mockConfig);
  });
});

// ── updateConfig ─────────────────────────────────────────────────────────────

describe("updateConfig", () => {
  it("sends POST with correct JSON body", async () => {
    let capturedBody: unknown = null;

    server.use(
      http.post("/web/config", async ({ request }) => {
        capturedBody = await request.json();
        return HttpResponse.json({ status: "ok", configReloaded: false, reloadType: "none", affectedInstances: [] });
      })
    );

    const payload = { changes: { "Settings.LoopSleepTimer": 10 } };
    await updateConfig(payload);

    expect(capturedBody).toEqual(payload);
  });
});

// ── fetchJson error handling ──────────────────────────────────────────────────

describe("fetchJson error handling", () => {
  it("throws error with server error message on non-OK response", async () => {
    server.use(
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ error: "Service unavailable" }, { status: 503 })
      )
    );

    await expect(getQbitCategories()).rejects.toThrow("Service unavailable");
  });

  it("throws generic error on non-OK response without error field", async () => {
    server.use(
      http.get("/web/qbit/categories", () =>
        new HttpResponse(null, { status: 500, statusText: "Internal Server Error" })
      )
    );

    await expect(getQbitCategories()).rejects.toThrow("500");
  });
});
