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
import {
  render,
  screen,
  waitFor,
  fireEvent,
  within,
} from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ArrCatalogView } from "../../pages/ArrCatalogView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import { SearchProvider } from "../../context/SearchContext";

vi.mock("../../utils/sharedIntersectionObserver", () => ({
  observePosterVisibility: () => () => {},
}));

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

const minimalMeta = () =>
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
  localStorage.setItem("torrentarr.arrBrowseMode.radarr", "list");
  return render(<ArrCatalogView kind="radarr" active={active} />, {
    wrapper: Wrapper,
  });
}

// ── Card header ───────────────────────────────────────────────────────────────

describe("Radarr catalog – card header", () => {
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

describe("Radarr catalog – empty state", () => {
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
      http.get("/web/radarr/Radarr%20HD/movies", () =>
        HttpResponse.json(emptyMoviesResponse),
      ),
    );

    renderView();

    await screen.findByText(
      "No movies in the local catalog.",
      {},
      { timeout: 5000 },
    );
  });
});

// ── Single instance sidebar ───────────────────────────────────────────────────

describe("Radarr catalog – instance sidebar", () => {
  it("shows instance button in sidebar when one radarr instance is configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(radarrArrList)),
      http.get("/web/radarr/Radarr%20HD/movies", () =>
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
      http.get("/web/radarr/Radarr%20HD/movies", () =>
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

    // Table columns always appear when movies are loaded; the empty state can
    // still be mounted briefly while the async fetch resolves on slower runners.
    await screen.findByText("Title", {}, { timeout: 8000 });
    await waitFor(() => {
      expect(screen.queryByText("No movies found.")).not.toBeInTheDocument();
    });
  }, 10000);
});

it("paginates, searches, filters missing movies, and opens details in the active catalog", async () => {
  const requests: URL[] = [];
  server.use(
    http.get("/web/meta", minimalMeta),
    http.get("/web/config", () => HttpResponse.json(minimalConfig)),
    http.post("/web/config", () => HttpResponse.json({})),
    http.get("/web/arr", () => HttpResponse.json(radarrArrList)),
    http.get("/web/radarr/Radarr%20HD/movies", ({ request }) => {
      const url = new URL(request.url);
      requests.push(url);
      const page = Number(url.searchParams.get("page"));
      const query = url.searchParams.get("q");
      const all = Array.from({ length: 51 }, (_, i) => ({
        id: i + 1,
        title: i === 0 ? "First page" : i === 50 ? "Second page" : `Movie ${i}`,
        monitored: true,
        hasFile: i < 50,
        reason: i === 50 ? "Missing" : "Quality",
      }));
      all.push({
        id: 52,
        title: "Wanted",
        monitored: true,
        hasFile: false,
        reason: "Upgrade",
      });
      const filtered = all.filter(
        (movie) =>
          (!query || movie.title.includes(query)) &&
          (url.searchParams.get("has_file") !== "false" || !movie.hasFile) &&
          (!url.searchParams.get("reason") ||
            url.searchParams.get("reason") === "all" ||
            movie.reason === url.searchParams.get("reason")),
      );
      return HttpResponse.json({
        movies: filtered.slice(page * 50, (page + 1) * 50),
        total: filtered.length,
        page,
        page_size: 50,
        counts: { available: 50, monitored: 52 },
      });
    }),
  );
  localStorage.setItem("torrentarr.arrBrowseMode.radarr", "list");
  render(<ArrCatalogView kind="radarr" active />, { wrapper: Wrapper });
  await screen.findByText("First page");
  fireEvent.click(screen.getByRole("button", { name: "Next" }));
  await screen.findByText("Second page");
  fireEvent.change(screen.getByLabelText("Status"), {
    target: { value: "missing" },
  });
  await waitFor(() =>
    expect(requests.at(-1)?.searchParams.get("has_file")).toBe("false"),
  );
  await screen.findByText("Wanted");
  expect(requests.at(-1)?.searchParams.get("page")).toBe("0");
  expect(
    screen.queryByRole("button", { name: "Next" }),
  ).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Search Reason"), {
    target: { value: "Missing" },
  });
  await waitFor(() =>
    expect(requests.at(-1)?.searchParams.get("reason")).toBe("Missing"),
  );
  await waitFor(() =>
    expect(screen.queryByText("Wanted")).not.toBeInTheDocument(),
  );
  fireEvent.change(screen.getByLabelText("Search Reason"), {
    target: { value: "all" },
  });
  fireEvent.change(screen.getByLabelText("Search"), {
    target: { value: "Wanted" },
  });
  await screen.findByText("Wanted");
  expect(requests.at(-1)?.searchParams.get("page")).toBe("0");
  expect(requests.at(-1)?.searchParams.get("q")).toBe("Wanted");
  fireEvent.change(screen.getByLabelText("Status"), {
    target: { value: "missing" },
  });
  await waitFor(() => expect(screen.getByText("Wanted")).toBeInTheDocument());
  fireEvent.click(screen.getByText("Wanted").closest("tr")!);
  const dialog = await screen.findByRole("dialog");
  expect(within(dialog).getByText("Wanted")).toBeInTheDocument();
  fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Search"), {
    target: { value: "absent" },
  });
  await screen.findByText("No movies match the current filters.");
  expect(
    screen.queryByText("No movies in the local catalog."),
  ).not.toBeInTheDocument();
});

// ── Inactive view ─────────────────────────────────────────────────────────────

describe("Radarr catalog – inactive", () => {
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
