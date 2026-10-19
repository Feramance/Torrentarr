import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
  vi,
} from "vitest";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { MaintenanceView } from "../../pages/MaintenanceView";
import type {
  MaintenancePlan,
  MaintenanceStatus,
  MaintenanceRunSummary,
} from "../../api/types";

const server = setupServer();
let plan: MaintenancePlan;
let status: MaintenanceStatus;
let history: MaintenanceRunSummary[];
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
});
afterAll(() => server.close());
beforeEach(() => {
  history = [];
  status = {
    running: false,
    armed: { qBit: false },
    armedOperations: { qBit: [] },
    nextScheduledRun: {},
  };
  plan = {
    planId: "plan",
    expiresAt: new Date(Date.now() + 60000).toISOString(),
    configurationFingerprint: "x",
    clientInstanceIds: ["qBit"],
    operations: ["CategoryReconcile", "OrphanScan"],
    candidateCount: 1,
    blockedCount: 1,
    candidateBytes: 0,
    warnings: ["Legacy grouped warning"],
    criticalErrors: [],
    operationResults: [
      {
        clientInstanceId: "qBit",
        operation: "CategoryReconcile",
        state: "ready",
        examined: 300,
        excluded: 0,
        uninspectable: 0,
        actionable: 1,
        blockedActions: 0,
      },
      {
        clientInstanceId: "qBit",
        operation: "OrphanScan",
        state: "blocked",
        examined: 0,
        excluded: 0,
        uninspectable: 300,
        actionable: 0,
        blockedActions: 1,
        reason: "File ownership inventory is incomplete.",
      },
    ],
    diagnostics: [
      {
        code: "missing-mapping",
        severity: "warning",
        clientInstanceId: "qBit",
        operations: ["OrphanScan"],
        message: "No explicit path mapping is configured.",
        remediation: "Check Maintenance.PathMappings in Config.",
        affectedFiles: 300,
        affectedTorrents: 100,
        examples: [
          {
            torrentName: "movie",
            clientPath: "/downloads/movie.mkv",
            localPath: "/torrents/movie.mkv",
          },
        ],
      },
    ],
    actions: [
      {
        id: "1",
        operation: "CategoryReconcile",
        kind: "SetCategory",
        clientInstanceId: "qBit",
        torrentName: "movie",
        bytes: 0,
        reason: "category mapping",
        destructive: false,
        requiredCapabilities: [],
      },
      {
        id: "2",
        operation: "OrphanScan",
        kind: "QuarantineFile",
        clientInstanceId: "qBit",
        path: "/torrents/other",
        bytes: 4,
        reason: "orphan",
        destructive: true,
        blockedReason: "File ownership inventory is incomplete.",
        requiredCapabilities: [],
      },
    ],
  };
  server.use(
    http.get("/web/torrent-clients", () =>
      HttpResponse.json([
        {
          id: "qBit",
          type: "qbittorrent",
          connected: true,
          version: "5.0",
          capabilities: {},
          managedCategories: ["movies"],
        },
      ]),
    ),
    http.get("/web/maintenance/status", () => HttpResponse.json(status)),
    http.get("/web/maintenance/history", () => HttpResponse.json(history)),
    http.post("/web/maintenance/preview", () => HttpResponse.json(plan)),
    http.post("/web/maintenance/arm", () => {
      status.armed.qBit = true;
      status.armedOperations.qBit = ["CategoryReconcile"];
      return HttpResponse.json(status);
    }),
  );
});

async function preview() {
  await screen.findByText(/qBit · qbittorrent/);
  fireEvent.click(screen.getByRole("button", { name: "Preview" }));
  await screen.findByRole("table");
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Preview" })).toBeEnabled(),
  );
}

it("keeps historical runs with retired operation IDs visible without offering that operation", async () => {
  history = [
    {
      runId: "historical",
      startedAt: "2026-10-19T10:00:00Z",
      trigger: "manual",
      result: "partial",
      clientInstanceIds: ["qBit"],
      planned: 0,
      applied: 0,
      skipped: 0,
      failed: 0,
      blocked: 1,
      pending: 0,
      bytesMoved: 0,
      errors: [],
      diagnostics: [],
      operationResults: [
        {
          clientInstanceId: "qBit",
          operation: 8,
          state: "blocked",
          examined: 0,
          excluded: 0,
          uninspectable: 0,
          actionable: 0,
          blockedActions: 1,
          reason: "Historical rule",
        },
      ],
    },
  ];
  render(<MaintenanceView />);
  expect(
    await screen.findByText(/qBit \/ 8: blocked — Historical rule/),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("checkbox", { name: /share polic/i }),
  ).not.toBeInTheDocument();
});

