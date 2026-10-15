import React from "react";
import {
  describe,
  it,
  expect,
  vi,
  beforeAll,
  afterAll,
  afterEach,
} from "vitest";
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

// Config with qBit + qBit-1 + one Radarr instance
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

// Config with only the qBit instance
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
  it("renders a Delete button for the qBit instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    renderConfig();

    // Wait for config to load by checking for a known card header
    await screen.findByText("qBit");
    // Find the card that contains "qBit" and confirm its delete button
    const qbitHeader = screen.getByText("qBit");
    const card = qbitHeader.closest(".card")!;
    expect(
      within(card).getByRole("button", { name: /delete/i }),
    ).toBeInTheDocument();
  });

  it("renders a Delete button for the qBit-1 instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    renderConfig();

    await screen.findByText("qBit-1");
    const qbit1Header = screen.getByText("qBit-1");
    const card = qbit1Header.closest(".card")!;
    expect(
      within(card).getByRole("button", { name: /delete/i }),
    ).toBeInTheDocument();
  });

  it("renders Delete buttons for Arr instances", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    renderConfig();

    await screen.findByText("Radarr-1080");
    const arrHeader = screen.getByText("Radarr-1080");
    const card = arrHeader.closest(".card")!;
    expect(
      within(card).getByRole("button", { name: /delete/i }),
    ).toBeInTheDocument();
  });
});

// ── deleteQbitInstance logic ──────────────────────────────────────────────────

describe("ConfigView – deleteQbitInstance behaviour", () => {
  it("confirms and removes qBit just like any other instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit");
    const qbitHeader = screen.getByText("qBit");
    const card = qbitHeader.closest(".card")!;
    const deleteBtn = within(card).getByRole("button", { name: /delete/i });

    await user.click(deleteBtn);

    expect(confirmSpy).toHaveBeenCalledWith(
      "Delete qBit? This action cannot be undone.",
    );
    expect(screen.queryByText("qBit")).not.toBeInTheDocument();
    expect(screen.getByText("qBit-1")).toBeInTheDocument();
  });

  it("clicking Delete on qBit-1 calls window.confirm", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(false);
    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit-1");
    const qbit1Header = screen.getByText("qBit-1");
    const card = qbit1Header.closest(".card")!;
    const deleteBtn = within(card).getByRole("button", { name: /delete/i });

    await user.click(deleteBtn);

    expect(confirmSpy).toHaveBeenCalledWith(
      "Delete qBit-1? This action cannot be undone.",
    );
  });

  it("confirms and removes qBit-1 from the UI when window.confirm returns true", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
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
    // Other qBit card should still be present
    expect(screen.getByText("qBit")).toBeInTheDocument();
  });
});

