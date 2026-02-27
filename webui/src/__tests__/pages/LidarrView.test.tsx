import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { LidarrView } from "../../pages/LidarrView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import { SearchProvider } from "../../context/SearchContext";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

const minimalConfig = {
  Settings: {},
  WebUI: { LiveArr: false, GroupSonarr: false, GroupLidarr: false },
};

const emptyArrList = { arr: [], ready: true };

const lidarrArrList = {
  arr: [{ category: "lidarr-hd", name: "Lidarr HD", type: "lidarr" }],
  ready: true,
};

// LidarrAlbumsResponse: albums is LidarrAlbumEntry[] — each entry has a nested `album` object
const emptyAlbumsResponse = {
  category: "lidarr-hd",
  albums: [],
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
  return render(<LidarrView active={active} />, { wrapper: Wrapper });
}

// ── Card header ───────────────────────────────────────────────────────────────

describe("LidarrView – card header", () => {
  it("renders the Lidarr card header", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList))
    );

    renderView();

    await screen.findByText("Lidarr");
  });
});

// ── Empty state (no instances) ────────────────────────────────────────────────

describe("LidarrView – empty state", () => {
  it("shows 'No albums found.' when no instances are configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList))
    );

    renderView();

    await screen.findByText("No albums found.");
  });

  it("shows 'No albums found.' when instance returns empty album list", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(lidarrArrList)),
      http.get("/web/lidarr/lidarr-hd/albums", () =>
        HttpResponse.json(emptyAlbumsResponse)
      )
    );

    renderView();

    await screen.findByText("No albums found.", {}, { timeout: 5000 });
  });
});

// ── Single instance ───────────────────────────────────────────────────────────

describe("LidarrView – instance sidebar", () => {
  it("shows instance button in sidebar when one lidarr instance is configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(lidarrArrList)),
      http.get("/web/lidarr/lidarr-hd/albums", () =>
        HttpResponse.json(emptyAlbumsResponse)
      )
    );

    renderView();

    // Use findByRole to get the sidebar button specifically (not the mobile select option)
    const btn = await screen.findByRole("button", { name: /lidarr hd/i }, { timeout: 5000 });
    expect(btn).toBeInTheDocument();
  });

  it("shows albums table when albums are returned", async () => {
    // LidarrAlbumsResponse.albums contains LidarrAlbumEntry objects:
    // each entry has { album: { artistName, title, ... }, totals, tracks }
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(lidarrArrList)),
      http.get("/web/lidarr/lidarr-hd/albums", () =>
        HttpResponse.json({
          category: "lidarr-hd",
          albums: [
            {
              album: {
                id: 1,
                title: "OK Computer",
                artistName: "Radiohead",
                releaseDate: "1997-05-21",
                monitored: true,
                hasFile: true,
                reason: null,
                qualityProfileName: "Lossless",
              },
              totals: { available: 10, monitored: 10, missing: 0 },
              tracks: [],
            },
          ],
          total: 1,
          page: 0,
          page_size: 50,
          counts: { available: 1, monitored: 1 },
        })
      )
    );

    renderView();

    // The sidebar button appears when instances load (before albums fetch completes).
    // Use waitFor to handle the race between instance load and album fetch.
    const btn = await screen.findByRole("button", { name: /lidarr hd/i }, { timeout: 5000 });
    expect(btn).toBeInTheDocument();
    await waitFor(
      () => expect(screen.queryByText("No albums found.")).not.toBeInTheDocument(),
      { timeout: 8000 }
    );
  });
});

// ── Inactive view ─────────────────────────────────────────────────────────────

describe("LidarrView – inactive", () => {
  it("renders card frame when active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({}))
    );

    renderView(false);

    await screen.findByText("Lidarr");
  });
});
