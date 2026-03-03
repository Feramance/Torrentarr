import { describe, it, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ArrView } from "../../pages/ArrView";
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

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <ToastProvider>
      <WebUIProvider>
        <SearchProvider>{children}</SearchProvider>
      </WebUIProvider>
    </ToastProvider>
  );
}

// ── Routing ───────────────────────────────────────────────────────────────────

describe("ArrView – routing", () => {
  it("renders RadarrView (shows 'Radarr' header) when type=radarr", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList))
    );

    render(<ArrView type="radarr" active={true} />, { wrapper: Wrapper });

    await screen.findByText("Radarr");
  });

  it("renders SonarrView (shows 'Sonarr' header) when type=sonarr", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList))
    );

    render(<ArrView type="sonarr" active={true} />, { wrapper: Wrapper });

    await screen.findByText("Sonarr");
  });

  it("renders LidarrView (shows 'Lidarr' header) when type=lidarr", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList))
    );

    render(<ArrView type="lidarr" active={true} />, { wrapper: Wrapper });

    await screen.findByText("Lidarr");
  });
});

// ── Inactive views ────────────────────────────────────────────────────────────

describe("ArrView – inactive", () => {
  it("renders RadarrView card frame when type=radarr active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({}))
    );

    render(<ArrView type="radarr" active={false} />, { wrapper: Wrapper });

    await screen.findByText("Radarr");
  });

  it("renders SonarrView card frame when type=sonarr active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({}))
    );

    render(<ArrView type="sonarr" active={false} />, { wrapper: Wrapper });

    await screen.findByText("Sonarr");
  });

  it("renders LidarrView card frame when type=lidarr active=false", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({}))
    );

    render(<ArrView type="lidarr" active={false} />, { wrapper: Wrapper });

    await screen.findByText("Lidarr");
  });
});
