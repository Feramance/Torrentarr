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
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { LogsView } from "../../pages/LogsView";
import { ToastProvider } from "../../context/ToastContext";

// LazyLog uses ResizeObserver which isn't available in JSDOM
vi.mock("@melloware/react-logviewer", () => ({
  LazyLog: ({ text }: { text: string }) => (
    <div data-testid="lazy-log">{text}</div>
  ),
}));

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function Wrapper({ children }: { children: ReactNode }) {
  return <ToastProvider>{children}</ToastProvider>;
}

function renderView(active = true) {
  return render(<LogsView active={active} />, { wrapper: Wrapper });
}

// ── Structural rendering ──────────────────────────────────────────────────────

describe("LogsView – card header", () => {
  it("renders the Logs card header", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByText("Logs");
  });

  it("renders the Log File label", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByText("Log File");
  });
});

// ── Empty state ───────────────────────────────────────────────────────────────

describe("LogsView – empty state", () => {
  it("shows the select-prompt when no files are returned", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByText("Select a log file to view...");
  });

  it("shows select-prompt when list request fails", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.error()));

    renderView();

    // Error is swallowed (toast shown instead); component stays in empty state
    await screen.findByText("Select a log file to view...");
  });
});

// ── Control buttons ───────────────────────────────────────────────────────────

describe("LogsView – control buttons", () => {
  it("renders the Reload List button", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByRole("button", { name: /reload list/i });
  });

  it("renders the Reload button", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByRole("button", { name: /^reload$/i });
  });

  it("renders the Download button", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByRole("button", { name: /download/i });
  });

  it("renders the Auto-scroll checkbox", async () => {
    server.use(http.get("/web/logs", () => HttpResponse.json({ files: [] })));

    renderView();

    await screen.findByText("Auto-scroll");
    expect(screen.getByRole("checkbox")).toBeInTheDocument();
  });
});

// ── File list loaded ──────────────────────────────────────────────────────────

describe("LogsView – with files", () => {
  it("renders LazyLog when log content is available", async () => {
    server.use(
      http.get("/web/logs", () =>
        HttpResponse.json({
          files: [
            { name: "All.log", size: 1024, modified: new Date().toISOString() },
          ],
        }),
      ),
      http.get("/web/logs/All.log", () =>
        HttpResponse.text("2025-01-01 INFO Starting"),
      ),
    );

    renderView();

    // Wait for log content to be loaded and passed to the mocked LazyLog
    const logEl = await screen.findByTestId("lazy-log");
    expect(logEl).toBeInTheDocument();
    expect(logEl.textContent).toContain("INFO");
  });

  it("does not show the select-prompt when log content is available", async () => {
    server.use(
      http.get("/web/logs", () =>
        HttpResponse.json({
          files: [
            { name: "All.log", size: 1024, modified: new Date().toISOString() },
          ],
        }),
      ),
      http.get("/web/logs/All.log", () =>
        HttpResponse.text("2025-01-01 INFO Starting"),
      ),
    );

    renderView();

    await screen.findByTestId("lazy-log");
    expect(
      screen.queryByText("Select a log file to view..."),
    ).not.toBeInTheDocument();
  });
});