describe("MaintenanceView", () => {
  it("shows operation coverage and bounded diagnostics separately from actions", async () => {
    const openConfig = vi.fn();
    render(<MaintenanceView onOpenConfig={openConfig} />);
    await preview();
    expect(
      screen.getByText(/blocked · 1 diagnostic issues/),
    ).toBeInTheDocument();
    const table = screen.getByRole("table");
    expect(within(table).getByText("ready")).toBeInTheDocument();
    expect(
      within(table).getByRole("cell", { name: /^blocked File ownership/ }),
    ).toHaveTextContent("File ownership inventory is incomplete.");
    const summary = screen.getByText("1 diagnostic issues — view details");
    expect(summary.closest("details")).not.toHaveAttribute("open");
    fireEvent.click(summary);
    expect(screen.getByText(/300 files · 100 torrents/)).toBeInTheDocument();
    expect(screen.getByText("/downloads/movie.mkv")).toBeInTheDocument();
    expect(screen.getByText("/torrents/movie.mkv")).toBeInTheDocument();
    expect(
      screen.queryByText("Legacy grouped warning"),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Open Config" }));
    expect(openConfig).toHaveBeenCalledOnce();
    expect(
      screen.getByRole("button", { name: "Apply preview" }),
    ).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Arm" }));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Apply preview" }),
      ).toBeEnabled(),
    );
  });

  it.each(["client", "operation"])(
    "invalidates the preview after changing a %s selection",
    async (selection) => {
      render(<MaintenanceView />);
      await preview();
      fireEvent.click(
        screen.getByRole("checkbox", {
          name: selection === "client" ? /qBit · qbittorrent/ : "Orphan Scan",
        }),
      );
      expect(screen.queryByRole("table")).not.toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Arm" })).toBeDisabled();
      if (selection === "client")
        expect(screen.getByRole("button", { name: "Preview" })).toBeDisabled();
    },
  );

  it("blocks invalid plans and distinguishes clean inspections from blocked ones", async () => {
    plan.criticalErrors = ["Unknown maintenance operation."];
    plan.operationResults[0].actionable = 0;
    render(<MaintenanceView />);
    await preview();
    expect(screen.getByText("Nothing needs changing.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Arm" })).toBeDisabled();
    expect(
      screen.getByRole("button", { name: "Apply preview" }),
    ).toBeDisabled();
  });

  it("polls active execution and keeps cancellation available during apply", async () => {
    let finishApply: (() => void) | undefined;
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const run: MaintenanceRunSummary = {
      runId: "run",
      startedAt: new Date().toISOString(),
      trigger: "apply",
      clientInstanceIds: ["qBit"],
      planned: 1,
      applied: 0,
      skipped: 0,
      failed: 0,
      blocked: 1,
      pending: 1,
      bytesMoved: 0,
      result: "running",
      errors: [],
      operationResults: plan.operationResults,
      diagnostics: plan.diagnostics,
    };
    server.use(
      http.post("/web/maintenance/apply", async () => {
        status.running = true;
        status.currentRun = run;
        await new Promise<void>((resolve) => {
          finishApply = resolve;
        });
        return HttpResponse.json(run);
      }),
      http.post("/web/maintenance/cancel", () => {
        status.running = false;
        status.currentRun = undefined;
        run.result = "cancelled";
        run.completedAt = new Date().toISOString();
        history = [run];
        finishApply?.();
        return new HttpResponse(null, { status: 202 });
      }),
    );
    render(<MaintenanceView />);
    await preview();
    fireEvent.click(screen.getByRole("button", { name: "Arm" }));
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Apply preview" }),
      ).toBeEnabled(),
    );
    fireEvent.click(screen.getByRole("button", { name: "Apply preview" }));
    expect(
      await screen.findByRole("status", {}, { timeout: 5000 }),
    ).toHaveTextContent("1 pending");
    expect(screen.getByRole("button", { name: "Cancel" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await screen.findByText(/cancelled/);
    expect(screen.getByText(/qBit \/ OrphanScan: blocked/)).toBeInTheDocument();
  }, 12000);
});
