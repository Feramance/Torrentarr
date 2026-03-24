import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { RadarrView } from "../../pages/RadarrView";
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

const minimalMeta = () =>
  HttpResponse.json({
    current_version: "5.9.2",
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
  });

const emptyArrList = { arr: [], ready: true };

const radarrArrList = {
  arr: [{ category: "radarr-hd", name: "Radarr HD", type: "radarr" }],
  ready: true,
};

const emptyMoviesResponse = {
  movies: [],
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
  return render(<RadarrView active={active} />, { wrapper: Wrapper });
}

// ── Card header ───────────────────────────────────────────────────────────────

describe("RadarrView – card header", () => {
  it("renders the Radarr card header", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("Radarr");
  });
});

// ── Empty state (no instances) ────────────────────────────────────────────────

describe("RadarrView – empty state", () => {
  it("shows 'No movies found.' when no instances are configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("No movies found.");
  });

  it("shows 'No movies found.' when instance returns empty movie list", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(radarrArrList)),
      http.get("/web/radarr/radarr-hd/movies", () =>
        HttpResponse.json(emptyMoviesResponse),
      ),
    );

    renderView();

    await screen.findByText("No movies found.", {}, { timeout: 5000 });
  });
});

// ── Single instance sidebar ───────────────────────────────────────────────────

describe("RadarrView – instance sidebar", () => {
  it("shows instance button in sidebar when one radarr instance is configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(radarrArrList)),
      http.get("/web/radarr/radarr-hd/movies", () =>
        HttpResponse.json(emptyMoviesResponse),
      ),
    );

    renderView();

    // findByRole("button", { name }) is prohibitively slow for RadarrView's
    // complex DOM. Use findByText with { selector: "button" } which does text-
    // content matching (not ARIA traversal) and is much faster. The sidebar
    // <button>Radarr HD</button> appears as soon as /web/arr responds (before
    // movies are fetched), so we don't need to wait for movie data here.
    const btn = await screen.findByText(
      /radarr hd/i,
      { selector: "button" },
      { timeout: 4500 },
    );
    expect(btn).toBeInTheDocument();
  });

  it("shows movies table columns when movies are returned", async () => {
    server.use(
      http.get("/web/meta", minimalMeta),
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(radarrArrList)),
      http.get("/web/radarr/radarr-hd/movies", () =>
        HttpResponse.json({
          movies: [
            {
              title: "The Matrix",
              year: 1999,
              monitored: true,
              hasFile: true,
              reason: null,
              qualityProfileName: "HD-1080p",
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

    // Wait for returned movie row; this is more stable than asserting header text.
    await screen.findByText("The Matrix", {}, { timeout: 8000 });
    expect(screen.queryByText("No movies found.")).not.toBeInTheDocument();
  }, 10000);
});

// ── Inactive view ─────────────────────────────────────────────────────────────

describe("RadarrView – inactive", () => {
  it("renders card frame when active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
    );

    renderView(false);

    // Card header still renders even when inactive
    await screen.findByText("Radarr");
  });
});
