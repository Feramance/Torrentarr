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
import {
  fireEvent,
  render,
  screen,
  within,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { MaintenanceView } from "../../pages/MaintenanceView";
import { CONFIG_SCHEMA } from "../../config/configFields.generated";
import { NAVIGATE_TAB_EVENT } from "../../utils/navigateTab";
import { ConfigView } from "../../pages/ConfigView";
import { ToastProvider, ToastViewport } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";

const server = setupServer(
  http.get("/web/config/schema", () => HttpResponse.json(CONFIG_SCHEMA)),
  http.get("/web/torrent-clients", () =>
    HttpResponse.json(
      ["qBit", "seedbox"].map((id) => ({ id, connected: true })),
    ),
  ),
  http.get("/web/maintenance/status", () =>
    HttpResponse.json({ running: false, operations: [], armed: {} }),
  ),
  http.get("/web/maintenance/history", () => HttpResponse.json([])),
);

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

async function openSection(name: string): Promise<void> {
  await screen.findByRole("navigation", { name: "Configuration sections" });
  if (
    ["Settings", "Web Settings", "Authentication", "Integrations"].includes(
      name,
    )
  ) {
    await userEvent.click(
      screen.getByRole("button", { name: new RegExp(`^${name}(?: •)?$`) }),
    );
    return;
  }
  await userEvent.click(
    screen.getByRole("button", { name: "Torrent Clients", exact: true }),
  );
  let button = screen.queryByRole("button", {
    name: `Configure ${name}`,
    exact: true,
  });
  if (!button) {
    await userEvent.click(
      screen.getByRole("button", { name: "Integrations", exact: true }),
    );
    button = screen.getByRole("button", {
      name: `Configure ${name}`,
      exact: true,
    });
  }
  await userEvent.click(button);
}

it("saves the explicit choice to keep protection off for an unchanged monitor list", async () => {
  const config = {
    ...configQbitOnly,
    qBit: {
      ...configQbitOnly.qBit,
      FreeSpace: [],
      Migration: {
        Notices: [
          "no unambiguous local monitor path; free-space protection remains disabled until configured",
        ],
      },
    },
  };
  const save = vi.fn();
  server.use(
    http.get("/web/config", () => HttpResponse.json(config)),
    http.post("/web/config", async ({ request }) => {
      save(await request.json());
      config.qBit.Migration.Notices = [];
      return HttpResponse.json(okUpdateResponse);
    }),
  );
  renderConfig();
  await openSection("qBit");
  await userEvent.click(
    screen.getByRole("button", { name: "Keep free-space protection off" }),
  );
  await waitFor(() =>
    expect(save).toHaveBeenCalledWith({
      changes: { "qBit.FreeSpace": [] },
      keepFreeSpaceProtectionOff: "qBit.FreeSpace",
    }),
  );
  await waitFor(() =>
    expect(
      screen.queryByRole("button", { name: "Keep free-space protection off" }),
    ).not.toBeInTheDocument(),
  );
});

it("requires confirmation before changing the draft to disable authentication", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configQbitOnly,
        WebUI: { ...configQbitOnly.WebUI, AuthDisabled: false },
      }),
    ),
  );
  const user = userEvent.setup();
  renderConfig();
  await openSection("Authentication");
  const accessControls = screen
    .getByText("Access controls")
    .closest("details")!;
  await user.click(within(accessControls).getByText("Access controls"));
  const authDisabled = screen.getByLabelText("Auth Disabled");
  await user.click(authDisabled);
  const warning = screen.getByRole("dialog", {
    name: "Disable authentication?",
  });
  await user.click(within(warning).getByRole("button", { name: "Cancel" }));
  expect(accessControls).toHaveAttribute("open");
  expect(authDisabled).toHaveFocus();
  expect(authDisabled).not.toBeChecked();
  await user.click(authDisabled);
  await user.keyboard("{Escape}");
  expect(accessControls).toHaveAttribute("open");
  expect(authDisabled).toHaveFocus();
  expect(authDisabled).not.toBeChecked();
  await user.click(authDisabled);
  await user.click(
    screen.getByRole("button", { name: "Disable authentication" }),
  );
  expect(accessControls).toHaveAttribute("open");
  expect(authDisabled).toHaveFocus();
  expect(authDisabled).toBeChecked();
});

it("lists every client together by name with the same controls and shared settings", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configQbitOnly,
        TorrentClient: {
          "a-transmission": {
            Type: "transmission",
            Host: "transmission",
            Port: 9091,
            Disabled: true,
          },
          "z-qbit": { Type: "qbittorrent", Host: "seedbox", Port: 8080 },
        },
      }),
    ),
  );
  renderConfig();
  await openSection("a-transmission");
  expect(
    screen.getByRole("navigation", { name: "Configuration sections" }),
  ).toBeInTheDocument();
  expect(
    within(
      screen.getByRole("region", { name: "qBittorrent Instances" }),
    ).getByRole("button", { name: "Add Instance" }),
  ).toBeInTheDocument();
  for (const name of ["a-transmission", "qBit", "z-qbit"]) {
    await openSection(name);
    const editor = screen.getByRole("region", {
      name: /^(Configure |Settings|Web Settings|Authentication)/,
    });
    expect(
      within(editor).getByText("Managed Categories", { exact: true }),
    ).toBeInTheDocument();
    expect(
      within(editor).getByRole("button", { name: "Open Manage" }),
    ).toBeInTheDocument();
    expect(
      within(editor).getByRole("button", { name: "Delete", exact: true }),
    ).toBeInTheDocument();
  }
});

