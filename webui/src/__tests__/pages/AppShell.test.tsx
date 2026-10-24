import React from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { AppShell } from "../../AppShell";
import { ToastProvider } from "../../context/ToastContext";
import { SearchProvider } from "../../context/SearchContext";
import { WebUIProvider } from "../../context/WebUIContext";
import * as api from "../../api/client";

vi.mock("../../api/client", async (original) => ({
  ...(await original<typeof import("../../api/client")>()),
  getStatus: vi.fn(),
  getConfig: vi.fn(async () => ({ WebUI: { LiveArr: false } })),
}));
vi.mock("../../pages/ProcessesView", () => ({
  ProcessesView: () => <p>Worker dashboard</p>,
}));
vi.mock("../../pages/ArrCatalogView", () => ({
  ArrCatalogView: () => <input aria-label="catalog filter" />,
}));
const empty = { ready: true, arrs: [], qbitInstances: {} };
const offline = {
  ...empty,
  arrs: [
    {
      name: "Readarr-books",
      category: "shared",
      type: "readarr",
      alive: false,
    },
  ],
  qbitInstances: {
    transmission: { alive: false, host: "offline", port: 9091, version: null },
  },
};
function view() {
  return render(
    <ToastProvider>
      <SearchProvider>
        <WebUIProvider>
          <AppShell
            authRequired={false}
            onSignOut={() => {}}
            initialMeta={{ current_version: "6.15.2" }}
          />
        </WebUIProvider>
      </SearchProvider>
    </ToastProvider>,
  );
}
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.mocked(api.getStatus).mockResolvedValue(empty);
});
function saved() {
  window.dispatchEvent(new Event("torrentarr:config-saved"));
}
describe("configured navigation", () => {
  it("hides every unavailable Arr/client tab while keeping core views", async () => {
    view();
    await waitFor(() => expect(api.getStatus).toHaveBeenCalled());
    for (const name of [
      "Radarr",
      "Sonarr",
      "Lidarr",
      "Readarr",
      "Torrent Clients",
    ])
      expect(
        screen.queryByRole("button", { name, exact: true }),
      ).not.toBeInTheDocument();
    for (const name of ["Processes", "Logs", "Maintenance", "Config"])
      expect(
        screen.getByRole("button", { name, exact: true }),
      ).toBeInTheDocument();
    expect(screen.queryByLabelText("catalog filter")).not.toBeInTheDocument();
  });
  it("shows configured offline instances, retains page state and removes deleted tabs", async () => {
    vi.mocked(api.getStatus).mockResolvedValue(offline);
    view();
    fireEvent.click(
      await screen.findByRole("button", { name: "Readarr", exact: true }),
    );
    const filter = await screen.findByLabelText("catalog filter");
    fireEvent.change(filter, { target: { value: "Dune" } });
    const scrollTo = vi.spyOn(window, "scrollTo").mockImplementation(() => {});
    Object.defineProperty(window, "scrollY", {
      value: 420,
      configurable: true,
    });
    fireEvent.click(
      screen.getByRole("button", { name: "Processes", exact: true }),
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Readarr", exact: true }),
    );
    expect(screen.getByLabelText("catalog filter")).toHaveValue("Dune");
    expect(scrollTo).toHaveBeenLastCalledWith(0, 420);
    Object.defineProperty(window, "scrollY", { value: 0, configurable: true });
    scrollTo.mockRestore();
    expect(
      screen.getByRole("button", { name: "Torrent Clients", exact: true }),
    ).toBeInTheDocument();
    vi.mocked(api.getStatus).mockRejectedValueOnce(new Error("offline"));
    saved();
    await waitFor(() => expect(api.getStatus).toHaveBeenCalledTimes(3));
    expect(
      screen.getByRole("button", { name: "Readarr", exact: true }),
    ).toBeInTheDocument();
    vi.mocked(api.getStatus).mockResolvedValue(empty);
    saved();
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: "Readarr", exact: true }),
      ).not.toBeInTheDocument(),
    );
    await waitFor(() =>
      expect(screen.getByText("Worker dashboard")).toBeVisible(),
    );
    expect(screen.queryByLabelText("catalog filter")).not.toBeInTheDocument();
  });
});

it("renders the authenticated shell when browser storage is blocked", async () => {
  vi.spyOn(window, "localStorage", "get").mockImplementation(() => {
    throw new Error("blocked");
  });
  try {
    view();
    expect(await screen.findByText("Worker dashboard")).toBeInTheDocument();
  } finally {
    vi.restoreAllMocks();
  }
});
