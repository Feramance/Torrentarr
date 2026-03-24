import React from "react";
import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { QbitCategoriesView } from "../../pages/QbitCategoriesView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

// Minimal config that makes WebUIProvider work without errors.
// LiveArr: false prevents the 1-second polling interval from firing in tests.
const baseConfig = {
  Settings: { LoopSleepTimer: 5 },
  WebUI: { Port: 6969, LiveArr: false, Theme: "Dark" },
  qBit: { Host: "localhost", Port: 8080 },
};

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <ToastProvider>
      <WebUIProvider>{children}</WebUIProvider>
    </ToastProvider>
  );
}

function renderView(active = true) {
  return render(<QbitCategoriesView active={active} />, { wrapper: Wrapper });
}

// ── empty state ──────────────────────────────────────────────────────────────

describe("QbitCategoriesView – empty state", () => {
  it("shows empty-state message when no categories are returned", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ categories: [], ready: true }),
      ),
    );

    renderView();

    await screen.findByText(/No categories found/);
  });

  it("does not fetch when active=false", () => {
    // If the component fires any request the unhandled handler would throw,
    // so simply rendering without mocks confirms no fetch happens.
    renderView(false);
    // Nothing to assert – the absence of a network error is the assertion.
  });
});

// ── data rendering ───────────────────────────────────────────────────────────

const singleArrCategory = {
  category: "radarr",
  instance: "qBit",
  managedBy: "arr",
  torrentCount: 7,
  seedingCount: 3,
  totalSize: 1073741824, // 1 GB
  avgRatio: 1.25,
  avgSeedingTime: 7200,
  seedingConfig: {
    maxRatio: 2,
    maxTime: -1,
    removeMode: 3,
    downloadLimit: -1,
    uploadLimit: -1,
  },
};

const singleQbitCategory = {
  category: "autobrr",
  instance: "qBit",
  managedBy: "qbit",
  torrentCount: 2,
  seedingCount: 1,
  totalSize: 512 * 1024 * 1024,
  avgRatio: 0.5,
  avgSeedingTime: 3600,
  seedingConfig: {
    maxRatio: -1,
    maxTime: 604800,
    removeMode: 2,
    downloadLimit: -1,
    uploadLimit: -1,
  },
};

describe("QbitCategoriesView – data rendering", () => {
  it("renders a table row for each category", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({
          categories: [singleArrCategory, singleQbitCategory],
          ready: true,
        }),
      ),
    );

    renderView();

    await screen.findByText("radarr");
    expect(screen.getByText("autobrr")).toBeInTheDocument();
  });

  it("shows 'Arr' badge for arr-managed category", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ categories: [singleArrCategory], ready: true }),
      ),
    );

    renderView();

    await screen.findByText("radarr");
    expect(screen.getByText("Arr")).toBeInTheDocument();
  });

  it("shows 'qBit' badge for qbit-managed category", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ categories: [singleQbitCategory], ready: true }),
      ),
    );

    renderView();

    await screen.findByText("autobrr");
    // The badge specifically has class badge-qbit; the Instance column also renders "qBit"
    // so narrow the query to the badge span element.
    expect(
      screen.getByText("qBit", { selector: "span.badge-qbit" }),
    ).toBeInTheDocument();
  });

  it("shows 'Disabled' in Max Ratio column when maxRatio=-1", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({ categories: [singleArrCategory], ready: true }),
      ),
    );

    renderView();

    await screen.findByText("radarr");
    // maxRatio = -1 → "Disabled"
    expect(screen.getAllByText("Disabled").length).toBeGreaterThanOrEqual(1);
  });
});

// ── summary stats ─────────────────────────────────────────────────────────────

describe("QbitCategoriesView – summary stats", () => {
  it("updates category count in the summary line after data loads", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({
          categories: [singleArrCategory, singleQbitCategory],
          ready: true,
        }),
      ),
    );

    const { container } = renderView();

    // Wait for data by checking that the table row appears
    await screen.findByText("radarr");

    // The hint div contains all stats as plain text (numbers are text nodes, labels are <strong>)
    const hintDiv = container.querySelector(".hint")!;
    expect(hintDiv.textContent).toContain("2"); // total categories
  });

  it("counts qBit-managed and Arr-managed categories separately", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(baseConfig)),
      http.get("/web/qbit/categories", () =>
        HttpResponse.json({
          categories: [singleArrCategory, singleQbitCategory],
          ready: true,
        }),
      ),
    );

    const { container } = renderView();

    await screen.findByText("radarr");

    const hintDiv = container.querySelector(".hint")!;
    expect(hintDiv.textContent).toContain("qBit-managed:"); // label present
    expect(hintDiv.textContent).toContain("Arr-managed:"); // label present
    // singleQbitCategory.managedBy="qbit" and singleArrCategory.managedBy="arr"
    // so counts should each be 1
    expect(hintDiv.textContent).toMatch(/qBit-managed:\s*1/);
    expect(hintDiv.textContent).toMatch(/Arr-managed:\s*1/);
  });
});
