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
    await userEvent.click(
      within(card).getByRole("button", { name: /configure/i }),
    );
    expect(
      within(screen.getByRole("dialog")).getByRole("button", {
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

    await screen.findByText("qBit-1");
    const qbit1Header = screen.getByText("qBit-1");
    const card = qbit1Header.closest(".card")!;
    await userEvent.click(
      within(card).getByRole("button", { name: /configure/i }),
    );
    expect(
      within(screen.getByRole("dialog")).getByRole("button", {
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

    await screen.findByText("Radarr-1080");
    const arrHeader = screen.getByText("Radarr-1080");
    const card = arrHeader.closest(".card")!;
    await userEvent.click(
      within(card).getByRole("button", { name: /configure/i }),
    );
    expect(
      within(screen.getByRole("dialog")).getByRole("button", {
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
    const card = (await screen.findByText("qBit")).closest(".card")!;
    await user.click(within(card).getByRole("button", { name: /configure/i }));
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
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
    const card = (await screen.findByText("qBit-1")).closest(".card")!;
    await user.click(within(card).getByRole("button", { name: /configure/i }));
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
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
          name: /^save$/i,
        }),
      );
      if (outcome === "retry") {
        await screen.findByText(/configuration is busy/i);
        await user.click(
          within(screen.getByRole("dialog")).getByRole("button", {
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

    const card = (await screen.findByText("Web Settings")).closest(".card")!;
    await user.click(within(card).getByRole("button", { name: /configure/i }));
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
    const web = (await screen.findByText("Web Settings")).closest(".card")!;
    await user.click(within(web).getByRole("button", { name: /configure/i }));
    await user.type(
      within(screen.getByRole("dialog")).getByRole("textbox", {
        name: "Url Base",
      }),
      "/draft",
    );
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", { name: "Close" }),
    );
    const qbit = screen.getByText("qBit").closest(".card")!;
    await user.click(within(qbit).getByRole("button", { name: /configure/i }));
    const host = within(screen.getByRole("dialog")).getByRole("textbox", {
      name: "Host",
    });
    await user.clear(host);
    await user.type(host, "seedbox.local");
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
        name: /^Save$/,
      }),
    );
    await screen.findByText("Configuration saved");
    expect(saved!.changes).toEqual({ "qBit.Host": "seedbox.local" });
    await user.click(within(web).getByRole("button", { name: /configure/i }));
    expect(
      within(screen.getByRole("dialog")).getByRole("textbox", {
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
    const card = (await screen.findByText("qBit")).closest(".card")!;
    await user.click(within(card).getByRole("button", { name: /configure/i }));
    const host = within(screen.getByRole("dialog")).getByRole("textbox", {
      name: "Host",
    });
    await user.clear(host);
    await user.type(host, "keep-this.local");
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", {
        name: /^Save$/,
      }),
    );
    await screen.findByText("disk unavailable");
    expect(host).toHaveValue("keep-this.local");
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(
      screen.queryByText(/Configuration saved successfully/),
    ).not.toBeInTheDocument();
  });
});

it("releases its modal and scroll lock while an inactive config panel is retained", async () => {
  server.use(http.get("/web/config", () => HttpResponse.json(configQbitOnly)));
  const user = userEvent.setup();
  const { rerender } = renderConfig();
  const card = (await screen.findByText("Web Settings")).closest(".card")!;
  await user.click(within(card).getByRole("button", { name: /configure/i }));
  expect(screen.getByRole("dialog")).toBeInTheDocument();
  expect(document.body.style.overflow).toBe("hidden");
  rerender(<ConfigView active={false} />);
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(document.body.style.overflow).toBe("");
});

it("uses the shared client dialog and saves only the selected additional client", async () => {
  let changes: Record<string, unknown> = {};
  server.use(
    http.get("/web/config", () =>
      HttpResponse.json({
        ...configQbitOnly,
        TorrentClients: {
          seedbox: {
            Type: "transmission",
            Host: "old-host",
            Port: 9091,
            Password: "[redacted]",
            Maintenance: { PathMappings: [{ From: "/old", To: "/new" }] },
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
  const card = (await screen.findByText("seedbox")).closest(".card")!;
  await user.click(within(card).getByRole("button", { name: "Configure" }));
  expect(
    screen.getByRole("textbox", { name: "Client Type", exact: true }),
  ).toHaveValue("transmission");
  expect(
    screen.getByRole("textbox", { name: "Path Mappings", exact: true }),
  ).toHaveValue(JSON.stringify([{ From: "/old", To: "/new" }], null, 2));
  const mappings = screen.getByRole("textbox", {
    name: "Path Mappings",
    exact: true,
  });
  await user.clear(mappings);
  await user.type(mappings, "invalid JSON");
  await user.click(screen.getByRole("button", { name: "Save", exact: true }));
  expect(changes).toEqual({});
  expect(screen.getByRole("dialog")).toBeInTheDocument();
  fireEvent.change(mappings, {
    target: { value: JSON.stringify([{ From: "/old", To: "/new" }]) },
  });
  const host = screen.getByRole("textbox", { name: "Host", exact: true });
  await user.clear(host);
  await user.type(host, "new-host");
  await user.click(screen.getByRole("button", { name: "Save", exact: true }));
  await waitFor(() =>
    expect(changes).toEqual({ "TorrentClients.seedbox.Host": "new-host" }),
  );
});
