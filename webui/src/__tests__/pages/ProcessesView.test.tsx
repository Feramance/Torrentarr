import { NAVIGATE_TAB_EVENT } from "../../utils/navigateTab";
import userEvent from "@testing-library/user-event";
import React from "react";
import {
  describe,
  it,
  expect,
  beforeAll,
  afterAll,
  afterEach,
  vi,
} from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ProcessesView } from "../../pages/ProcessesView";
import { WebUIProvider } from "../../context/WebUIContext";
import { ToastProvider } from "../../context/ToastContext";

const server = setupServer(
  http.get("/web/config", () =>
    HttpResponse.json({ WebUI: { LiveArr: false } }),
  ),
  http.get("/web/statistics/arr-activity", () =>
    HttpResponse.json({
      range: "24h",
      bucketMinutes: 5,
      generatedAt: "2026-11-04T12:00:00Z",
      series: [],
    }),
  ),
);

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
  return (
    <ToastProvider>
      <WebUIProvider>{children}</WebUIProvider>
    </ToastProvider>
  );
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
    expect(
      screen.getByText("Awaiting first completed pass"),
    ).toBeInTheDocument();
    expect(
      screen.getByText("Awaiting first successful scan"),
    ).toBeInTheDocument();
    expect(screen.getByText("Searches").parentElement).toHaveTextContent("—");
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
        HttpResponse.json({
          processes: [
            qbitProcess,
            {
              name: "qbit-autobrr",
              category: "autobrr",
              kind: "category",
              alive: true,
            },
            {
              name: "qbit-seed",
              category: "seed",
              kind: "category",
              alive: true,
            },
            {
              name: "Recheck",
              category: "recheck",
              kind: "category",
              alive: true,
            },
          ],
        }),
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
        HttpResponse.json({
          categories: [qbitCategory, { ...qbitCategory, category: "seed" }],
          ready: true,
        }),
      ),
    );

    renderView();

    await screen.findByText("Torrent Clients");
    await screen.findByText("autobrr");
    expect(screen.getAllByText("autobrr")).toHaveLength(1);
    expect(screen.getByText("seed")).toBeInTheDocument();
    expect(screen.queryByText("qbit-autobrr")).not.toBeInTheDocument();
    expect(screen.queryByText("qbit-seed")).not.toBeInTheDocument();
    expect(screen.getByText("Recheck")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /autobrr/ }).querySelector(".badge"),
    ).toBeNull();
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
      screen.getAllByText("Torrents in category").length,
    ).toBeGreaterThanOrEqual(2);
    expect(screen.getByText("Paused for free space")).toBeInTheDocument();
  });
});

it("refreshes loop status without showing a worker badge", async () => {
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
            torrent: { phase: status, startedAt: "2026-10-18T12:00:00Z" },
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
    await screen.findByText("Running", {}, { timeout: 3000 }),
  ).toBeInTheDocument();
  expect(
    document.querySelector(".process-card__badge"),
  ).not.toBeInTheDocument();
  expect(screen.getByText("Arr queue").parentElement).toHaveTextContent("1");
});

it("keeps instance controls separate from activity with a secondary restart action", async () => {
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
  expect(await screen.findByText("Radarr-A")).toBeInTheDocument();
  expect(screen.queryByText("1/1")).not.toBeInTheDocument();
  await userEvent.click(screen.getByText("Actions"));
  expect(screen.getByText(/No active search/)).toBeInTheDocument();
  expect(screen.getByText("Arr queue").parentElement).toHaveTextContent("2");
  const card = screen.getByText("Radarr-A").closest(".process-card")!;
  expect(card.querySelectorAll(".process-activity")).toHaveLength(2);
  expect(card.querySelector(".process-metric")).not.toBeInTheDocument();
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
  expect(await screen.findByText("Radarr-A")).toBeInTheDocument();
  expect(screen.queryByText("0/1")).not.toBeInTheDocument();
  expect(
    screen
      .getAllByLabelText("Stopped")
      .filter((el) => el.classList.contains("process-status__dot")),
  ).toHaveLength(2);
  expect(screen.queryByText(/resumes/)).not.toBeInTheDocument();
});

it("keeps successful zero counts and stale observations visible", async () => {
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
            alive: true,
            searchesTriggered: 0,
            itemsSearched: 0,
            searchTimestamp: "2026-10-18T12:00:00Z",
            categoryCount: 12,
            downloadingCount: 0,
            seedingCount: 12,
            observedAt: "2026-10-18T12:00:00Z",
            observationError: "Client offline; showing last successful scan",
          },
        ],
      }),
    ),
    http.get("/web/qbit/categories", () => HttpResponse.json(emptyCategories)),
  );
  renderView();
  await screen.findByText("Radarr-A");
  expect(screen.getByText("Searches").parentElement).toHaveTextContent(
    "Searches 0",
  );
  expect(screen.getByText("Downloading").parentElement).toHaveTextContent(
    "Downloading 0",
  );
  expect(screen.getByText("Managed").parentElement).toHaveTextContent(
    "Managed 12",
  );
  expect(screen.getByText("Arr queue").parentElement).toHaveTextContent("—");
  expect(screen.getByText(/Client offline/)).toBeInTheDocument();
  expect(screen.getByText(/Scanned/)).toBeInTheDocument();
  expect(
    screen.queryByText(/Awaiting first completed pass/),
  ).not.toBeInTheDocument();
});

it.each(["worker", "torrent"])(
  "makes the Arr %s torrent activity target navigate without nesting restart buttons",
  async (kind) => {
    const navigation = vi.fn();
    window.addEventListener(NAVIGATE_TAB_EVENT, navigation);
    server.use(
      http.get("/web/processes", () =>
        HttpResponse.json({
          processes: [
            {
              name: "Sonarr-one",
              category: "fallback",
              kind,
              alive: true,
              pid: 10,
              restartable: true,
            },
          ],
        }),
      ),
      http.get("/web/status", () =>
        HttpResponse.json({
          ready: true,
          arrs: [{ name: "Sonarr-one", type: "sonarr", category: "tv" }],
          qbitInstances: { one: { alive: false } },
        }),
      ),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({
          ready: true,
          categories: [
            {
              instance: "one",
              category: "tv",
              managedBy: "arr",
              torrentCount: 2,
              seedingCount: 0,
              totalSize: 0,
            },
          ],
        }),
      ),
    );
    try {
      renderView();
      const button = await screen.findByRole("button", {
        name: "View torrents for Sonarr-one",
      });
      expect(button.querySelector("button")).toBeNull();
      fireEvent.click(button);
      expect(navigation).toHaveBeenCalledTimes(1);
      expect((navigation.mock.calls[0]![0] as CustomEvent).detail).toEqual({
        tab: "qbittorrent",
        qbitCategory: "tv",
        qbitInstance: "one",
      });
      if (kind === "worker")
        expect(
          screen.queryByRole("button", { name: /Search/ }),
        ).not.toBeInTheDocument();
    } finally {
      window.removeEventListener(NAVIGATE_TAB_EVENT, navigation);
    }
  },
);