// ── delete button rendering ───────────────────────────────────────────────────

describe("ConfigView – delete button rendering", () => {
  it("renders a Delete button for the qBit instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    renderConfig();

    // Wait for config to load by checking for a known card header
    await openSection("qBit");
    expect(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /delete/i,
      }),
    ).toBeInTheDocument();
  });

  it("renders a Delete button for the qBit-1 instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    renderConfig();

    await openSection("qBit-1");
    expect(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /delete/i,
      }),
    ).toBeInTheDocument();
  });

  it("renders Delete buttons for Arr instances", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    renderConfig();

    await openSection("Radarr-1080");
    expect(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /delete/i,
      }),
    ).toBeInTheDocument();
  });
});

// ── deleteQbitInstance logic ──────────────────────────────────────────────────

describe("ConfigView – section deletion", () => {
  it("persists only the confirmed section deletion", async () => {
    let current = structuredClone(configWithInstances);
    let changes: Record<string, unknown> = {};
    server.use(
      http.get("/web/config", () => HttpResponse.json(current)),
      http.post("/web/config", async ({ request }) => {
        changes = (
          (await request.json()) as { changes: Record<string, unknown> }
        ).changes;
        current = Object.fromEntries(
          Object.entries(current).filter(([key]) => changes[key] !== null),
        ) as typeof current;
        return HttpResponse.json(okUpdateResponse);
      }),
    );
    const user = userEvent.setup();
    renderConfig();
    await openSection("qBit");
    await user.click(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /delete/i,
      }),
    );
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
        name: "Remove",
      }),
    );
    await waitFor(() =>
      expect(screen.queryByText("qBit")).not.toBeInTheDocument(),
    );
    expect(changes).toEqual({ qBit: null });
    expect(screen.getByText("qBit-1")).toBeInTheDocument();
    expect(screen.queryByText("qBit")).not.toBeInTheDocument();
  });
  it("keeps the section when its confirmation is cancelled", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
    );
    const user = userEvent.setup();
    renderConfig();
    await openSection("qBit-1");
    await user.click(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /delete/i,
      }),
    );
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
        name: "Cancel",
      }),
    );
    expect(screen.getAllByText("qBit-1").length).toBeGreaterThan(0);
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
      await openSection("qBit");
      expect(screen.queryByText("(Default)")).not.toBeInTheDocument();

      const dialog = screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      });
      for (const newName of names) {
        const name = within(dialog).getByPlaceholderText("qBit-seedbox");
        await user.clear(name);
        await user.type(name, `${newName}{Enter}`);
      }
      await user.click(
        within(
          screen.getByRole("region", {
            name: /^(Configure |Settings|Web Settings|Authentication)/,
          }),
        ).getByRole("button", {
          name: /^save$/i,
        }),
      );
      if (outcome === "retry") {
        await screen.findByText(/configuration is busy/i);
        await user.click(
          within(
            screen.getByRole("region", {
              name: /^(Configure |Settings|Web Settings|Authentication)/,
            }),
          ).getByRole("button", {
            name: /^save$/i,
          }),
        );
      }
      if (outcome === "back")
        await screen.findByText(/no changes detected|configuration saved/i);
      else await waitFor(() => expect(saves).toBeGreaterThan(0));
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
  it("clicking 'Add Instance' creates a client-1 card when qBit is the only instance", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();

    renderConfig();

    await openSection("qBit");
    expect(screen.queryByText("client-1")).not.toBeInTheDocument();

    // Scope the shared add button to the torrent client section; the
    // Integrations tab has its own top-level Add Instance button.
    const qbitSection = screen.getByRole("region", {
      name: "qBittorrent Instances",
    });
    const addBtn = within(qbitSection).getByRole("button", {
      name: /add instance/i,
    });
    await user.click(addBtn);

    // findAllByText: the card header AND the modal title both contain "client-1"
    await screen.findAllByText("client-1");
  });

  it("clicking 'Add Instance' twice creates client-1 and then client-2", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();

    renderConfig();

    await openSection("qBit");

    // Click once → creates client-1 and opens its configure modal
    const getAddBtn = () =>
      within(
        screen.getByRole("region", { name: "qBittorrent Instances" }),
      ).getByRole("button", { name: /add instance/i });

    await user.click(getAddBtn());
    // Card header + modal title both show "client-1"
    await screen.findAllByText("client-1");

    // Click again → creates client-2 (re-query button since DOM updated)
    await user.click(getAddBtn());
    await screen.findAllByText("client-2");
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

    renderConfig();

    await openSection("Web Settings");

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

    renderConfig();

    await openSection("Web Settings");
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

    await openSection("Web Settings");
    await user.click(screen.getByRole("button", { name: "Save", exact: true }));

    expect(
      (await screen.findAllByText(/UrlBase must start with \//i)).length,
    ).toBeGreaterThan(0);
  });

  it("stores valid UrlBase in form state for save", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configQbitOnly)),
      http.post("/web/config", () => HttpResponse.json(okUpdateResponse)),
    );

    const user = userEvent.setup();
    renderConfig();

    await openSection("Web Settings");
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

    renderConfig();

    await openSection("Radarr-1080");

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

    renderConfig();

    await openSection("Radarr-1080");

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

