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
      await screen.findByRole("button", { name: "Expand Readarr" }),
    );
    fireEvent.click(
      screen.getByRole("button", {
        name: "Readarr-books",
        exact: true,
      }),
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
    fireEvent.click(screen.getByRole("button", { name: "Expand Readarr" }));
    fireEvent.click(
      screen.getByRole("button", { name: "Readarr-books", exact: true }),
    );
    expect(screen.getByLabelText("catalog filter")).toHaveValue("Dune");
    expect(scrollTo).toHaveBeenLastCalledWith(0, 420);
    Object.defineProperty(window, "scrollY", { value: 0, configurable: true });
    scrollTo.mockRestore();
    fireEvent.click(
      screen.getByRole("button", { name: "Expand Torrent Clients" }),
    );
    expect(
      screen.getByRole("button", { name: "transmission", exact: true }),
    ).toBeInTheDocument();
    vi.mocked(api.getStatus).mockRejectedValueOnce(new Error("offline"));
    saved();
    await waitFor(() => expect(api.getStatus).toHaveBeenCalledTimes(3));
    expect(
      screen.getByRole("button", { name: "Expand Readarr" }),
    ).toBeInTheDocument();
    vi.mocked(api.getStatus).mockResolvedValue(empty);
    saved();
    await waitFor(() =>
      expect(
        screen.queryByRole("button", { name: "Expand Readarr" }),
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

it("expands Arr choices in the drawer before navigating", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ...empty,
    arrs: [
      { name: "Radarr-HD", category: "movies", type: "radarr" },
      { name: "Radarr-4K", category: "movies4k", type: "radarr" },
    ],
  });
  view();
  fireEvent.click(screen.getByRole("button", { name: "Menu", exact: true }));
  const drawer = screen.getByRole("dialog", { name: "Navigation" });
  fireEvent.click(
    await within(drawer).findByRole("button", {
      name: "Radarr",
      exact: true,
    }),
  );
  expect(
    within(drawer).getByRole("button", { name: "All Radarr" }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("dialog", { name: "Navigation" }),
  ).toBeInTheDocument();
  expect(screen.getByRole("heading", { name: "Processes" })).toBeVisible();
  fireEvent.click(within(drawer).getByRole("button", { name: "Radarr-4K" }));
  expect(
    screen.queryByRole("dialog", { name: "Navigation" }),
  ).not.toBeInTheDocument();
  expect(
    await screen.findByRole("heading", { name: "Radarr-4K" }),
  ).toBeVisible();
});

it("no longer shows a Help menu in the app bar", () => {
  view();
  expect(
    screen.queryByRole("button", { name: "Help", exact: true }),
  ).not.toBeInTheDocument();
});

it("collapses the desktop navigation to an icon rail", () => {
  view();
  const shell = document.querySelector(".console-shell");
  const trigger = screen.getByRole("button", {
    name: "Collapse navigation",
  });
  expect(trigger).toHaveAttribute("aria-controls", "app-navigation");
  expect(trigger).toHaveAttribute("aria-expanded", "true");
  fireEvent.click(trigger);
  expect(shell).toHaveAttribute("data-nav", "collapsed");
  expect(
    screen.getByRole("button", { name: "Expand navigation" }),
  ).toHaveAttribute("aria-expanded", "false");
  fireEvent.click(screen.getByRole("button", { name: "Expand navigation" }));
  expect(shell).toHaveAttribute("data-nav", "expanded");
  expect(trigger).toHaveAttribute("aria-expanded", "true");
});

it("expands the rail for instance selection and collapses after choosing", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ...empty,
    arrs: [
      { name: "Radarr-HD", category: "movies", type: "radarr" },
      { name: "Radarr-4K", category: "movies4k", type: "radarr" },
    ],
  });
  view();
  const shell = document.querySelector(".console-shell");
  await screen.findByRole("button", { name: "Expand Radarr" });
  fireEvent.click(screen.getByRole("button", { name: "Collapse navigation" }));
  fireEvent.click(screen.getByRole("button", { name: "Radarr", exact: true }));
  expect(shell).toHaveAttribute("data-nav", "expanded");
  expect(screen.getByRole("button", { name: "Radarr-4K" })).toBeInTheDocument();
  expect(screen.getByRole("heading", { name: "Processes" })).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "Radarr-4K" }));
  expect(shell).toHaveAttribute("data-nav", "collapsed");
  expect(
    await screen.findByRole("heading", { name: "Radarr-4K" }),
  ).toBeVisible();
});

it("collapses an expanded Arr section when leaving that view", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ...empty,
    arrs: [
      { name: "Radarr-HD", category: "movies", type: "radarr" },
      { name: "Radarr-4K", category: "movies4k", type: "radarr" },
    ],
  });
  view();
  const disclosure = await screen.findByRole("button", {
    name: "Expand Radarr",
  });
  fireEvent.click(disclosure);
  expect(screen.getByRole("button", { name: "Radarr-4K" })).toBeInTheDocument();
  fireEvent.click(
    screen.getByRole("button", { name: "Processes", exact: true }),
  );
  expect(disclosure).toHaveAttribute("aria-expanded", "false");
  expect(
    screen.queryByRole("button", { name: "Radarr-4K" }),
  ).not.toBeInTheDocument();
});

