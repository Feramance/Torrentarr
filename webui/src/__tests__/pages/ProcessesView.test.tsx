import userEvent from "@testing-library/user-event";
import React from "react";
import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
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
      category: "autobrr",
      instance: "qBit",
      managedBy: "qbit",
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

    await screen.findByText("Torrent Clients");
    await screen.findByText("autobrr");
  });

  it("omits Arr-managed categories without hiding client-owned names on another instance", async () => {
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
      http.get("/web/status", () =>
        HttpResponse.json({
          ...emptyStatus,
          qbitInstances: { qBit: { alive: true }, seedbox: { alive: true } },
        }),
      ),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({
          categories: [
            arrCategory,
            { ...arrCategory, instance: "seedbox", managedBy: "qbit" },
            { ...arrCategory, category: "autobrr", managedBy: "qbit" },
          ],
          ready: true,
        }),
      ),
    );

    renderView();

    await screen.findByText("autobrr");
    expect(screen.getAllByText("sonarr")).toHaveLength(1);
    expect(screen.queryByText("Arr")).not.toBeInTheDocument();
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
              freeSpacePaused: 0,
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
      screen.getAllByText("Managing 0 categories").length,
    ).toBeGreaterThanOrEqual(2);
    expect(screen.getByText("0 paused for free space")).toBeInTheDocument();
  });
});

it("refreshes worker status when its other metrics are unchanged", async () => {
  let status = "Sleeping";
  server.use(
    http.get("/web/processes", () =>
      HttpResponse.json({
        processes: [
          {
            category: "radarr",
            name: "Radarr",
            kind: "worker",
            pid: 1,
            alive: true,
            status,
            queueCount: 1,
            categoryCount: 12,
          },
        ],
      }),
    ),
    http.get("/web/status", () =>
      HttpResponse.json({
        ...emptyStatus,
        arrs: [{ category: "radarr", name: "Radarr", type: "radarr" }],
      }),
    ),
    http.get("/web/qbit/categories", () => HttpResponse.json(emptyCategories)),
  );
  renderView();
  await screen.findByText(/Sleeping/);
  status = "Running";
  expect(
    await screen.findByText(/Worker · Running/, {}, { timeout: 3000 }),
  ).toBeInTheDocument();
  expect(screen.getByText(/1 in progress/)).toBeInTheDocument();
});

it("shows two loop activities with one physical worker count and restart", async () => {
  let restarts = 0;
  server.use(
    http.get("/web/status", () =>
      HttpResponse.json({
        ...emptyStatus,
        arrs: [{ name: "Radarr-A", category: "shared", type: "radarr" }],
      }),
    ),
    http.get("/web/processes", () =>
      HttpResponse.json({
        processes: [
          {
            name: "Radarr-A",
            category: "shared",
            kind: "worker",
            restartKey: "Radarr-A",
            restartable: true,
            alive: true,
            pid: 42,
            search: { phase: "Sleeping", startedAt: "2026-10-18T12:00:00Z" },
            torrent: { phase: "Running", startedAt: "2026-10-18T12:00:00Z" },
            searchSummary: "0 searches triggered (2 items)",
            queueCount: 2,
            categoryCount: 3,
          },
        ],
      }),
    ),
    http.get("/web/qbit/categories", () => HttpResponse.json(emptyCategories)),
    http.post("/web/processes/Radarr-A/worker/restart", () => {
      restarts++;
      return HttpResponse.json({
        status: "restarted",
        restarted: ["Radarr-A"],
      });
    }),
  );
  renderView();
  expect(await screen.findByText("1/1")).toBeInTheDocument();
  expect(screen.getByText(/No active search/)).toBeInTheDocument();
  expect(screen.getByText(/2 in progress/)).toBeInTheDocument();
  expect(
    screen.getAllByRole("button", { name: "Restart worker", exact: true }),
  ).toHaveLength(1);
  await userEvent.click(
    screen.getByRole("button", { name: "Restart worker", exact: true }),
  );
  await waitFor(() => expect(restarts).toBe(1));
});

it("marks stopped worker activities as stopped despite a stale heartbeat", async () => {
  server.use(
    http.get("/web/status", () =>
      HttpResponse.json({
        ...emptyStatus,
        arrs: [{ name: "Radarr-A", category: "movies", type: "radarr" }],
      }),
    ),
    http.get("/web/qbit/categories", () => HttpResponse.json(emptyCategories)),
    http.get("/web/processes", () =>
      HttpResponse.json({
        processes: [
          {
            name: "Radarr-A",
            category: "movies",
            kind: "worker",
            alive: false,
            pid: 42,
            search: { phase: "Sleeping", wakeAt: "2026-10-18T12:01:00Z" },
            torrent: { phase: "Running" },
          },
        ],
      }),
    ),
  );
  renderView();
  expect(await screen.findByText("0/1")).toBeInTheDocument();
  expect(
    screen
      .getAllByLabelText("Stopped")
      .filter((el) => el.classList.contains("process-status__dot")),
  ).toHaveLength(2);
  expect(screen.queryByText(/resumes/)).not.toBeInTheDocument();
});