describe("ConfigView – section persistence", () => {
  it("saves only the selected section and retains unrelated drafts", async () => {
    const seeding = {
      MaxUploadRatio: 0,
      DownloadRateLimitPerTorrent: -1,
      UploadRateLimitPerTorrent: -1,
      HitAndRunMode: "disabled",
    };
    let stored = {
      ...structuredClone(configWithInstances),
      qBit: {
        ...configWithInstances.qBit,
        Password: "[redacted]",
        CategorySeeding: seeding,
      },
    };
    let saved: { changes: Record<string, unknown> } | null = null;
    server.use(
      http.get("/web/config", () => HttpResponse.json(stored)),
      http.post("/web/config", async ({ request }) => {
        saved = (await request.json()) as { changes: Record<string, unknown> };
        stored = {
          ...stored,
          qBit: { ...stored.qBit, Host: String(saved.changes["qBit.Host"]) },
        };
        return HttpResponse.json(okUpdateResponse);
      }),
    );
    const user = userEvent.setup();
    renderConfig();
    await openSection("Web Settings");
    await user.type(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("textbox", {
        name: "Url Base",
      }),
      "/draft",
    );

    await openSection("qBit");
    const host = within(
      screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).getByRole("textbox", {
      name: "Host",
    });
    await user.clear(host);
    await user.type(host, "seedbox.local");
    await user.click(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /^Save$/,
      }),
    );
    await screen.findByText("Configuration saved");
    expect(saved!.changes).toEqual({ "qBit.Host": "seedbox.local" });
    await openSection("Web Settings");
    expect(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("textbox", {
        name: "Url Base",
      }),
    ).toHaveValue("/draft");
  });

  it("retains edited values and the dialog when persistence fails", async () => {
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          ...configQbitOnly,
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
      http.post("/web/config", () =>
        HttpResponse.json({ error: "disk unavailable" }, { status: 500 }),
      ),
    );
    const user = userEvent.setup();
    renderConfig();
    await openSection("qBit");
    const host = within(
      screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).getByRole("textbox", {
      name: "Host",
    });
    await user.clear(host);
    await user.type(host, "keep-this.local");
    await user.click(
      within(
        screen.getByRole("region", {
          name: /^(Configure |Settings|Web Settings|Authentication)/,
        }),
      ).getByRole("button", {
        name: /^Save$/,
      }),
    );
    await screen.findByText("disk unavailable");
    expect(host).toHaveValue("keep-this.local");
    expect(
      screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Configuration saved/)).not.toBeInTheDocument();
  });
});

it.each(["Web Settings", "Radarr-1080"])(
  "releases the %s modal and scroll lock while Config is inactive",
  async (section) => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(configWithInstances)),
    );
    const { rerender } = renderConfig();
    await openSection(section);
    expect(
      screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).toBeInTheDocument();
    expect(document.body.style.overflow).toBe("");
    rerender(<ConfigView active={false} />);
    expect(
      screen.queryByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).not.toBeInTheDocument();
    expect(document.body.style.overflow).toBe("");
    rerender(<ConfigView active />);
    expect(
      screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).toBeInTheDocument();
    expect(document.body.style.overflow).toBe("");
    fireEvent.keyDown(window, { key: "Escape" });
    expect(
      screen.getByRole("region", {
        name: /^(Configure |Settings|Web Settings|Authentication)/,
      }),
    ).toBeInTheDocument();
    expect(document.body.style.overflow).toBe("");
  },
);

it("uses the shared client dialog and saves only the selected torrent client", async () => {
  let changes: Record<string, unknown> = {};
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configQbitOnly,
        TorrentClient: {
          seedbox: {
            Type: "transmission",
            Host: "old-host",
            Port: 9091,
            Password: "[redacted]",
            Maintenance: {
              PathMappings: [
                { ClientPath: "/old", LocalPath: "/new", FutureProperty: true },
              ],
            },
          },
        },
      }),
    ),
    http.post("/web/config", async ({ request }) => {
      changes = ((await request.json()) as { changes: Record<string, unknown> })
        .changes;
      return HttpResponse.json(okUpdateResponse);
    }),
  );
  const user = userEvent.setup();
  renderConfig();
  await openSection("seedbox");
  expect(
    screen.getByRole("textbox", { name: "Client Type", exact: true }),
  ).toHaveValue("transmission");
  expect(
    screen.getByText("Path mappings", { selector: "summary" }),
  ).toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Open Manage" })).toBeEnabled();
  const host = screen.getByRole("textbox", { name: "Host", exact: true });
  await user.clear(host);
  await user.type(host, "new-host");
  await user.click(screen.getByRole("button", { name: "Save", exact: true }));
  await waitFor(() =>
    expect(changes).toEqual({ "TorrentClient.seedbox.Host": "new-host" }),
  );
});

