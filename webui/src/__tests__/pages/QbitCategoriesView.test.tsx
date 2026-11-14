import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QbitCategoriesView } from "../../pages/QbitCategoriesView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import * as api from "../../api/client";
import type {
  QbitOverviewCategory,
  QbitTorrentOverview,
} from "../../api/types";

vi.mock("../../api/client", () => ({
  getConfig: vi.fn(),
  updateConfig: vi.fn(),
  getStatus: vi.fn(),
  getQbitOverview: vi.fn(),
  getTransferHistory: vi.fn(async () => ({ series: [] })),
}));
const torrent = (
  name = "Example",
  state = "stalledUP",
): QbitTorrentOverview => ({
  hash: name,
  name,
  category: "movies",
  state,
  tags: ["private"],
  progress: 1,
  priority: 1,
  eta: 0,
  availability: 2,
  size: 1024,
  totalSize: 1024,
  downloaded: 1024,
  uploaded: 2048,
  amountLeft: 0,
  ratio: 2,
  dlspeed: 0,
  upspeed: 12,
  numSeeds: 1,
  numLeechs: 2,
  numComplete: 3,
  numIncomplete: 4,
  addedOn: 1700000000,
  completionOn: 1700000001,
  seedingTime: 3600,
  timeActive: 3600,
  lastActivity: 1700000002,
  ratioLimit: -1,
  seedingTimeLimit: -1,
  dlLimit: -1,
  upLimit: -1,
});
const category = (): QbitOverviewCategory => ({
  category: "movies",
  qbitInstance: "qBit-home",
  managedBy: "arr",
  arrName: "Radarr-1080",
  torrentCount: 2,
  seedingCount: 1,
  totalSize: 2048,
  avgRatio: 2,
  avgSeedingTime: 3600,
  seedingConfig: {
    maxRatio: 2,
    maxTime: 86400,
    removeMode: 3,
    downloadLimit: -1,
    uploadLimit: -1,
  },
  torrents: [torrent(), torrent("Downloading", "downloading")],
});
function view(active = true) {
  return render(
    <ToastProvider>
      <WebUIProvider>
        <QbitCategoriesView active={active} />
      </WebUIProvider>
    </ToastProvider>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  sessionStorage.clear();
  vi.mocked(api.getConfig).mockResolvedValue({ WebUI: { LiveArr: false } });
  vi.mocked(api.getStatus).mockResolvedValue({
    qbitInstances: {
      "qBit-home": { alive: true, host: "localhost", port: 8080, version: "5" },
    },
    arrs: [],
  });
  vi.mocked(api.getQbitOverview).mockResolvedValue({
    instances: ["qBit-home"],
    categories: [category()],
    ready: true,
  });
});
describe("torrent overview parity", () => {
  it("refreshes transfer history when live updates are disabled", async () => {
    view();
    await waitFor(() => expect(api.getTransferHistory).toHaveBeenCalled());
    const refresh = await screen.findByRole("button", { name: "Refresh" });
    await waitFor(() => expect(refresh).toBeEnabled());
    vi.mocked(api.getTransferHistory).mockClear();
    fireEvent.click(refresh);
    await waitFor(() =>
      expect(api.getTransferHistory).toHaveBeenCalledTimes(1),
    );
  });

  it("loads the only configured instance instead of the aggregate view", async () => {
    view();
    await waitFor(() =>
      expect(api.getQbitOverview).toHaveBeenCalledWith("qBit-home"),
    );
    expect(screen.queryByText(/All qBittorrent/)).not.toBeInTheDocument();
  });
  it("expands a category into torrent metrics and retains aggregate statistics", async () => {
    view();
    fireEvent.click(await screen.findByRole("button", { name: /movies.*Arr/ }));
    expect(await screen.findByText("Example")).toBeInTheDocument();
    expect(screen.getAllByText("Seeding time").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Peers").length).toBeGreaterThan(0);
    expect(screen.getAllByText("private").length).toBe(2);
    expect(screen.getByText(/Average ratio 2.00/)).toBeInTheDocument();
  });
  it("searches torrent names and recalculates the visible category summary", async () => {
    view();
    await screen.findByRole("button", { name: /movies.*Arr/ });
    fireEvent.change(screen.getByLabelText("Search"), {
      target: { value: "Example" },
    });
    const section = await screen.findByRole("button", {
      name: /movies.*Arr.*1 torrent/,
    });
    fireEvent.click(section);
    expect(screen.getByText("Example")).toBeInTheDocument();
    expect(screen.queryByText("Downloading")).not.toBeInTheDocument();
  });
  it("handles mixed subcategory policies without crashing", async () => {
    vi.mocked(api.getQbitOverview).mockResolvedValue({
      instances: ["qBit-home"],
      categories: [{ ...category(), seedingConfig: null }],
      ready: true,
    });
    view();
    expect(
      await screen.findByText("Mixed seeding policies"),
    ).toBeInTheDocument();
  });
  it("shows a useful empty state when no clients are configured", async () => {
    vi.mocked(api.getStatus).mockResolvedValue({ qbitInstances: {}, arrs: [] });
    vi.mocked(api.getQbitOverview).mockResolvedValue({
      instances: [],
      categories: [],
      ready: true,
    });
    view();
    expect(
      await screen.findByText(/No qBittorrent instances found/),
    ).toBeInTheDocument();
  });
  it("does not fetch an inactive overview", async () => {
    view(false);
    await waitFor(() => expect(api.getConfig).toHaveBeenCalled());
    expect(api.getStatus).not.toHaveBeenCalled();
    expect(api.getQbitOverview).not.toHaveBeenCalled();
  });
});

it("keeps server totals when a category search matches a truncated list", async () => {
  vi.mocked(api.getQbitOverview).mockResolvedValue({
    ready: true,
    instances: ["qBit-home"],
    categories: [{ ...category(), torrentCount: 900, seedingCount: 800 }],
  });
  view();
  await screen.findByRole("button", { name: /movies.*Arr/ });
  fireEvent.change(screen.getByLabelText("Search"), {
    target: { value: "movies" },
  });
  expect(screen.getAllByText(/900/).length).toBeGreaterThan(0);
});
it("fetches the newly selected instance while an old overview is pending", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    arrs: [],
    qbitInstances: {
      "qBit-home": { alive: true, host: "home", port: 1, version: "5" },
      "qBit-remote": { alive: true, host: "remote", port: 2, version: "5" },
    },
  });
  let old!: (value: Awaited<ReturnType<typeof api.getQbitOverview>>) => void;
  vi.mocked(api.getQbitOverview).mockImplementation((instance) =>
    instance === "qBit-remote"
      ? Promise.resolve({
          ready: true,
          instances: [],
          categories: [
            {
              ...category(),
              category: "remote-only",
              qbitInstance: "qBit-remote",
            },
          ],
        })
      : new Promise((resolve) => {
          old = resolve;
        }),
  );
  const { rerender } = view();
  await waitFor(() => expect(api.getQbitOverview).toHaveBeenCalled());
  rerender(
    <ToastProvider>
      <WebUIProvider>
        <QbitCategoriesView active selectedInstance="qBit-remote" />
      </WebUIProvider>
    </ToastProvider>,
  );
  await screen.findByRole("button", { name: /remote-only/ });
  await act(async () =>
    old({ ready: true, instances: [], categories: [category()] }),
  );
  expect(
    screen.queryByRole("button", { name: /movies.*Arr/ }),
  ).not.toBeInTheDocument();
});

