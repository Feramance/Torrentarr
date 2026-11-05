import React from "react";
import { describe, it, expect, vi, beforeEach } from "vitest";
import {
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
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
    for (const name of ["Processes", "Logs", "Manage", "Config"])
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

it("opens Display with persisted controls and returns focus on close", async () => {
  view();
  const trigger = screen.getByRole("button", { name: "Display", exact: true });
  trigger.focus();
  fireEvent.click(trigger);
  const dialog = screen.getByRole("dialog", { name: "Display" });
  fireEvent.click(
    within(dialog).getByRole("button", { name: "Light", exact: true }),
  );
  expect(document.documentElement).toHaveAttribute("data-theme", "light");
  fireEvent.click(
    within(dialog).getByRole("button", { name: "Compact", exact: true }),
  );
  expect(document.querySelector("[data-density]")).toHaveAttribute(
    "data-density",
    "compact",
  );
  fireEvent.click(
    within(dialog).getByRole("button", { name: "Close Display" }),
  );
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(trigger).toHaveFocus();
});

it("closes the navigation drawer after switching pages and on Escape", async () => {
  view();
  fireEvent.click(screen.getByRole("button", { name: "Menu", exact: true }));
  const dialog = screen.getByRole("dialog", { name: "Navigation" });
  fireEvent.click(
    within(dialog).getByRole("button", { name: "Config", exact: true }),
  );
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Menu", exact: true }));
  fireEvent(
    screen.getByRole("dialog", { name: "Navigation" }),
    new Event("cancel", { bubbles: false, cancelable: true }),
  );
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("dismisses Display when the native popover closes", () => {
  view();
  fireEvent.click(screen.getByRole("button", { name: "Display", exact: true }));
  const dialog = screen.getByRole("dialog", { name: "Display" });
  const event = new Event("toggle");
  Object.defineProperty(event, "newState", { value: "closed" });
  fireEvent(dialog, event);
  expect(
    screen.queryByRole("dialog", { name: "Display" }),
  ).not.toBeInTheDocument();
});

it("provides Wiki and GitHub footer links on desktop and in the navigation drawer", () => {
  view();
  const resources = screen.getByLabelText("Project resources");
  const wiki = within(resources).getByRole("link", { name: /Wiki/ });
  expect(wiki).toHaveAttribute(
    "href",
    "https://feramance.github.io/Torrentarr/",
  );
  expect(wiki).toHaveAttribute("target", "_blank");
  expect(
    within(resources).getByRole("link", { name: /GitHub/ }),
  ).toHaveAttribute("href", "https://github.com/Feramance/Torrentarr");
  fireEvent.click(screen.getByRole("button", { name: "Menu", exact: true }));
  const drawer = screen.getByRole("dialog", { name: "Navigation" });
  expect(
    within(drawer).getByRole("link", { name: /Wiki/ }),
  ).toBeInTheDocument();
  expect(within(drawer).getByRole("link", { name: /GitHub/ })).toHaveAttribute(
    "rel",
    "noopener noreferrer",
  );
});

it("clears a stale client focus when Arr navigation requests all clients", async () => {
  vi.mocked(api.getStatus).mockResolvedValue(offline);
  view();
  await screen.findByRole("button", { name: "Torrent Clients", exact: true });
  sessionStorage.setItem("torrentarr:focusQbitInstance", "stale");
  window.dispatchEvent(
    new CustomEvent("qbitrr:navigate-tab", {
      detail: { tab: "qbittorrent", qbitCategory: "shared" },
    }),
  );
  expect(sessionStorage.getItem("torrentarr:focusQbitInstance")).toBeNull();
  expect(sessionStorage.getItem("qbitrr:focusQbitCategory")).toBe("shared");
  sessionStorage.clear();
});