it.each([false, true])(
  "saves mapping add/edit/remove/clear and retains a failed draft (canonical=%s)",
  async (canonical) => {
    const initial = [
      { ClientPath: "/downloads", LocalPath: "/data", FutureProperty: true },
    ];
    let mappings = initial;
    let fail = false;
    let lastChanges: Record<string, unknown> = {};
    const prefix = canonical ? "TorrentClient.seedbox" : "qBit";
    const application = {
      acceptedRevision: 20,
      state: "pending",
      components: { processing: { appliedRevision: 19 } },
    };
    const fixture = () => ({
      ...configQbitOnly,
      [canonical ? "TorrentClient" : "qBit"]: canonical
        ? {
            seedbox: {
              Type: "transmission",
              Host: "seedbox",
              Port: 9091,
              Disabled: true,
              Maintenance: { PathMappings: mappings },
            },
          }
        : {
            ...configQbitOnly.qBit,
            CategorySeeding: {
              MaxUploadRatio: 0,
              DownloadRateLimitPerTorrent: -1,
              UploadRateLimitPerTorrent: -1,
              HitAndRunMode: "disabled",
            },
            Maintenance: { PathMappings: mappings },
          },
    });
    server.use(
      http.get("/web/config", () => HttpResponse.json(fixture())),
      http.post("/web/config", async ({ request }) => {
        lastChanges = (
          (await request.json()) as { changes: Record<string, unknown> }
        ).changes;
        if (fail)
          return HttpResponse.json(
            { error: "Configuration busy; retry" },
            { status: 409 },
          );
        mappings = lastChanges[
          `${prefix}.Maintenance.PathMappings`
        ] as typeof initial;
        return HttpResponse.json({
          ...okUpdateResponse,
          reloadType: "live",
          application,
        });
      }),
    );
    const user = userEvent.setup();
    render(
      <MaintenanceView requestedClientId={canonical ? "seedbox" : "qBit"} />,
      { wrapper: Wrapper },
    );
    const open = async () => {
      const shared = await screen.findByText(/Shared settings ·/);
      if (!shared.closest("details")!.open) await user.click(shared);
      const mappings = screen.getByText("Path mappings", {
        selector: "summary",
      });
      if (!mappings.closest("details")!.open) await user.click(mappings);
    };
    const save = async () => {
      await user.click(screen.getByRole("button", { name: "Save settings" }));
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "Save settings" }),
        ).toBeDisabled(),
      );
    };
    await open();
    fireEvent.change(
      screen.getByRole("textbox", { name: "Torrentarr path", exact: true }),
      { target: { value: "/edited" } },
    );
    fail = true;
    await user.click(
      screen.getByRole("button", { name: "Save settings", exact: true }),
    );
    await screen.findByText("Configuration busy; retry");
    expect(
      screen.getByRole("textbox", { name: "Torrentarr path", exact: true }),
    ).toHaveValue("/edited");
    expect(mappings).toEqual(initial);
    fail = false;
    await user.click(screen.getByRole("button", { name: "Add path mapping" }));
    fireEvent.change(
      screen.getAllByRole("textbox", {
        name: "Torrent client path",
        exact: true,
      })[1],
      { target: { value: "D:\\Downloads\\TV" } },
    );
    fireEvent.change(
      screen.getAllByRole("textbox", {
        name: "Torrentarr path",
        exact: true,
      })[1],
      { target: { value: "/tv files" } },
    );
    await save();
    expect(lastChanges).toEqual({
      [`${prefix}.Maintenance.PathMappings`]: [
        {
          ClientPath: "/downloads",
          LocalPath: "/edited",
          FutureProperty: true,
        },
        { ClientPath: "D:\\Downloads\\TV", LocalPath: "/tv files" },
      ],
    });
    expect(
      screen.getByText(
        "Configuration saved; waiting for active operations to finish",
      ),
    ).toBeInTheDocument();
    await open();
    expect(
      screen.getAllByRole("textbox", {
        name: "Torrentarr path",
        exact: true,
      })[0],
    ).toHaveValue("/edited");
    await user.click(
      screen.getAllByRole("button", { name: "Remove path mapping" })[1],
    );
    await save();
    expect(mappings).toHaveLength(1);
    await open();
    await user.click(
      screen.getByRole("button", { name: "Remove path mapping" }),
    );
    await save();
    expect(mappings).toEqual([]);
    await open();
    expect(
      screen.queryByRole("textbox", {
        name: "Torrent client path",
        exact: true,
      }),
    ).not.toBeInTheDocument();
    window.dispatchEvent(
      new CustomEvent("torrentarr:configuration", {
        detail: { ...application, state: "applied" },
      }),
    );
    await waitFor(() =>
      expect(
        screen.queryByText(
          "Configuration saved; waiting for active operations to finish",
        ),
      ).not.toBeInTheDocument(),
    );
    expect(
      screen.queryByText(/Configuration applied live/i),
    ).not.toBeInTheDocument();
    window.dispatchEvent(
      new CustomEvent("torrentarr:configuration", {
        detail: { ...application, acceptedRevision: 19 },
      }),
    );
    expect(
      screen.queryByText(
        "Configuration saved; waiting for active operations to finish",
      ),
    ).not.toBeInTheDocument();
  },
);