it("clears an old search and opens shared categories across all clients", async () => {
  vi.mocked(api.getStatus).mockResolvedValue({
    ready: true,
    arrs: [],
    qbitInstances: {
      "qBit-home": { alive: true, host: "fixture", port: 1, version: null },
      "qBit-remote": { alive: false, host: "fixture", port: 2, version: null },
    },
  });
  vi.mocked(api.getQbitOverview).mockImplementation(async (instance) => ({
    ready: true,
    instances: ["qBit-home", "qBit-remote"],
    categories: [
      { ...category(), qbitInstance: "qBit-home" },
      { ...category(), qbitInstance: "qBit-remote" },
    ].filter((cat) => !instance || cat.qbitInstance === instance),
  }));
  const node = (active: boolean, selectedInstance = "qBit-home") => (
    <ToastProvider>
      <WebUIProvider>
        <QbitCategoriesView
          active={active}
          selectedInstance={selectedInstance}
        />
      </WebUIProvider>
    </ToastProvider>
  );
  const result = render(node(true));
  await screen.findAllByRole("button", { name: /movies.*Arr/ });
  fireEvent.change(screen.getByLabelText("Search"), {
    target: { value: "not-a-match" },
  });
  result.rerender(node(false));
  sessionStorage.setItem("qbitrr:focusQbitCategory", "movies");
  sessionStorage.removeItem("torrentarr:focusQbitInstance");
  result.rerender(node(true, "aggregate"));
  await waitFor(() => expect(screen.getByLabelText("Search")).toHaveValue(""));
  expect(screen.queryByLabelText("Instance")).not.toBeInTheDocument();
  await waitFor(() => {
    const buttons = screen.getAllByRole("button", { name: /movies.*Arr/ });
    expect(buttons).toHaveLength(2);
    for (const button of buttons)
      expect(button).toHaveAttribute("aria-expanded", "true");
  });
});