describe("ConfigView – equal client names", () => {
  it.each([
    [["qBit-renamed"], "ok"],
    [["qBit-middle", "qBit-renamed"], "ok"],
    [["qBit-renamed"], "retry"],
    [["qBit-renamed"], "warning"],
    [["qBit-renamed", "qBit"], "back"],
  ] as const)(
    "renames qBit through %j with save outcome %s",
    async (names, outcome) => {
      let changes: Record<string, unknown> = {};
      let renames: Record<string, string> = {};
      let saves = 0;
      server.use(
        http.get("/web/config", () =>
          HttpResponse.json({
            ...configQbitOnly,
            Settings: {
              ...configQbitOnly.Settings,
              ConsoleLevel: "INFO",
              CompletedDownloadFolder: "/downloads",
              FreeSpace: "10G",
              FreeSpaceFolder: "/downloads",
              NoInternetSleepTimer: 60,
              SearchLoopDelay: -1,
              IgnoreTorrentsYoungerThan: 0,
              AutoUpdateCron: "0 0 * * *",
              AutoUpdateChannel: "latest",
              MaxProcessRestarts: 5,
              ProcessRestartWindow: 3600,
              ProcessRestartDelay: 5,
            },
            WebUI: { ...configQbitOnly.WebUI, Host: "localhost" },
            qBit: {
              ...configQbitOnly.qBit,
              CategorySeeding: {
                MaxUploadRatio: 0,
                DownloadRateLimitPerTorrent: -1,
                UploadRateLimitPerTorrent: -1,
                HitAndRunMode: "disabled",
              },
            },
          }),
        ),
        http.post("/web/config", async ({ request }) => {
          const payload = (await request.json()) as {
            changes: Record<string, unknown>;
            renames: Record<string, string>;
          };
          changes = payload.changes;
          renames = payload.renames;
          saves++;
          if (outcome === "retry" && saves === 1)
            return HttpResponse.json(
              {
                error:
                  "Configuration is busy. Retry when processing completes.",
              },
              { status: 409 },
            );
          return HttpResponse.json({
            ...okUpdateResponse,
            warning:
              outcome === "warning"
                ? "Configuration saved, but workers failed to restart"
                : null,
          });
        }),
      );
      const user = userEvent.setup();
      renderConfig();
      const header = await screen.findByText("qBit");
      expect(screen.queryByText("(Default)")).not.toBeInTheDocument();
      await user.click(
        within(header.closest(".card")!).getByRole("button", {
          name: /configure/i,
        }),
      );
      const dialog = screen.getByRole("dialog");
      for (const newName of names) {
        const name = within(dialog).getByPlaceholderText("qBit-seedbox");
        await user.clear(name);
        await user.type(name, `${newName}{Enter}`);
      }
      await user.click(
        within(screen.getByRole("dialog")).getByRole("button", {
          name: /done/i,
        }),
      );
      await user.click(
        screen.getByRole("button", { name: /save \+ live reload/i }),
      );
      if (outcome === "retry") {
        await screen.findByText(/configuration is busy/i);
        await user.click(
          screen.getByRole("button", { name: /save \+ live reload/i }),
        );
      }
      await screen.findByText(
        outcome === "back"
          ? /no changes detected|configuration saved/i
          : /configuration saved/i,
      );
      if (outcome === "back") {
        expect(renames).toEqual({});
        expect(changes["qBit.Host"]).not.toBeNull();
        return;
      }
      if (outcome === "warning") {
        expect(
          screen.getByText(
            "Configuration saved, but workers failed to restart",
          ),
        ).toBeInTheDocument();
        expect(
          screen.queryByText(/all instances reloaded/i),
        ).not.toBeInTheDocument();
      }
      if (outcome === "retry") expect(saves).toBe(2);
      expect(changes["qBit.Host"]).toBeNull();
      expect(changes["qBit-renamed.Host"]).toBe("localhost");
      expect(renames).toEqual({ qBit: "qBit-renamed" });
    },
  );
});

// ── addQbitInstance logic ──────────────────────────────────────────────────────

describe("ConfigView – addQbitInstance behaviour", () => {
  it("clicking 'Add Instance' creates a qBit-1 card when qBit is the only instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit");
    expect(screen.queryByText("qBit-1")).not.toBeInTheDocument();

    // The qBit "Add Instance" button is inside the "qBittorrent Instances" section.
    // Arr groups (Radarr/Sonarr/Lidarr) each have their own "Add Instance" button,
    // so we must scope the query to avoid ambiguity.
    const qbitSection = screen
      .getByText("qBittorrent Instances")
      .closest("section")!;
    const addBtn = within(qbitSection).getByRole("button", {
      name: /add instance/i,
    });
    await user.click(addBtn);

    // findAllByText: the card header AND the modal title both contain "qBit-1"
    await screen.findAllByText("qBit-1");
  });

  it("clicking 'Add Instance' twice creates qBit-1 and then qBit-2", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();

    renderConfig();

    await screen.findByText("qBit");

    // Click once → creates qBit-1 and opens its configure modal
    const getAddBtn = () =>
      within(
        screen.getByText("qBittorrent Instances").closest("section")!,
      ).getByRole("button", { name: /add instance/i });

    await user.click(getAddBtn());
    // Card header + modal title both show "qBit-1"
    await screen.findAllByText("qBit-1");

    // Click again → creates qBit-2 (re-query button since DOM updated)
    await user.click(getAddBtn());
    await screen.findAllByText("qBit-2");
  });
});

// ── UrlBase field ─────────────────────────────────────────────────────────────