it.each([false, true])(
  "round-trips storage settings without editing credentials (canonical=%s)",
  async (canonical) => {
    const prefix = canonical ? "TorrentClient.seedbox" : "qBit";
    let saved: Record<string, unknown> = {};
    const storage = {
      Enabled: false,
      FutureSetting: 42,
      Orphans: { Enabled: false, MinimumAgeMinutes: 60 },
    };
    const client = {
      Type: "qbittorrent",
      Disabled: true,
      Host: "localhost",
      Port: 8080,
      Password: "[redacted]",
      Maintenance: storage,
    };
    const fixture = {
      ...configQbitOnly,
      ...(canonical
        ? { TorrentClient: { seedbox: client } }
        : { qBit: client }),
    };
    server.use(
      http.get("/web/config", () => HttpResponse.json(fixture)),
      http.post("/web/config", async ({ request }) => {
        saved = ((await request.json()) as { changes: Record<string, unknown> })
          .changes;
        return HttpResponse.json(okUpdateResponse);
      }),
    );
    const user = userEvent.setup();
    render(
      <MaintenanceView requestedClientId={canonical ? "seedbox" : "qBit"} />,
      { wrapper: Wrapper },
    );
    await screen.findByText(/Shared settings ·/);
    await user.click(
      screen.getByText("Orphan-file cleanup", { selector: "strong" }),
    );
    fireEvent.change(
      screen.getByLabelText("Minimum orphan file age (minutes)"),
      { target: { value: "120" } },
    );
    await user.click(
      screen.getByRole("button", { name: "Save settings", exact: true }),
    );
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Save settings" }),
      ).toBeDisabled(),
    );
    expect(saved).toMatchObject({
      [`${prefix}.Maintenance.Orphans.MinimumAgeMinutes`]: 120,
    });
    expect(Object.keys(saved)).not.toContain(`${prefix}.Password`);
    expect(storage.FutureSetting).toBe(42);
  },
);

it("opens Manage for the saved client without losing its pending connection edits", async () => {
  server.use(http.get("/web/config", () => HttpResponse.json(configQbitOnly)));
  const navigate = vi.fn();
  window.addEventListener(NAVIGATE_TAB_EVENT, navigate);
  try {
    const user = userEvent.setup();
    renderConfig();
    await openSection("qBit");
    fireEvent.change(
      screen.getByRole("textbox", { name: "Host", exact: true }),
      { target: { value: "pending.local" } },
    );
    await user.click(screen.getByRole("button", { name: "Open Manage" }));
    expect(navigate.mock.calls[0][0].detail).toEqual({
      tab: "maintenance",
      manageClientId: "qBit",
      preserveConfigDraft: true,
    });
    expect(
      screen.getByRole("textbox", { name: "Host", exact: true }),
    ).toHaveValue("pending.local");
  } finally {
    window.removeEventListener(NAVIGATE_TAB_EVENT, navigate);
  }
});

it("filters inline setting fields without locking page scrolling or losing drafts", async () => {
  server.use(http.get("/web/config", () => HttpResponse.json(configQbitOnly)));
  const user = userEvent.setup();
  renderConfig();
  await user.click(await screen.findByRole("button", { name: "Web Settings" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(document.body.style.overflow).toBe("");
  const search = screen.getByRole("searchbox", {
    name: "Find a setting in this section",
  });
  await user.type(search, "Url Base");
  const url = screen.getByRole("textbox", { name: "Url Base" });
  await user.type(url, "/draft");
  await openSection("qBit");
  await user.click(screen.getByRole("button", { name: /Web Settings/ }));
  expect(screen.getByRole("textbox", { name: "Url Base" })).toHaveValue(
    "/draft",
  );
});

it("shows grouped Arr application summaries and opens the selected inline editor", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configWithInstances,
        "Radarr-1080": {
          ...configWithInstances["Radarr-1080"],
          URI: "http://user:secret@radarr:7878/base?apikey=hidden",
        },
        "Sonarr-TV": {
          Managed: false,
          URI: "http://sonarr:8989",
          Category: "tv",
          SearchOnly: true,
        },
      }),
    ),
  );
  renderConfig();
  await screen.findByRole("navigation", { name: "Configuration sections" });
  await userEvent.click(
    screen.getByRole("button", { name: "Integrations", exact: true }),
  );
  expect(screen.queryByLabelText("Library instance")).not.toBeInTheDocument();
  const radarr = screen.getByRole("button", {
    name: "Configure Radarr-1080",
    exact: true,
  });
  expect(radarr).toHaveTextContent("radarr:7878/base");
  expect(radarr).toHaveTextContent("Search + torrents");
  expect(radarr).not.toHaveTextContent("secret");
  expect(radarr).not.toHaveTextContent("hidden");
  expect(radarr).toHaveAttribute("aria-pressed", "false");
  expect(
    screen.queryByRole("region", { name: /^Configure / }),
  ).not.toBeInTheDocument();
  const sonarr = screen.getByRole("button", {
    name: "Configure Sonarr-TV",
    exact: true,
  });
  expect(sonarr).toHaveTextContent("tv");
  expect(sonarr).toHaveTextContent("Search only");
  expect(sonarr).toHaveTextContent("Disabled");
  await userEvent.click(sonarr);
  expect(sonarr).toHaveAttribute("aria-pressed", "true");
  expect(
    screen.getByRole("heading", { name: /Sonarr-TV/ }),
  ).toBeInTheDocument();
});

