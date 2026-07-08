import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { resetUrlBaseCacheForTests } from "../../api/urlBase";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import {
  getQbitCategories,
  getStatus,
  getConfig,
  updateConfig,
  login,
  getToken,
  getMeta,
  setPassword,
  AuthError,
  getLidarrArtists,
  getLidarrArtistDetail,
} from "../../api/client";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => {
  server.resetHandlers();
  resetUrlBaseCacheForTests();
});
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
              seedingConfig: {
                maxRatio: 2,
                maxTime: -1,
                removeMode: 3,
                downloadLimit: -1,
                uploadLimit: -1,
              },
            },
          ],
          ready: true,
        }),
      ),
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
        }),
      ),
    );

    const result = await getStatus();

    expect(result.qbit.alive).toBe(false);
    expect(result.arrs).toHaveLength(0);
  });
});

// ── getConfig ────────────────────────────────────────────────────────────────

describe("getConfig", () => {
  it("returns plain config when no warning field", async () => {
    const mockConfig = {
      Settings: { LoopSleepTimer: 5 },
      WebUI: { Port: 6969 },
    };
    server.use(http.get("/web/config", () => HttpResponse.json(mockConfig)));

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
        }),
      ),
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
        return HttpResponse.json({
          status: "ok",
          configReloaded: false,
          reloadType: "none",
          affectedInstances: [],
        });
      }),
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
        HttpResponse.json({ error: "Service unavailable" }, { status: 503 }),
      ),
    );

    await expect(getQbitCategories()).rejects.toThrow("Service unavailable");
  });

  it("throws generic error on non-OK response without error field", async () => {
    server.use(
      http.get(
        "/web/qbit/categories",
        () =>
          new HttpResponse(null, {
            status: 500,
            statusText: "Internal Server Error",
          }),
      ),
    );

    await expect(getQbitCategories()).rejects.toThrow("500");
  });
});

// ── login ─────────────────────────────────────────────────────────────────────

describe("login", () => {
  it("returns success on 200", async () => {
    server.use(
      http.post("/web/login", () => HttpResponse.json({ success: true })),
    );
    const result = await login({ username: "admin", password: "password" });
    expect(result.success).toBe(true);
  });

  it("throws AuthError with message on 401", async () => {
    server.use(
      http.post("/web/login", () =>
        HttpResponse.json({ error: "Unauthorized" }, { status: 401 }),
      ),
    );
    await expect(
      login({ username: "admin", password: "wrong" }),
    ).rejects.toThrow("Unauthorized");
  });

  it("throws AuthError with SETUP_REQUIRED code on 403", async () => {
    server.use(
      http.post("/web/login", () =>
        HttpResponse.json(
          { error: "Password not set", code: "SETUP_REQUIRED" },
          { status: 403 },
        ),
      ),
    );
    let caught: unknown;
    try {
      await login({ username: "admin", password: "any" });
    } catch (e) {
      caught = e;
    }
    expect(caught).toBeInstanceOf(AuthError);
    expect((caught as AuthError).code).toBe("SETUP_REQUIRED");
    expect((caught as AuthError).message).toBe("Password not set");
  });

  it("thrown error is also an instanceof Error", async () => {
    server.use(
      http.post("/web/login", () =>
        HttpResponse.json({ error: "Unauthorized" }, { status: 401 }),
      ),
    );
    await expect(
      login({ username: "a", password: "b" }),
    ).rejects.toBeInstanceOf(Error);
  });
});

// ── getToken ──────────────────────────────────────────────────────────────────

describe("getToken", () => {
  it("returns token on 200", async () => {
    server.use(
      http.get("/web/token", () =>
        HttpResponse.json({ token: "my-api-token" }),
      ),
    );
    const result = await getToken();
    expect(result.token).toBe("my-api-token");
  });

  it("throws on 401", async () => {
    server.use(
      http.get("/web/token", () =>
        HttpResponse.json({ error: "Unauthorized" }, { status: 401 }),
      ),
    );
    await expect(getToken()).rejects.toThrow("Unauthorized");
  });
});

