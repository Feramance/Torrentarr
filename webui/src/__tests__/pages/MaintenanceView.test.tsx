import { beforeEach, expect, it, vi } from "vitest";
import {
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { set } from "lodash-es";
import { MaintenanceView } from "../../pages/MaintenanceView";
import * as api from "../../api/client";
import type {
  ConfigDocument,
  ConfigSchemaResponse,
  MaintenancePlan,
  MaintenanceStatus,
} from "../../api/types";
import { CONFIG_SCHEMA } from "../../config/configFields.generated";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import {
  maintenanceOperations,
  maintenanceToolForPath,
} from "../../config/maintenanceOperations";
import {
  configureConfigFields,
  getClientFields,
  getSchemaDefaults,
} from "../../pages/config/configFields";
vi.mock("../../api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../api/client")>()),
  getConfig: vi.fn(),
  getConfigSchema: vi.fn(),
  updateConfig: vi.fn(),
  getTorrentClients: vi.fn(),
  getMaintenanceStatus: vi.fn(),
  getMaintenanceHistory: vi.fn(),
  previewMaintenance: vi.fn(),
  applyMaintenance: vi.fn(),
  maintenanceCommand: vi.fn(),
}));
let status: MaintenanceStatus;
let plan: MaintenancePlan;
let document: ConfigDocument;
function show(props: Parameters<typeof MaintenanceView>[0] = {}) {
  return render(
    <ToastProvider>
      <WebUIProvider>
        <MaintenanceView {...props} />
      </WebUIProvider>
    </ToastProvider>,
  );
}
function openTool(name: string) {
  const card = screen
    .getByText(name, { selector: "strong" })
    .closest("details")!;
  fireEvent.click(card.querySelector("summary")!);
  return card;
}
async function chooseRecycle() {
  await userEvent.click(screen.getByLabelText(/Outcome after failed repair/));
  await userEvent.click(screen.getByRole("option", { name: "recycle" }));
}
async function loaded() {
  await screen.findByText("Shared settings · enabled");
}
beforeEach(() => {
  vi.clearAllMocks();
  configureConfigFields(CONFIG_SCHEMA as unknown as ConfigSchemaResponse);
  const client = getSchemaDefaults("TorrentClient");
  set(client, "Maintenance.Enabled", true);
  for (const tool of maintenanceOperations) {
    set(client, ["Maintenance", "Operations", tool.operation, "Enabled"], true);
    set(
      client,
      ["Maintenance", "Operations", tool.operation, "ExecutionMode"],
      "manual",
    );
  }
  document = {
    TorrentClient: {
      one: structuredClone(client),
      two: structuredClone(client),
    },
  };
  status = {
    running: false,
    armed: {},
    configurationFingerprint: "fixture",
    operations: maintenanceOperations.map((tool) => ({
      clientInstanceId: "one",
      operation: tool.operation,
      enabled: true,
      executionMode: "manual",
      authorized: false,
      schedule: "",
      nextRun: null,
    })),
  };
  plan = {
    planId: "reviewed",
    configurationFingerprint: "fixture",
    expiresAt: new Date(Date.now() + 60000).toISOString(),
    clientInstanceIds: ["one"],
    operations: ["OrphanScan"],
    actions: [
      {
        id: "file",
        operation: "OrphanScan",
        kind: "QuarantineFile",
        clientInstanceId: "one",
        path: "/downloads/orphan",
        bytes: 4,
        reason: "Not referenced",
        destructive: true,
        requiredCapabilities: [],
      },
    ],
    warnings: [],
    criticalErrors: [],
    candidateCount: 1,
    blockedCount: 0,
    candidateBytes: 4,
    operationResults: [],
  };
  vi.mocked(api.getConfig).mockImplementation(async () =>
    structuredClone(document),
  );
  vi.mocked(api.getConfigSchema).mockResolvedValue(
    CONFIG_SCHEMA as unknown as ConfigSchemaResponse,
  );
  vi.mocked(api.getTorrentClients).mockResolvedValue(
    ["one", "two"].map((id) => ({
      id,
      type: "qbittorrent",
      connected: true,
      capabilities: {},
      managedCategories: [],
    })),
  );
  vi.mocked(api.getMaintenanceStatus).mockImplementation(async () => status);
  vi.mocked(api.getMaintenanceHistory).mockResolvedValue([]);
  vi.mocked(api.maintenanceCommand).mockResolvedValue(status);
  vi.mocked(api.previewMaintenance).mockImplementation(async () => plan);
  vi.mocked(api.updateConfig).mockImplementation(async ({ changes }) => {
    for (const [path, value] of Object.entries(changes))
      set(document, path, value);
    return {
      configReloaded: true,
      reloadType: "hot",
      affectedInstances: [],
    } as Awaited<ReturnType<typeof api.updateConfig>>;
  });
  vi.mocked(api.applyMaintenance).mockResolvedValue({
    runId: "run",
    clientInstanceIds: ["one"],
    trigger: "once",
    startedAt: new Date().toISOString(),
    planned: 1,
    applied: 1,
    skipped: 0,
    failed: 0,
    bytesMoved: 4,
    result: "success",
    errors: [],
  });
});
it("opens without scans or mutations and keeps every tool collapsed", async () => {
  show();
  await loaded();
  expect(screen.getByRole("heading", { name: "Manage" })).toBeInTheDocument();
  for (const tool of maintenanceOperations)
    expect(
      screen.getByText(tool.title, { selector: "strong" }).closest("details"),
    ).not.toHaveAttribute("open");
  expect(
    screen.getByText("Recent runs").closest("details"),
  ).not.toHaveAttribute("open");
  expect(api.previewMaintenance).not.toHaveBeenCalled();
  expect(api.updateConfig).not.toHaveBeenCalled();
  expect(api.maintenanceCommand).not.toHaveBeenCalled();
});
it("opens one tool at a time with advanced settings collapsed", async () => {
  show();
  await loaded();
  const first = openTool("Error recovery");
  await waitFor(() => expect(first).toHaveAttribute("open"));
  expect(
    within(first)
      .getByText("Advanced", { selector: "summary" })
      .closest("details"),
  ).not.toHaveAttribute("open");
  const second = openTool("Hardlink audit");
  await waitFor(() => expect(second).toHaveAttribute("open"));
  expect(first).not.toHaveAttribute("open");
});
it("assigns every schema field to exactly one tool or shared settings", () => {
  const fields = getClientFields().filter(
    (field) => field.path?.[0] === "Maintenance",
  );
  expect(fields.length).toBeGreaterThan(90);
  for (const field of fields) {
    const owner = maintenanceToolForPath(field.path!);
    if (owner)
      expect(
        maintenanceOperations.filter((tool) => tool.operation === owner),
      ).toHaveLength(1);
  }
});
it("checks only the selected tool and applies a reviewed preview once", async () => {
  show();
  await loaded();
  openTool("Orphan-file cleanup");
  fireEvent.click(
    screen.getByRole("button", { name: "Check orphan-file cleanup" }),
  );
  await screen.findByText("/downloads/orphan");
  expect(api.previewMaintenance).toHaveBeenCalledWith(["one"], ["OrphanScan"]);
  fireEvent.click(
    screen.getByRole("button", { name: /Apply reviewed changes/ }),
  );
  await waitFor(() =>
    expect(api.applyMaintenance).toHaveBeenCalledWith("reviewed", true),
  );
  expect(screen.getByText("Recent runs").closest("details")).toHaveAttribute(
    "open",
  );
});
it("saves only changed maintenance paths and never executes on save", async () => {
  show();
  await loaded();
  openTool("Error recovery");
  await chooseRecycle();
  fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
  await waitFor(() =>
    expect(api.updateConfig).toHaveBeenCalledWith({
      changes: {
        "TorrentClient.one.Maintenance.ErrorRecovery.Outcome": "recycle",
      },
    }),
  );
  expect(api.previewMaintenance).not.toHaveBeenCalled();
  expect(api.applyMaintenance).not.toHaveBeenCalled();
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Save settings" }),
    ).toBeDisabled(),
  );
});
it("preserves a draft when switching clients is cancelled and discards only after confirmation", async () => {
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(false);
  show();
  await loaded();
  openTool("Error recovery");
  await chooseRecycle();
  fireEvent.change(screen.getByLabelText("Torrent client"), {
    target: { value: "two" },
  });
  expect(screen.getByLabelText("Torrent client")).toHaveValue("one");
  confirm.mockReturnValue(true);
  fireEvent.change(screen.getByLabelText("Torrent client"), {
    target: { value: "two" },
  });
  expect(screen.getByLabelText("Torrent client")).toHaveValue("two");
  expect(
    screen.getByText("keep", { selector: "div[class*=singleValue]" }),
  ).toBeInTheDocument();
  confirm.mockRestore();
});
it("selects the client requested by the configuration shortcut", async () => {
  show({ requestedClientId: "two" });
  await loaded();
  expect(screen.getByLabelText("Torrent client")).toHaveValue("two");
});
it("reveals advanced validation errors and does not save invalid settings", async () => {
  show();
  await loaded();
  const tool = openTool("Error recovery");
  fireEvent.click(within(tool).getByText("Advanced", { selector: "summary" }));
  fireEvent.change(screen.getByLabelText(/Repair attempts per incident/), {
    target: { value: "0" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
  expect(api.updateConfig).not.toHaveBeenCalled();
  await waitFor(() =>
    expect(screen.getAllByRole("alert").length).toBeGreaterThan(0),
  );
  expect(
    within(tool)
      .getByText("Advanced", { selector: "summary" })
      .closest("details"),
  ).toHaveAttribute("open");
});
it("keeps unsaved settings after a failed save", async () => {
  vi.mocked(api.updateConfig).mockRejectedValue(new Error("Save failed"));
  show();
  await loaded();
  openTool("Error recovery");
  await chooseRecycle();
  fireEvent.click(screen.getByRole("button", { name: "Save settings" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Save failed");
  expect(
    screen.getByText("recycle", { selector: "div[class*=singleValue]" }),
  ).toBeInTheDocument();
});
it("invalidates preview when settings are edited", async () => {
  show();
  await loaded();
  openTool("Orphan-file cleanup");
  fireEvent.click(
    screen.getByRole("button", { name: "Check orphan-file cleanup" }),
  );
  await screen.findByText("/downloads/orphan");
  openTool("Error recovery");
  await chooseRecycle();
  expect(
    screen.getByRole("button", { name: /Apply reviewed changes/ }),
  ).toBeDisabled();
});

it("offers two modes and preserves a schedule hidden in Manual without executing", async () => {
  show();
  await loaded();
  const card = openTool("Error recovery");
  const ui = within(card);
  expect(
    ui.queryByLabelText("Operation schedule (UTC)"),
  ).not.toBeInTheDocument();
  await userEvent.click(ui.getByLabelText("Execution mode"));
  expect(
    within(screen.getByRole("listbox"))
      .getAllByRole("option")
      .map((option) => option.textContent),
  ).toEqual(["Manual", "Automatic"]);
  await userEvent.click(screen.getByRole("option", { name: "Automatic" }));
  const schedule = ui.getByLabelText("Operation schedule (UTC)");
  fireEvent.change(schedule, { target: { value: "" } });
  expect(ui.getByText(/Effective schedule \(UTC\):/)).toHaveTextContent(
    "*/15 * * * *",
  );
  fireEvent.change(schedule, { target: { value: "*/7 * * * *" } });
  await userEvent.click(ui.getByLabelText("Execution mode"));
  await userEvent.click(screen.getByRole("option", { name: "Manual" }));
  expect(
    ui.queryByLabelText("Operation schedule (UTC)"),
  ).not.toBeInTheDocument();
  await userEvent.click(ui.getByLabelText("Execution mode"));
  await userEvent.click(screen.getByRole("option", { name: "Automatic" }));
  expect(ui.getByLabelText("Operation schedule (UTC)")).toHaveValue(
    "*/7 * * * *",
  );
  expect(api.previewMaintenance).not.toHaveBeenCalled();
  expect(api.applyMaintenance).not.toHaveBeenCalled();
  expect(api.maintenanceCommand).not.toHaveBeenCalled();
  expect(screen.queryByText("Authorize session")).not.toBeInTheDocument();
});

it("reveals an invalid schedule even while the tool is Manual", async () => {
  set(
    document,
    "TorrentClient.one.Maintenance.Operations.ErrorRecovery.Schedule",
    "bad schedule",
  );
  show();
  await loaded();
  const card = openTool("Error recovery");
  expect(
    within(card).queryByLabelText("Operation schedule (UTC)"),
  ).not.toBeInTheDocument();
  await chooseRecycle();
  await userEvent.click(screen.getByRole("button", { name: "Save settings" }));
  expect(
    await within(card).findByLabelText("Operation schedule (UTC)"),
  ).toBeVisible();
  expect(api.updateConfig).not.toHaveBeenCalled();
});
