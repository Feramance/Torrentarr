import React from "react";
import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { SonarrView } from "../../pages/SonarrView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import { SearchProvider } from "../../context/SearchContext";

// Permanent catch-all: settles any stale inflightRequests promises instantly after
// resetHandlers() clears runtime handlers (prevents cross-test contamination).
const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(async () => {
  server.resetHandlers();
  server.use(http.all("*", () => new HttpResponse(null, { status: 500 })));
  await new Promise<void>((r) => setTimeout(r, 50));
  server.resetHandlers();
});
afterAll(() => server.close());

const minimalConfig = {
  Settings: {},
  WebUI: { LiveArr: false, GroupSonarr: false, GroupLidarr: false },
};

const emptyArrList = { arr: [], ready: true };

const sonarrArrList = {
  arr: [{ category: "sonarr-hd", name: "Sonarr HD", type: "sonarr" }],
  ready: true,
};

const emptySeriesResponse = {
  series: [],
  total: 0,
  page: 0,
  page_size: 50,
  counts: { available: 0, monitored: 0 },
};

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <ToastProvider>
      <WebUIProvider>
        <SearchProvider>{children}</SearchProvider>
      </WebUIProvider>
    </ToastProvider>
  );
}

function renderView(active = true) {
  return render(<SonarrView active={active} />, { wrapper: Wrapper });
}

// ── Card header ───────────────────────────────────────────────────────────────

describe("SonarrView – card header", () => {
  it("renders the Sonarr card header", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("Sonarr");
  });
});

// ── Empty state ───────────────────────────────────────────────────────────────

describe("SonarrView – empty state", () => {
  it("shows 'No series found.' when no instances are configured", async () => {
    // WebUIContext defaults groupSonarr=true; after config loads (GroupSonarr:false)
    // the flat aggregate view renders "No series found.". Allow extra time for the
    // config fetch + state update under CPU load from parallel test workers.
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("No series found.", {}, { timeout: 5000 });
  });

  it("shows 'No series found.' when instance returns empty series list", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(sonarrArrList)),
      http.get("/web/sonarr/sonarr-hd/series", () =>
        HttpResponse.json(emptySeriesResponse),
      ),
    );

    renderView();

    await screen.findByText("No series found.", {}, { timeout: 5000 });
  });
});

// ── Single instance ───────────────────────────────────────────────────────────

describe("SonarrView – instance sidebar", () => {
  it("shows instance button in sidebar when one sonarr instance is configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(sonarrArrList)),
      http.get("/web/sonarr/sonarr-hd/series", () =>
        HttpResponse.json(emptySeriesResponse),
      ),
    );

    renderView();

    // Use findByRole to get the sidebar button specifically (not the mobile select option)
    const btn = await screen.findByRole(
      "button",
      { name: /sonarr hd/i },
      { timeout: 5000 },
    );
    expect(btn).toBeInTheDocument();
  });

  it("shows series table columns when series are returned", async () => {
    // SonarrSeriesEntry format: { series: { title, ... }, totals: { ... }, seasons: { "N": { episodes: [...] } } }
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(sonarrArrList)),
      http.get("/web/sonarr/sonarr-hd/series", () =>
        HttpResponse.json({
          series: [
            {
              series: {
                title: "Breaking Bad",
                qualityProfileName: "HD-1080p",
              },
              totals: { available: 1, monitored: 1 },
              seasons: {
                "1": {
                  monitored: 1,
                  available: 1,
                  episodes: [
                    {
                      id: 1,
                      title: "Pilot",
                      episodeNumber: 1,
                      seasonNumber: 1,
                      monitored: true,
                      hasFile: true,
                    },
                  ],
                },
              },
            },
          ],
          total: 1,
          page: 0,
          page_size: 25,
          counts: { available: 1, monitored: 1 },
        }),
      ),
    );

    renderView();

    // When episodes load (groupSonarr=false → flat table), the "Title" column header appears
    await screen.findByText("Title", {}, { timeout: 5000 });
    expect(screen.queryByText("No series found.")).not.toBeInTheDocument();
  });
});

// ── Inactive view ─────────────────────────────────────────────────────────────

describe("SonarrView – inactive", () => {
  it("renders card frame when active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
    );

    renderView(false);

    await screen.findByText("Sonarr");
  });
});