it("shows empty type sections and creates integrations from the top-level picker while retaining drafts", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({ Settings: {}, WebUI: {} }),
    ),
  );
  renderConfig();
  await openSection("Integrations");
  expect(screen.queryByLabelText("Integration type")).not.toBeInTheDocument();
  for (const type of ["Radarr", "Sonarr", "Lidarr", "Readarr"]) {
    const section = screen.getByRole("region", { name: `${type} Instances` });
    expect(within(section).getByText("0 instances")).toBeInTheDocument();
    expect(
      within(section).queryByRole("button", { name: "Add Instance" }),
    ).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Add Instance" }));
    const picker = screen.getByRole("dialog", { name: "Add integration" });
    await userEvent.click(
      within(picker).getByRole("button", { name: type, exact: true }),
    );
    expect(
      screen.getByRole("region", { name: `Configure ${type}-1` }),
    ).toBeInTheDocument();
  }
  await userEvent.click(screen.getByRole("button", { name: "Add Instance" }));
  await userEvent.click(
    within(screen.getByRole("dialog", { name: "Add integration" })).getByRole(
      "button",
      { name: "Radarr", exact: true },
    ),
  );
  expect(
    screen.getByRole("button", { name: "Configure Radarr-1" }),
  ).toHaveTextContent("Unsaved");
  expect(
    screen.getByRole("region", { name: "Configure Radarr-2" }),
  ).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("button", { name: "Torrent Clients", exact: true }),
  );
  const group = screen.getByRole("region", { name: "qBittorrent Instances" });
  expect(within(group).getByText("0 instances")).toBeInTheDocument();
  expect(screen.queryByLabelText("Torrent client")).not.toBeInTheDocument();
});

it("groups mixed client storage by normalized type without conflating identities", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configQbitOnly,
        TorrentClient: {
          qBit: {
            Type: "QBITTORRENT",
            Host: "canonical",
            Port: 8080,
            ManagedCategories: ["movies"],
            TorrentScanIntervalSeconds: 60,
          },
          custom: { Type: "Custom", Host: "custom", Port: 1234 },
          transmission: { Type: "transmission", Host: "other", Port: 9091 },
        },
      }),
    ),
  );
  renderConfig();
  await userEvent.click(
    await screen.findByRole("button", { name: "Torrent Clients", exact: true }),
  );
  const group = screen.getByRole("region", { name: "qBittorrent Instances" });
  expect(within(group).getByText("2 instances")).toBeInTheDocument();
  expect(within(group).getByText("movies")).toBeInTheDocument();
  expect(within(group).getByText("60 s")).toBeInTheDocument();
  await userEvent.click(
    within(group).getByRole("button", { name: "Configure qBit (legacy)" }),
  );
  expect(
    screen.getByRole("textbox", { name: "Host", exact: true }),
  ).toHaveValue("localhost");
  await userEvent.click(
    within(group).getByRole("button", {
      name: "Configure qBit (TorrentClient)",
    }),
  );
  expect(
    screen.getByRole("textbox", { name: "Host", exact: true }),
  ).toHaveValue("canonical");
  await userEvent.click(
    within(screen.getByRole("region", { name: "custom Instances" })).getByRole(
      "button",
      { name: "Add Instance" },
    ),
  );
  expect(screen.getByRole("textbox", { name: "Client Type" })).toHaveValue(
    "custom",
  );
  fireEvent.change(screen.getByRole("textbox", { name: "Client Type" }), {
    target: { value: "transmission" },
  });
  expect(
    within(
      screen.getByRole("region", { name: "transmission Instances" }),
    ).getByText("2 instances"),
  ).toBeInTheDocument();
});

