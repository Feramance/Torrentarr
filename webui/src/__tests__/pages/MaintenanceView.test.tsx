import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { MaintenanceView } from "../../pages/MaintenanceView";

const server = setupServer();
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

describe("MaintenanceView", () => {
  it("shows exact destructive preview counts and capability skips", async () => {
    server.use(
      http.get("/web/torrent-clients", () => HttpResponse.json([{ id: "qBit", QbitInstance: "qBit", type: "qbittorrent", connected: true, version: "5.0", capabilities: {}, managedCategories: ["movies"] }])),
      http.get("/web/maintenance/status", () => HttpResponse.json({ running: false, armed: { qBit: false }, nextScheduledRun: {} })),
      http.get("/web/maintenance/history", () => HttpResponse.json([])),
      http.post("/web/maintenance/preview", () => HttpResponse.json({
        planId: "plan", expiresAt: new Date().toISOString(), configurationFingerprint: "x",
        clientInstanceIds: ["qBit"], operations: ["OrphanScan"], candidateCount: 1,
        blockedCount: 1, candidateBytes: 4096, warnings: [], criticalErrors: [],
        actions: [
          { id: "1", kind: "RecycleContent", clientInstanceId: "qBit", torrentName: "movie", bytes: 4096, reason: "policy", destructive: true, requiredCapabilities: [] },
          { id: "2", kind: "AddTag", clientInstanceId: "qBit", torrentName: "other", bytes: 0, reason: "audit", destructive: false, blockedReason: "Client does not support tags.", requiredCapabilities: ["tags"] },
        ],
      })),
    );
    render(<MaintenanceView />);
    await screen.findByText(/qBit · qbittorrent/);
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    await screen.findByText((_, element) => element?.tagName === "P" && element.textContent?.includes("1 actions · 1 destructive") === true);
    expect(screen.getByText(/Client does not support tags/)).toBeInTheDocument();
  });
});