describe("ConfigView – UrlBase field", () => {
  it("renders API schema fields that are marked for UI exposure", async () => {
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          ...configQbitOnly,
          WebUI: {
            ...configQbitOnly.WebUI,
            AllowInsecureExposure: false,
            HiddenSchemaField: false,
          },
        }),
      ),
      http.get("/web/config/schema", () =>
        HttpResponse.json({
          version: 1,
          sections: {
            WebUI: [
              {
                section: "WebUI",
                path: ["AllowInsecureExposure"],
                key: "AllowInsecureExposure",
                label: "Allow Insecure Exposure",
                kind: "checkbox",
                default: false,
                comments: "Acknowledge an intentionally public bind.",
                required: false,
                secure: false,
                uiExpose: true,
                applyLive: false,
                requiresRestart: true,
              },
              {
                section: "WebUI",
                path: ["HiddenSchemaField"],
                key: "HiddenSchemaField",
                label: "Hidden Schema Field",
                kind: "checkbox",
                default: false,
                comments: "Not intended for the editor.",
                required: false,
                secure: false,
                uiExpose: false,
                applyLive: false,
                requiresRestart: true,
              },
            ],
          },
        }),
      ),
    );

    const user = userEvent.setup();
    renderConfig();

    const webSettingsCard = (await screen.findByText("Web Settings")).closest(
      ".card",
    )!;
    await user.click(
      within(webSettingsCard).getByRole("button", { name: /configure/i }),
    );

    expect(
      await screen.findByLabelText("Allow Insecure Exposure"),
    ).toBeInTheDocument();
    expect(screen.queryByText("Hidden Schema Field")).not.toBeInTheDocument();
  });

  it("renders Url Base field in Web Settings modal", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();
    renderConfig();

    await screen.findByText("Web Settings");
    const webSettingsCard = screen.getByText("Web Settings").closest(".card")!;
    await user.click(
      within(webSettingsCard).getByRole("button", { name: /configure/i }),
    );
    expect(await screen.findByText("Url Base")).toBeInTheDocument();
  });

  it("shows validation toast for invalid UrlBase on save", async () => {
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          ...configQbitOnly,
          WebUI: { ...configQbitOnly.WebUI, UrlBase: "no-leading-slash" },
        }),
      ),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();
    renderConfig();

    await screen.findByText("Save + Live Reload");
    await user.click(
      screen.getByRole("button", { name: /save \+ live reload/i }),
    );

    expect(
      await screen.findByText(/UrlBase must start with \//i),
    ).toBeInTheDocument();
  });

  it("stores valid UrlBase in form state for save", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();
    renderConfig();

    await screen.findByText("Web Settings");
    const webSettingsCard = screen.getByText("Web Settings").closest(".card")!;
    await user.click(
      within(webSettingsCard).getByRole("button", { name: /configure/i }),
    );
    await screen.findByText("Url Base");
    const urlBaseField = screen.getByText("Url Base").closest(".field")!;
    const urlBaseInput = within(urlBaseField).getByRole("textbox");
    await user.clear(urlBaseInput);
    await user.type(urlBaseInput, "/qbitrr");

    expect(urlBaseInput).toHaveValue("/qbitrr");
  });
});

describe("ConfigView – Arr Import Mode and Entry Search", () => {
  it("populates Import Mode and Entry Search from API JSON", async () => {
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          ...configWithInstances,
          "Radarr-1080": {
            ...configWithInstances["Radarr-1080"],
            ImportMode: "Copy",
            EntrySearch: {
              SearchMissing: true,
              DoUpgradeSearch: true,
            },
          },
        }),
      ),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();
    renderConfig();

    await screen.findByText("Radarr-1080");
    const arrHeader = screen.getByText("Radarr-1080");
    const card = arrHeader.closest(".card")!;
    await user.click(within(card).getByRole("button", { name: /configure/i }));

    expect(await screen.findByText("Import Mode")).toBeInTheDocument();
    const importModeField = screen.getByText("Import Mode").closest(".field")!;
    expect(within(importModeField).getByText("Copy")).toBeInTheDocument();

    const searchMissing = screen.getByLabelText(
      "Search Missing",
    ) as HTMLInputElement;
    expect(searchMissing.checked).toBe(true);
  });
});

describe("ConfigView – Arr Entry Search and Import Mode", () => {
  it("populates Import Mode and Entry Search from API JSON keys", async () => {
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          ...configWithInstances,
          "Radarr-1080": {
            ...configWithInstances["Radarr-1080"],
            ImportMode: "Copy",
            EntrySearch: {
              SearchMissing: false,
              DoUpgradeSearch: true,
            },
          },
        }),
      ),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
      http.post("/web/arr/test-connection", () =>
        HttpResponse.json({ success: true, message: "ok" }),
      ),
    );

    const user = userEvent.setup();
    renderConfig();

    await screen.findByText("Radarr-1080");
    const arrHeader = screen.getByText("Radarr-1080");
    const card = arrHeader.closest(".card")!;
    await user.click(within(card).getByRole("button", { name: /configure/i }));

    await screen.findByText("Import Mode");
    const importModeField = screen.getByText("Import Mode").closest(".field")!;
    expect(within(importModeField).getByText("Copy")).toBeInTheDocument();

    await screen.findByText("Search Missing");
    const searchMissing = screen.getByLabelText(/search missing/i);
    expect(searchMissing).not.toBeChecked();

    const upgradeSearch = screen.getByLabelText(/do upgrade search/i);
    expect(upgradeSearch).toBeChecked();
  });
});