it.each([
  "ordinary",
  "accept",
  "defer",
  "retry",
  "empty target",
  "failed save",
])(
  "handles %s save requirements independently of legacy reload labels",
  async (scenario) => {
    const requirements = [
      { component: "Radarr search", category: "radarr", kind: "search" },
      { component: "Sonarr torrents", category: "sonarr", kind: "torrent" },
    ];
    const current = {
      ...structuredClone(configQbitOnly),
      WebUI: { ...configQbitOnly.WebUI, Host: "127.0.0.1", UrlBase: "" },
    };
    const restarts: string[] = [];
    let retry = scenario === "retry" || scenario === "empty target";
    let saves = 0;
    server.use(
      http.get("/web/config", () => HttpResponse.json(current)),
      http.post("/web/config", async ({ request }) => {
        saves++;
        const { changes } = (await request.json()) as {
          changes: Record<string, unknown>;
        };
        if (scenario === "failed save")
          return HttpResponse.json({ error: "save failed" }, { status: 500 });
        current.WebUI.UrlBase = String(changes["WebUI.UrlBase"]);
        return HttpResponse.json({
          ...okUpdateResponse,
          reloadType: "full",
          configReloaded: true,
          restartRequirements: scenario === "ordinary" ? [] : requirements,
        });
      }),
      http.post("/web/processes/:category/:kind/restart", ({ params }) => {
        expect(saves).toBe(1);
        restarts.push(`${params.category}/${params.kind}`);
        if (
          retry &&
          params.category === "radarr" &&
          scenario === "empty target"
        )
          return HttpResponse.json({ status: "restarted", restarted: [] });
        if (retry && params.category === "radarr")
          return HttpResponse.json(
            { error: "restart failed" },
            { status: 500 },
          );
        return HttpResponse.json({ status: "restarted" });
      }),
    );
    renderConfig();
    await openSection("Web Settings");
    expect(
      screen.queryByText(/Applies live|No restart required/),
    ).not.toBeInTheDocument();
    const save = screen.getByRole("button", { name: "Save", exact: true });
    expect(
      screen.queryByRole("dialog", { name: "Restart required" }),
    ).not.toBeInTheDocument();
    if (scenario === "ordinary") {
      await userEvent.click(save);
      await screen.findByText("No changes detected for WebUI");
      expect(saves).toBe(0);
    }
    fireEvent.change(screen.getByRole("textbox", { name: "Url Base" }), {
      target: { value: "/new" },
    });
    await userEvent.click(save);
    if (scenario === "failed save") {
      await screen.findByText("save failed");
      expect(
        screen.queryByRole("dialog", { name: "Restart required" }),
      ).not.toBeInTheDocument();
    } else if (scenario === "ordinary") {
      await screen.findByText("Configuration saved");
      expect(
        screen.queryByRole("dialog", { name: "Restart required" }),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByText(/reloaded|Applied live|without restart/i),
      ).not.toBeInTheDocument();
    } else {
      const dialog = await screen.findByRole("dialog", {
        name: "Restart required",
      });
      expect(dialog).toHaveTextContent("Radarr search, Sonarr torrents");
      expect(
        within(dialog).getByRole("button", { name: "Close" }),
      ).toHaveFocus();
      await userEvent.tab({ shift: true });
      expect(
        within(dialog).getByRole("button", { name: "Restart now" }),
      ).toHaveFocus();
      if (scenario === "defer") {
        await userEvent.click(
          within(dialog).getByRole("button", { name: "Later" }),
        );
        expect(screen.getByText(/Pending restart:/)).toHaveTextContent(
          "Radarr search, Sonarr torrents",
        );
        expect(current.WebUI.UrlBase).toBe("/new");
      } else {
        await userEvent.click(
          within(dialog).getByRole("button", { name: "Restart now" }),
        );
        await waitFor(() => expect(restarts).toHaveLength(2));
        if (scenario === "retry" || scenario === "empty target") {
          await screen.findByText(
            scenario === "retry"
              ? "restart failed"
              : "Radarr search did not restart",
          );
          expect(screen.getByText(/Pending restart:/)).toHaveTextContent(
            "Radarr search",
          );
          expect(screen.getByText(/Pending restart:/)).not.toHaveTextContent(
            "Sonarr torrents",
          );
          retry = false;
          await userEvent.click(
            await screen.findByRole("button", { name: "Retry restart" }),
          );
          await userEvent.click(
            screen.getByRole("button", { name: "Restart now" }),
          );
          await waitFor(() => expect(restarts).toHaveLength(3));
        }
        await waitFor(() =>
          expect(
            screen.queryByText(/Pending restart:/),
          ).not.toBeInTheDocument(),
        );
      }
    }
    if (["ordinary", "defer", "failed save"].includes(scenario))
      expect(restarts).toEqual([]);
  },
);

