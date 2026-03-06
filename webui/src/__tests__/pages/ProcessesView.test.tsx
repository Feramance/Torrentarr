import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ProcessesView } from "../../pages/ProcessesView";
import { ToastProvider } from "../../context/ToastContext";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

const emptyStatus = {
  qbit: { alive: false, host: null, port: null, version: null },
  qbitInstances: {},
  arrs: [],
  ready: true,
};

const emptyCategories = { categories: [], ready: true };

function Wrapper({ children }: { children: ReactNode }) {
  return <ToastProvider>{children}</ToastProvider>;
}

function renderView(active = true) {
  return render(<ProcessesView active={active} />, { wrapper: Wrapper });
}

// ── empty / no processes ──────────────────────────────────────────────────────

describe("ProcessesView – empty state", () => {
  it("shows empty-state message when no processes are returned", async () => {
    server.use(
      http.get("/web/processes", () => HttpResponse.json({ processes: [] })),
      http.get("/web/status", () => HttpResponse.json(emptyStatus)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json(emptyCategories),
      ),
    );

    renderView();

    await screen.findByText("No processes available.");
  });
});

// ── process grouping ──────────────────────────────────────────────────────────

describe("ProcessesView – process grouping", () => {
  it("groups Radarr processes under a Radarr section", async () => {
    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({
          processes: [
            {
              category: "radarr",
              name: "Radarr-1080",
              kind: "search",
              pid: 1,
              alive: true,
            },
            {
              category: "radarr",
              name: "Radarr-1080",
              kind: "torrent",
              pid: 2,
              alive: true,
            },
          ],
        }),
      ),
      http.get("/web/status", () =>
        HttpResponse.json({
          ...emptyStatus,
          arrs: [{ category: "radarr", name: "Radarr-1080", type: "radarr" }],
        }),
      ),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json(emptyCategories),
      ),
    );

    renderView();

    await screen.findByText("Radarr");
    expect(screen.getByText("Radarr-1080")).toBeInTheDocument();
  });

  it("groups Sonarr and Radarr processes into separate sections", async () => {
    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({
          processes: [
            {
              category: "radarr",
              name: "Radarr-1080",
              kind: "torrent",
              pid: 1,
              alive: true,
            },
            {
              category: "sonarr",
              name: "Sonarr-1080",
              kind: "torrent",
              pid: 2,
              alive: true,
            },
          ],
        }),
      ),
      http.get("/web/status", () =>
        HttpResponse.json({
          ...emptyStatus,
          arrs: [
            { category: "radarr", name: "Radarr-1080", type: "radarr" },
            { category: "sonarr", name: "Sonarr-1080", type: "sonarr" },
          ],
        }),
      ),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json(emptyCategories),
      ),
    );

    renderView();

    await screen.findByText("Radarr");
    expect(screen.getByText("Sonarr")).toBeInTheDocument();
  });

  it("does not show Radarr section when no radarr Arr instance is configured", async () => {
    // Process exists for radarr but no arr of type radarr in status → filtered out
    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({
          processes: [
            {
              category: "radarr",
              name: "Radarr-1080",
              kind: "torrent",
              pid: 1,
              alive: true,
            },
          ],
        }),
      ),
      http.get("/web/status", () =>
        HttpResponse.json({
          ...emptyStatus,
          arrs: [], // no radarr configured
        }),
      ),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json(emptyCategories),
      ),
    );

    renderView();

    await screen.findByText("No processes available.");
    expect(screen.queryByText("Radarr")).not.toBeInTheDocument();
  });
});

// ── qBit category chips ───────────────────────────────────────────────────────

describe("ProcessesView – qBit category chips", () => {
  it("shows category chips under qBittorrent section", async () => {
    const qbitProcess = {
      category: "qBit",
      name: "qBit",
      kind: "category",
      pid: 10,
      alive: true,
    };
    const qbitCategory = {
      category: "radarr",
      instance: "qBit",
      managedBy: "arr",
      torrentCount: 5,
      seedingCount: 2,
      totalSize: 0,
      avgRatio: 1.0,
      avgSeedingTime: 0,
      seedingConfig: {
        maxRatio: 2,
        maxTime: -1,
        removeMode: 3,
        downloadLimit: -1,
        uploadLimit: -1,
      },
    };

    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({ processes: [qbitProcess] }),
      ),
      http.get("/web/status", () =>
        HttpResponse.json({
          ...emptyStatus,
          qbitInstances: {
            qBit: {
              alive: true,
              host: "localhost",
              port: 8080,
              version: "5.0",
            },
          },
        }),
      ),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ categories: [qbitCategory], ready: true }),
      ),
    );

    renderView();

    await screen.findByText("qBittorrent");
    // The "radarr" chip should appear under the qBit card
    await screen.findByText("radarr");
  });

  it("shows 'Arr' badge on Arr-managed category chip", async () => {
    const qbitProcess = {
      category: "qBit",
      name: "qBit",
      kind: "category",
      pid: 10,
      alive: true,
    };
    const arrCategory = {
      category: "sonarr",
      instance: "qBit",
      managedBy: "arr",
      torrentCount: 3,
      seedingCount: 1,
      totalSize: 0,
      avgRatio: 1.0,
      avgSeedingTime: 0,
      seedingConfig: {
        maxRatio: 2,
        maxTime: -1,
        removeMode: 3,
        downloadLimit: -1,
        uploadLimit: -1,
      },
    };

    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({ processes: [qbitProcess] }),
      ),
      http.get("/web/status", () => HttpResponse.json(emptyStatus)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ categories: [arrCategory], ready: true }),
      ),
    );

    renderView();

    await screen.findByText("sonarr");
    // The managed-badge should say "Arr" for arr-managed categories
    expect(screen.getByText("Arr")).toBeInTheDocument();
  });
});

// ── Other section (Recheck, Failed, Free Space Manager) ───────────────────────

describe("ProcessesView – Other section", () => {
  it("shows Other section with Recheck, Failed, and Free Space Manager cards", async () => {
    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({
          processes: [
            {
              category: "Recheck",
              name: "Recheck",
              kind: "category",
              pid: null,
              alive: true,
              categoryCount: 0,
            },
            {
              category: "Failed",
              name: "Failed",
              kind: "category",
              pid: null,
              alive: true,
              categoryCount: 0,
            },
            {
              category: "FreeSpaceManager",
              name: "FreeSpaceManager",
              kind: "torrent",
              metricType: "free-space",
              pid: null,
              alive: true,
              categoryCount: 0,
            },
          ],
        }),
      ),
      http.get("/web/status", () => HttpResponse.json(emptyStatus)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json(emptyCategories),
      ),
    );

    renderView();

    await screen.findByText("Other");
    expect(screen.getAllByText("Recheck").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("Failed").length).toBeGreaterThanOrEqual(1);
    expect(
      screen.getAllByText("Free Space Manager").length,
    ).toBeGreaterThanOrEqual(1);
    expect(
      screen.getAllByText("Torrent count 0").length,
    ).toBeGreaterThanOrEqual(2);
    expect(screen.getByText("Paused: 0")).toBeInTheDocument();
  });
});
