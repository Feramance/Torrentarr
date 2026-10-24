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
  view();
  await waitFor(() => expect(api.getQbitOverview).toHaveBeenCalled());
  fireEvent.change(screen.getByLabelText("Instance"), {
    target: { value: "qBit-remote" },
  });
  await screen.findByRole("button", { name: /remote-only/ });
  await act(async () =>
    old({ ready: true, instances: [], categories: [category()] }),
  );
  expect(
    screen.queryByRole("button", { name: /movies.*Arr/ }),
  ).not.toBeInTheDocument();
});