it("provides Wiki, GitHub and OpenAPI links on desktop and in the drawer", () => {
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
  expect(
    within(resources).getByRole("link", { name: /OpenAPI/ }),
  ).toHaveAttribute("href", "/web/docs");
  fireEvent.click(screen.getByRole("button", { name: "Menu", exact: true }));
  const drawer = screen.getByRole("dialog", { name: "Navigation" });
  expect(
    within(drawer).getByRole("link", { name: /Wiki/ }),
  ).toBeInTheDocument();
  expect(within(drawer).getByRole("link", { name: /GitHub/ })).toHaveAttribute(
    "rel",
    "noopener noreferrer",
  );
  expect(within(drawer).getByRole("link", { name: /OpenAPI/ })).toHaveAttribute(
    "rel",
    "noopener noreferrer",
  );
});

it("clears a stale client focus when Arr navigation requests all clients", async () => {
  vi.mocked(api.getStatus).mockResolvedValue(offline);
  view();
  fireEvent.click(
    await screen.findByRole("button", { name: "Expand Torrent Clients" }),
  );
  await screen.findByRole("button", { name: "transmission", exact: true });
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

it("expands configured instances in the main navigation without replacing the active view", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ...empty,
    arrs: [
      { name: "Radarr-HD", category: "movies", type: "radarr" },
      { name: "Radarr-4K", category: "movies4k", type: "radarr" },
    ],
  });
  view();
  const disclosure = await screen.findByRole("button", {
    name: "Expand Radarr",
  });
  expect(disclosure).toHaveAttribute("aria-expanded", "false");
  fireEvent.click(disclosure);
  expect(disclosure).toHaveAttribute("aria-expanded", "true");
  expect(screen.getByText("Worker dashboard")).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "Radarr-4K" }));
  expect(screen.getByRole("button", { name: "Radarr-4K" })).toHaveAttribute(
    "aria-current",
    "page",
  );
  expect(
    screen.getByRole("button", { name: "All Radarr" }),
  ).toBeInTheDocument();
});

it("keeps only one navigation section expanded at a time", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ...empty,
    arrs: [
      { name: "Radarr-HD", category: "movies", type: "radarr" },
      { name: "Radarr-4K", category: "movies4k", type: "radarr" },
    ],
    qbitInstances: {
      "qBit-home": { alive: true, host: "fixture", port: 1, version: null },
      "qBit-seedbox": {
        alive: true,
        host: "fixture",
        port: 2,
        version: null,
      },
    },
  });
  view();
  const radarr = await screen.findByRole("button", { name: "Expand Radarr" });
  fireEvent.click(radarr);
  expect(screen.getByRole("button", { name: "Radarr-4K" })).toBeInTheDocument();

  const clients = screen.getByRole("button", {
    name: "Expand Torrent Clients",
  });
  fireEvent.click(clients);
  expect(radarr).toHaveAttribute("aria-expanded", "false");
  expect(
    screen.queryByRole("button", { name: "Radarr-4K" }),
  ).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "qBit-home" })).toBeInTheDocument();

  fireEvent.click(screen.getByRole("button", { name: "Radarr", exact: true }));
  expect(clients).toHaveAttribute("aria-expanded", "false");
  expect(screen.getByRole("button", { name: "Radarr-4K" })).toBeInTheDocument();
});

it("shows a collapsed section for a single configured instance", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ...empty,
    arrs: [{ name: "Radarr-HD", category: "movies", type: "radarr" }],
    qbitInstances: {
      "qBit-home": { alive: true, host: "fixture", port: 1, version: null },
    },
  });
  view();
  const radarr = await screen.findByRole("button", { name: "Expand Radarr" });
  const clients = screen.getByRole("button", {
    name: "Expand Torrent Clients",
  });
  expect(radarr).toHaveAttribute("aria-expanded", "false");
  expect(clients).toHaveAttribute("aria-expanded", "false");
  expect(
    screen.queryByRole("button", { name: "Radarr-HD", exact: true }),
  ).not.toBeInTheDocument();

  fireEvent.click(radarr);
  expect(radarr).toHaveAttribute("aria-expanded", "true");
  expect(
    screen.getByRole("button", { name: "Radarr-HD", exact: true }),
  ).toBeInTheDocument();
  expect(
    screen.queryByRole("button", { name: "All Radarr" }),
  ).not.toBeInTheDocument();

  fireEvent.click(
    screen.getByRole("button", { name: "Radarr-HD", exact: true }),
  );
  expect(await screen.findByLabelText("catalog filter")).toBeInTheDocument();
  expect(screen.getByRole("heading", { name: "Radarr-HD" })).toBeVisible();
});