// ── getMeta ───────────────────────────────────────────────────────────────────

describe("getMeta", () => {
  it("deserializes MetaResponse auth fields", async () => {
    server.use(
      http.get("/web/meta", () =>
        HttpResponse.json({
          current_version: "6.1.0",
          latest_version: null,
          update_available: false,
          changelog: null,
          current_version_changelog: null,
          changelog_url: null,
          repository_url: "https://github.com/example/torrentarr",
          homepage_url: "https://github.com/example/torrentarr",
          last_checked: null,
          update_state: {
            in_progress: false,
            last_result: null,
            last_error: null,
            completed_at: null,
          },
          installation_type: "binary",
          binary_download_url: null,
          binary_download_name: null,
          binary_download_size: null,
          binary_download_error: null,
          auth_required: true,
          local_auth_enabled: true,
          oidc_enabled: false,
        }),
      ),
    );

    const result = await getMeta();

    expect(result.auth_required).toBe(true);
    expect(result.local_auth_enabled).toBe(true);
    expect(result.oidc_enabled).toBe(false);
    expect(result.current_version).toBe("6.1.0");
    expect(result.update_available).toBe(false);
  });

  it("auth_required false when auth is disabled", async () => {
    server.use(
      http.get("/web/meta", () =>
        HttpResponse.json({
          current_version: "6.1.0",
          latest_version: null,
          update_available: false,
          changelog: null,
          current_version_changelog: null,
          changelog_url: null,
          repository_url: "",
          homepage_url: "",
          last_checked: null,
          update_state: {
            in_progress: false,
            last_result: null,
            last_error: null,
            completed_at: null,
          },
          installation_type: "binary",
          binary_download_url: null,
          binary_download_name: null,
          binary_download_size: null,
          binary_download_error: null,
          auth_required: false,
          local_auth_enabled: false,
          oidc_enabled: false,
        }),
      ),
    );

    const result = await getMeta();

    expect(result.auth_required).toBe(false);
    expect(result.local_auth_enabled).toBe(false);
  });

  it("deserializes url_base when present", async () => {
    server.use(
      http.get("/web/meta", () =>
        HttpResponse.json({
          current_version: "6.12.3",
          latest_version: null,
          update_available: false,
          changelog: null,
          current_version_changelog: null,
          changelog_url: null,
          repository_url: "",
          homepage_url: "",
          last_checked: null,
          update_state: {
            in_progress: false,
            last_result: null,
            last_error: null,
            completed_at: null,
          },
          installation_type: "binary",
          binary_download_url: null,
          binary_download_name: null,
          binary_download_size: null,
          binary_download_error: null,
          auth_required: false,
          local_auth_enabled: false,
          oidc_enabled: false,
          url_base: "/torrentarr",
        }),
      ),
    );

    const result = await getMeta();
    expect(result.url_base).toBe("/torrentarr");
  });

  it("deserializes setup_required when present", async () => {
    server.use(
      http.get("/web/meta", () =>
        HttpResponse.json({
          current_version: "6.1.0",
          latest_version: null,
          update_available: false,
          changelog: null,
          current_version_changelog: null,
          changelog_url: null,
          repository_url: "",
          homepage_url: "",
          last_checked: null,
          update_state: {
            in_progress: false,
            last_result: null,
            last_error: null,
            completed_at: null,
          },
          installation_type: "binary",
          binary_download_url: null,
          binary_download_name: null,
          binary_download_size: null,
          binary_download_error: null,
          auth_required: true,
          local_auth_enabled: true,
          oidc_enabled: false,
          setup_required: true,
        }),
      ),
    );

    const result = await getMeta();

    expect(result.setup_required).toBe(true);
  });
});

// ── getLidarrArtists / getLidarrArtistDetail ────────────────────────────────

