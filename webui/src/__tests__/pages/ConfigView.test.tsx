import { describe, it, expect, vi, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ConfigView } from "../../pages/ConfigView";
import { ToastProvider, ToastViewport } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
});
afterAll(() => server.close());

const okUpdateResponse = {
  status: "ok",
  configReloaded: false,
  reloadType: "none",
  affectedInstances: [],
};

// Config with qBit (default) + qBit-1 + one Radarr instance
const configWithInstances = {
  Settings: { LoopSleepTimer: 5, FailedCategory: "failed" },
  WebUI: { Port: 6969, Theme: "Dark", LiveArr: false },
  qBit: { Host: "localhost", Port: 8080, Disabled: false },
  "qBit-1": { Host: "192.168.1.100", Port: 9090, Disabled: false },
  "Radarr-1080": {
    Managed: true,
    URI: "http://radarr:7878",
    APIKey: "abc123",
    Category: "radarr",
  },
};

// Config with only the default qBit instance
const configQbitOnly = {
  Settings: { LoopSleepTimer: 5 },
  WebUI: { Port: 6969, LiveArr: false },
  qBit: { Host: "localhost", Port: 8080, Disabled: false },
};

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <ToastProvider>
      <WebUIProvider>
        {children}
        <ToastViewport />
      </WebUIProvider>
    </ToastProvider>
  );
}

function renderConfig() {
  return render(<ConfigView />, { wrapper: Wrapper });
}

// ── delete button rendering ───────────────────────────────────────────────────

describe("ConfigView – delete button rendering", () => {
  it("renders a Delete button for the default qBit instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    renderConfig();

    // Wait for config to load by checking for a known card header
    await screen.findByText("qBit");
    // Find the card that contains "qBit" (Default) and confirm its delete button
    const qbitHeader = screen.getByText("qBit");
    const card = qbitHeader.closest(".card")!;
    expect(within(card).getByRole("button", { name: /delete/i })).toBeInTheDocument();
  });

  it("renders a Delete button for the qBit-1 instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    renderConfig();

    await screen.findByText("qBit-1");
    const qbit1Header = screen.getByText("qBit-1");
    const card = qbit1Header.closest(".card")!;
    expect(within(card).getByRole("button", { name: /delete/i })).toBeInTheDocument();
  });

  it("renders Delete buttons for Arr instances", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    renderConfig();

    await screen.findByText("Radarr-1080");
    const arrHeader = screen.getByText("Radarr-1080");
    const card = arrHeader.closest(".card")!;
    expect(within(card).getByRole("button", { name: /delete/i })).toBeInTheDocument();
  });
});

// ── deleteQbitInstance logic ──────────────────────────────────────────────────

describe("ConfigView – deleteQbitInstance behaviour", () => {
  it("clicking Delete on the default qBit shows error toast and does not call window.confirm", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    const confirmSpy = vi.spyOn(window, "confirm");
    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit");
    const qbitHeader = screen.getByText("qBit");
    const card = qbitHeader.closest(".card")!;
    const deleteBtn = within(card).getByRole("button", { name: /delete/i });

    await user.click(deleteBtn);

    expect(confirmSpy).not.toHaveBeenCalled();
    // Toast with error message should appear
    await screen.findByText(/cannot be deleted/i);
  });

  it("clicking Delete on qBit-1 calls window.confirm", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(false);
    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit-1");
    const qbit1Header = screen.getByText("qBit-1");
    const card = qbit1Header.closest(".card")!;
    const deleteBtn = within(card).getByRole("button", { name: /delete/i });

    await user.click(deleteBtn);

    expect(confirmSpy).toHaveBeenCalledWith("Delete qBit-1? This action cannot be undone.");
  });

  it("confirms and removes qBit-1 from the UI when window.confirm returns true", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit-1");
    const qbit1Header = screen.getByText("qBit-1");
    const card = qbit1Header.closest(".card")!;
    const deleteBtn = within(card).getByRole("button", { name: /delete/i });

    await user.click(deleteBtn);

    // Card for qBit-1 should be gone
    expect(screen.queryByText("qBit-1")).not.toBeInTheDocument();
    // Default qBit card should still be present
    expect(screen.getByText("qBit")).toBeInTheDocument();
  });
});

// ── addQbitInstance logic ──────────────────────────────────────────────────────

describe("ConfigView – addQbitInstance behaviour", () => {
  it("clicking 'Add Instance' creates a qBit-1 card when qBit is the only instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit");
    expect(screen.queryByText("qBit-1")).not.toBeInTheDocument();

    // The qBit "Add Instance" button is inside the "qBittorrent Instances" section.
    // Arr groups (Radarr/Sonarr/Lidarr) each have their own "Add Instance" button,
    // so we must scope the query to avoid ambiguity.
    const qbitSection = screen.getByText("qBittorrent Instances").closest("section")!;
    const addBtn = within(qbitSection).getByRole("button", { name: /add instance/i });
    await user.click(addBtn);

    // findAllByText: the card header AND the modal title both contain "qBit-1"
    await screen.findAllByText("qBit-1");
  });

  it("clicking 'Add Instance' twice creates qBit-1 and then qBit-2", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse))
    );

    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit");

    // Click once → creates qBit-1 and opens its configure modal
    const getAddBtn = () =>
      within(screen.getByText("qBittorrent Instances").closest("section")!).getByRole(
        "button",
        { name: /add instance/i }
      );

    await user.click(getAddBtn());
    // Card header + modal title both show "qBit-1"
    await screen.findAllByText("qBit-1");

    // Click again → creates qBit-2 (re-query button since DOM updated)
    await user.click(getAddBtn());
    await screen.findAllByText("qBit-2");
  });
});