it.each([
  {
    tab: "Integrations",
    name: "Radarr-1080",
    field: "URI",
    draft: "http://draft:7878",
  },
  { tab: "Torrent Clients", name: "qBit", field: "Host", draft: "draft.local" },
  {
    tab: "Torrent Clients",
    name: "seedbox",
    field: "Host",
    draft: "draft.local",
  },
])(
  "opens $name only on selection and retains its draft when returning to the overview",
  async ({ tab, name, field, draft }) => {
    server.use(
      http.get("/web/config", () =>
        HttpResponse.json({
          ...configWithInstances,
          TorrentClient: {
            seedbox: {
              Type: "qbittorrent",
              Host: "canonical.local",
              Port: 8080,
            },
          },
        }),
      ),
    );
    renderConfig();
    const tabs = await screen.findByRole("navigation", {
      name: "Configuration sections",
    });
    const openTab = () =>
      userEvent.click(
        within(tabs).getByRole("button", { name: tab, exact: true }),
      );
    await openTab();
    expect(
      screen.queryByRole("region", { name: /^Configure / }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole("searchbox")).not.toBeInTheDocument();
    const row = () =>
      screen.getByRole("button", { name: `Configure ${name}`, exact: true });
    await userEvent.click(row());
    expect(
      screen.getByRole("region", { name: `Configure ${name}`, exact: true }),
    ).toBeInTheDocument();
    expect(
      screen
        .getByRole("region", { name: `Configure ${name}`, exact: true })
        .closest(".config-inline-backdrop"),
    ).toBeInTheDocument();
    expect(document.querySelector(".modal-backdrop")).not.toBeInTheDocument();
    const actions = document.querySelector(".config-inline-actions")!;
    expect(actions).toContainElement(
      screen.getByRole("button", { name: "Save", exact: true }),
    );
    const note = document.querySelector(".modal-footer-note");
    if (note) expect(actions).toContainElement(note);
    const input = screen.getByRole("textbox", { name: field, exact: true });
    fireEvent.change(input, { target: { value: draft } });
    expect(screen.getByRole("searchbox")).toBeInTheDocument();
    await userEvent.click(
      within(tabs).getByRole("button", { name: "Settings", exact: true }),
    );
    await openTab();
    expect(
      screen.queryByRole("region", { name: /^Configure / }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole("searchbox")).not.toBeInTheDocument();
    expect(row()).toHaveTextContent("Unsaved");
    await userEvent.click(row());
    expect(
      screen.getByRole("textbox", { name: field, exact: true }),
    ).toHaveValue(draft);
  },
);

it("focuses and scrolls after the requested editor mounts, without jumping on ordinary updates", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configWithInstances,
        TorrentClient: {
          seedbox: { Type: "qbittorrent", Host: "canonical.local", Port: 8080 },
        },
      }),
    ),
  );
  const previous = HTMLElement.prototype.scrollIntoView;
  const header = document.createElement("header");
  header.className = "appbar";
  vi.spyOn(header, "getBoundingClientRect").mockReturnValue({
    height: 72,
  } as DOMRect);
  document.body.append(header);
  const titles: string[] = [];
  const scroll = vi.fn(function (this: HTMLElement) {
    titles.push(this.querySelector("h2")?.textContent ?? "");
    expect(this).toHaveFocus();
    expect(this.style.scrollMarginTop).toBe("84px");
  });
  HTMLElement.prototype.scrollIntoView = scroll;
  try {
    renderConfig();
    const tabs = await screen.findByRole("navigation", {
      name: "Configuration sections",
    });
    expect(scroll).not.toHaveBeenCalled();
    for (const name of ["qBit", "seedbox", "Radarr-1080"]) {
      await openSection(name);
      expect(titles.at(-1)).toBe(`Configure ${name}`);
      const calls = scroll.mock.calls.length;
      const field = screen.getByRole("textbox", {
        name: name.startsWith("Radarr") ? "URI" : "Host",
        exact: true,
      });
      fireEvent.change(field, { target: { value: "" } });
      expect(scroll).toHaveBeenCalledTimes(calls);
      await userEvent.type(screen.getByRole("searchbox"), "Connection");
      expect(scroll).toHaveBeenCalledTimes(calls);
      fireEvent.change(screen.getByRole("searchbox"), {
        target: { value: "" },
      });
      const summary = screen.getByText(
        name.startsWith("Radarr") ? "Processing" : "Torrent scope",
        { selector: "summary" },
      );
      await userEvent.click(summary);
      fireEvent(
        window,
        new CustomEvent("torrentarr:configuration", {
          detail: { acceptedRevision: 42, state: "applied", components: {} },
        }),
      );
      expect(scroll).toHaveBeenCalledTimes(calls);
      // Selecting the same row is still an explicit navigation request.
      await userEvent.click(
        screen.getByRole("button", { name: `Configure ${name}`, exact: true }),
      );
      expect(scroll).toHaveBeenCalledTimes(calls + 1);
    }
    await userEvent.click(
      within(tabs).getByRole("button", {
        name: "Torrent Clients",
        exact: true,
      }),
    );
    const calls = scroll.mock.calls.length;
    expect(screen.queryByRole("searchbox")).not.toBeInTheDocument();
    await userEvent.click(
      within(
        screen.getByRole("region", { name: "qBittorrent Instances" }),
      ).getByRole("button", { name: "Add Instance" }),
    );
    expect(titles.at(-1)).toBe("Configure client-1");
    expect(scroll).toHaveBeenCalledTimes(calls + 1);
    await userEvent.click(
      within(tabs).getByRole("button", { name: "Integrations", exact: true }),
    );
    await userEvent.click(screen.getByRole("button", { name: "Add Instance" }));
    await userEvent.click(
      within(screen.getByRole("dialog", { name: "Add integration" })).getByRole(
        "button",
        { name: "Readarr", exact: true },
      ),
    );
    expect(titles.at(-1)).toBe("Configure Readarr-1");
    expect(scroll).toHaveBeenCalledTimes(calls + 2);
    expect(scroll).toHaveBeenLastCalledWith({
      block: "start",
      behavior: "instant",
    });
  } finally {
    HTMLElement.prototype.scrollIntoView = previous;
    header.remove();
  }
}, 15_000);

it("focuses the affected free-space monitor and preserves edits on fallback", async () => {
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configQbitOnly,
        qBit: {
          ...configQbitOnly.qBit,
          FreeSpace: [
            { Path: "/first", Limit: "10G", Enabled: true },
            { Path: "/second", Limit: "20G", Enabled: true },
          ],
        },
      }),
    ),
  );
  const { rerender } = renderConfig();
  await openSection("qBit");
  fireEvent.change(screen.getByRole("textbox", { name: "Host", exact: true }), {
    target: { value: "draft-host" },
  });

  rerender(
    <ConfigView
      destination={{
        client: "qBit",
        field: "FreeSpace",
        monitorPath: "/second",
      }}
    />,
  );
  const second = await screen.findByRole("group", {
    name: "Free-space monitor 2",
  });
  await waitFor(() =>
    expect(second.contains(document.activeElement)).toBe(true),
  );

  rerender(
    <ConfigView
      destination={{
        client: "qBit",
        field: "FreeSpace",
        monitorPath: "/removed",
      }}
    />,
  );
  await waitFor(() =>
    expect(
      screen.getByText("Free-space monitors", { selector: "summary" }),
    ).toHaveFocus(),
  );
  expect(
    screen.getByRole("textbox", { name: "Host", exact: true }),
  ).toHaveValue("draft-host");
});