describe("getLidarrArtists", () => {
  it("deserializes artists response and encodes category", async () => {
    let requestedUrl = "";
    server.use(
      http.get("/web/lidarr/*/artists", ({ request }) => {
        requestedUrl = new URL(request.url).pathname;
        return HttpResponse.json({
          category: "my lidarr",
          counts: { available: 1, monitored: 2, missing: 1 },
          counts_tracks: { available: 3, monitored: 4, missing: 1 },
          album_total: 2,
          total: 1,
          page: 0,
          page_size: 25,
          artists: [
            {
              artist: {
                id: 1,
                name: "Artist",
                monitored: true,
                albumsMonitored: 2,
                albumsAvailable: 1,
                albumsMissing: 1,
              },
            },
          ],
        });
      }),
    );

    const result = await getLidarrArtists("my lidarr", 0, 25, "beat");

    expect(requestedUrl).toBe("/web/lidarr/my%20lidarr/artists");
    expect(result.artists).toHaveLength(1);
    expect(result.artists[0].artist.name).toBe("Artist");
    expect(result.counts_tracks?.monitored).toBe(4);
  });

  it("throws on non-OK response", async () => {
    server.use(
      http.get("/web/lidarr/*/artists", () =>
        HttpResponse.json({ error: "bad" }, { status: 500 }),
      ),
    );

    await expect(getLidarrArtists("lidarr", 0, 50)).rejects.toThrow();
  });
});

describe("getLidarrArtistDetail", () => {
  it("deserializes artist detail with albums and tracks", async () => {
    server.use(
      http.get("/web/lidarr/*/artist/:id", () =>
        HttpResponse.json({
          category: "lidarr",
          counts: { available: 1, monitored: 2, missing: 1 },
          counts_tracks: { available: 1, monitored: 2, missing: 1 },
          artist: { id: 5, name: "Detail Artist", monitored: true },
          albums: [
            {
              album: { id: 10, title: "Album", monitored: true, hasFile: true },
              tracks: [
                { id: 100, title: "Track", monitored: true, hasFile: true },
              ],
            },
          ],
        }),
      ),
    );

    const result = await getLidarrArtistDetail("lidarr", 5);
    expect(result.artist.name).toBe("Detail Artist");
    expect(result.albums[0].tracks).toHaveLength(1);
  });

  it("throws on 404", async () => {
    server.use(
      http.get("/web/lidarr/*/artist/:id", () =>
        HttpResponse.json({ error: "Artist not found" }, { status: 404 }),
      ),
    );

    await expect(getLidarrArtistDetail("lidarr", 999)).rejects.toThrow();
  });
});

// ── setPassword ───────────────────────────────────────────────────────────────

describe("setPassword", () => {
  it("returns success on 200", async () => {
    server.use(
      http.post("/web/auth/set-password", () =>
        HttpResponse.json({ success: true }),
      ),
    );
    const result = await setPassword({ username: "admin", password: "newpwd" });
    expect(result.success).toBe(true);
  });

  it("throws with server error message on 403", async () => {
    server.use(
      http.post("/web/auth/set-password", () =>
        HttpResponse.json(
          { error: "Set password not allowed" },
          { status: 403 },
        ),
      ),
    );
    await expect(
      setPassword({ username: "admin", password: "newpwd" }),
    ).rejects.toThrow("Set password not allowed");
  });

  it("sends setupToken when provided", async () => {
    let capturedBody: Record<string, unknown> | null = null;
    server.use(
      http.post("/web/auth/set-password", async ({ request }) => {
        capturedBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json({ success: true });
      }),
    );
    await setPassword({
      username: "admin",
      password: "newpwd",
      setupToken: "my-setup-token",
    });
    expect(capturedBody?.setupToken).toBe("my-setup-token");
  });
});

// ── AuthError ─────────────────────────────────────────────────────────────────

describe("AuthError", () => {
  it("is an instanceof Error", () => {
    const err = new AuthError("test");
    expect(err).toBeInstanceOf(Error);
  });

  it("preserves code field", () => {
    const err = new AuthError("msg", "SETUP_REQUIRED");
    expect(err.code).toBe("SETUP_REQUIRED");
    expect(err.message).toBe("msg");
    expect(err.name).toBe("AuthError");
  });

  it("code is undefined when not provided", () => {
    const err = new AuthError("msg");
    expect(err.code).toBeUndefined();
  });
});
