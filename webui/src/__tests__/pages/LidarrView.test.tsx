import React from "react";
import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ArrCatalogView } from "../../pages/ArrCatalogView";
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
  WebUI: { LiveArr: false },
};

const emptyArrList = { arr: [], ready: true };

const lidarrArrList = {
  arr: [{ category: "lidarr-hd", name: "Lidarr HD", type: "lidarr" }],
  ready: true,
};

// Artist browse responses wrap each artist in an entry.
const emptyArtistsResponse = {
  category: "lidarr-hd",
  artists: [],
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
  localStorage.setItem("torrentarr.arrBrowseMode.lidarr", "list");
  return render(<ArrCatalogView kind="lidarr" active={active} />, {
    wrapper: Wrapper,
  });
}

// ── Card header ───────────────────────────────────────────────────────────────

describe("Lidarr catalog – card header", () => {
  it("renders the Lidarr catalog region", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByRole("region", { name: "Lidarr" });
  });
});

// ── Empty state (no instances) ────────────────────────────────────────────────

describe("Lidarr catalog – empty state", () => {
  it("shows 'No artists match the current filters.' when no instances are configured", async () => {
    // With no instances the aggregate view shows "No artists match the current filters."
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("No artists match the current filters.");
  });

  it("shows 'No artists in the local catalog.' when instance returns empty artist list", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(lidarrArrList)),
      http.get("/web/lidarr/Lidarr%20HD/artists", () =>
        HttpResponse.json(emptyArtistsResponse),
      ),
    );

    renderView();

    await screen.findByText(
      "No artists in the local catalog.",
      {},
      { timeout: 10000 },
    );
  }, 12000);
});

// ── Single instance ───────────────────────────────────────────────────────────

describe("Lidarr catalog – centralized instance selection", () => {
  it("does not duplicate navigation when one lidarr instance is configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(lidarrArrList)),
      http.get("/web/lidarr/Lidarr%20HD/artists", () =>
        HttpResponse.json(emptyArtistsResponse),
      ),
    );

    renderView();

    // Use findByRole to get the sidebar button specifically (not the mobile select option)
    await screen.findByRole("region", { name: "Lidarr HD" });
    expect(
      screen.queryByRole("button", { name: /lidarr hd/i }),
    ).not.toBeInTheDocument();
  });

  it("shows artist rows when artists are returned", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(lidarrArrList)),
      http.get("/web/lidarr/Lidarr%20HD/artists", () =>
        HttpResponse.json({
          category: "lidarr-hd",
          artists: [
            {
              artist: {
                id: 1,
                name: "Radiohead",
                albumCount: 1,
                trackTotalCount: 10,
                monitored: true,
                reason: null,
                qualityProfileName: "Lossless",
              },
            },
          ],
          total: 1,
          page: 0,
          page_size: 50,
          counts: { available: 1, monitored: 1 },
        }),
      ),
    );

    renderView();

    // The sidebar button appears when instances load (before albums fetch completes).
    // Use waitFor to handle the race between instance load and album fetch.
    await screen.findByRole("region", { name: "Lidarr HD" });
    expect(
      screen.queryByRole("button", { name: /lidarr hd/i }),
    ).not.toBeInTheDocument();
    await screen.findByText("Radiohead");
  });
});

// ── Inactive view ─────────────────────────────────────────────────────────────

describe("Lidarr catalog – inactive", () => {
  it("renders card frame when active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
    );

    renderView(false);

    await screen.findByRole("region", { name: "Lidarr" });
  });
});
