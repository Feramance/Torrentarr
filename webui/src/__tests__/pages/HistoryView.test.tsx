import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { HistoryView } from "../../pages/HistoryView";
import { getWorkerHistory } from "../../api/client";
import type {
  WorkerHistoryEvent,
  WorkerHistoryResponse,
} from "../../api/types";

vi.mock("../../api/client", () => ({ getWorkerHistory: vi.fn() }));
vi.mock("../../components/SearchDecisionDetails", () => ({
  SearchDecisionDetails: ({ instance }: { instance: string }) => (
    <div>Search details for {instance}</div>
  ),
}));
const getHistory = vi.mocked(getWorkerHistory);
const entry: WorkerHistoryEvent = {
  id: 3,
  timestamp: "2026-11-24T12:00:00Z",
  instance: "Radarr-one",
  loop: "search",
  type: "search",
  source: "worker",
  subjectType: "movie",
  subjectId: "42",
  summary: "Example movie",
  outcome: "submitted",
  details: "{}",
  clientInstance: "",
  commandId: 9,
  completedAt: null,
};
const response: WorkerHistoryResponse = {
  entries: [entry],
  workers: ["Radarr-one", "Sonarr-two", "Removed-worker"],
  types: ["search", "import"],
  nextCursor: null,
};
beforeEach(() => {
  getHistory.mockReset();
  getHistory.mockResolvedValue(response);
});

afterEach(() => vi.restoreAllMocks());

describe("HistoryView", () => {
  it("loads a preselected worker, filters by action type, and resets on repeated navigation", async () => {
    const { rerender } = render(
      <HistoryView active selection={{ worker: "Radarr-one" }} />,
    );
    expect(await screen.findByText("Example movie")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Worker" })).toHaveValue(
      "Radarr-one",
    );
    expect(
      screen.getByText("Search details for Radarr-one"),
    ).toBeInTheDocument();
    await userEvent.selectOptions(
      screen.getByRole("combobox", { name: "Action type" }),
      "import",
    );
    await waitFor(() =>
      expect(getHistory).toHaveBeenLastCalledWith("Radarr-one", "import"),
    );
    expect(
      screen.queryByText("Search details for Radarr-one"),
    ).not.toBeInTheDocument();
    rerender(<HistoryView active selection={{ worker: "Sonarr-two" }} />);
    await waitFor(() =>
      expect(getHistory).toHaveBeenLastCalledWith("Sonarr-two", ""),
    );
    expect(screen.getByRole("combobox", { name: "Action type" })).toHaveValue(
      "",
    );
  });

  it("appends older entries and includes workers removed from configuration", async () => {
    getHistory
      .mockResolvedValueOnce({ ...response, nextCursor: 3 })
      .mockResolvedValueOnce({
        ...response,
        entries: [{ ...entry, id: 2, summary: "Older search" }],
      });
    render(<HistoryView active />);
    await userEvent.click(
      await screen.findByRole("button", { name: "Load older entries" }),
    );
    expect(await screen.findByText("Older search")).toBeInTheDocument();
    expect(screen.getByText("Example movie")).toBeInTheDocument();
    expect(
      screen.getByRole("option", { name: "Removed-worker" }),
    ).toBeInTheDocument();
    expect(getHistory).toHaveBeenLastCalledWith("", "", 3);
  });

  it("shows retry and distinguishes an empty history from no filter matches", async () => {
    getHistory.mockRejectedValueOnce(new Error("offline"));
    render(<HistoryView active />);
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "History could not be loaded",
    );
    getHistory.mockResolvedValueOnce({ ...response, entries: [] });
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(
      await screen.findByText("No worker history has been recorded yet."),
    ).toBeInTheDocument();
    getHistory.mockResolvedValueOnce({ ...response, entries: [] });
    await userEvent.selectOptions(
      screen.getByRole("combobox", { name: "Worker" }),
      "Removed-worker",
    );
    expect(
      await screen.findByText("No history matches these filters."),
    ).toBeInTheDocument();
  });

  it("labels observed changes and exposes identifiers in row details", async () => {
    getHistory.mockResolvedValue({
      ...response,
      entries: [
        {
          ...entry,
          type: "update",
          source: "observed",
          summary: "Movie updated",
          details: '{"Title":{"before":"Old","after":"New"}}',
        },
      ],
    });
    render(<HistoryView active />);
    await screen.findByText("Movie updated");
    await userEvent.click(
      screen.getByRole("button", { name: "Details", exact: true }),
    );
    expect(screen.getByText("Observed catalog change")).toBeInTheDocument();
    const panel = screen.getByRole("region", {
      name: "Details for Movie updated",
    });
    expect(within(panel).getByText("42")).toBeInTheDocument();
    expect(
      within(panel).getByRole("row", { name: "Title Old New" }),
    ).toBeInTheDocument();
    expect(panel.querySelector("pre")).not.toBeInTheDocument();
  });
});

function ticker() {
  let tick = () => {};
  vi.spyOn(window, "setInterval").mockImplementation((callback, delay) => {
    if (delay === 3000) tick = callback as () => void;
    return 123456;
  });
  return () => tick();
}

it("loads new actions live, drains multi-page bursts, preserves older rows and open details, and updates outcomes", async () => {
  const tick = ticker();
  const row = (id: number) => ({ ...entry, id, summary: `Search ${id}` });
  getHistory
    .mockResolvedValueOnce({ ...response, entries: [row(3)], nextCursor: 3 })
    .mockResolvedValueOnce({ ...response, entries: [row(2)], nextCursor: 2 })
    .mockResolvedValueOnce({
      ...response,
      entries: [row(8), row(7)],
      nextCursor: 7,
    })
    .mockResolvedValueOnce({
      ...response,
      entries: [row(6), row(5)],
      nextCursor: 5,
    })
    .mockResolvedValueOnce({
      ...response,
      entries: [row(4), { ...row(3), outcome: "completed" }],
      nextCursor: 3,
    })
    .mockResolvedValueOnce({ ...response, entries: [row(1)] });
  render(<HistoryView active />);
  const originalRow = (await screen.findByText("Search 3")).closest("tr")!;
  const expanded = within(originalRow).getByRole("button", {
    name: "Details",
    exact: true,
  });
  await userEvent.click(expanded);
  expect(expanded).toHaveAttribute("aria-expanded", "true");
  await userEvent.click(
    screen.getByRole("button", { name: "Load older entries" }),
  );
  expect(await screen.findByText("Search 2")).toBeInTheDocument();
  await act(async () => tick());
  expect(await screen.findByText("Search 8")).toBeInTheDocument();
  expect(screen.getByText("Search 4")).toBeInTheDocument();
  expect(screen.getByText("Search 2")).toBeInTheDocument();
  const preservedRow = screen
    .getByText("Search 3", { selector: ".history-summary-text" })
    .closest("tr")!;
  expect(
    within(preservedRow).getByRole("button", { name: "Details", exact: true }),
  ).toBe(expanded);
  expect(expanded).toHaveAttribute("aria-expanded", "true");
  expect(
    screen.getByRole("region", { name: "Details for Search 3" }),
  ).toBeInTheDocument();
  expect(within(preservedRow).getByText("completed")).toBeInTheDocument();
  expect(screen.getByRole("status")).toHaveTextContent("5 new actions");
  const summaries = [...document.querySelectorAll(".history-summary-text")].map(
    (node) => node.textContent,
  );
  expect(summaries).toEqual([
    "Search 8",
    "Search 7",
    "Search 6",
    "Search 5",
    "Search 4",
    "Search 3",
    "Search 2",
  ]);
  await userEvent.click(
    screen.getByRole("button", { name: "Load older entries" }),
  );
  expect(await screen.findByText("Search 1")).toBeInTheDocument();
  expect(getHistory).toHaveBeenLastCalledWith("", "", 2);
});

it("pauses live requests when hidden, inactive, or disabled and catches up on visibility", async () => {
  const tick = ticker();
  const hidden = vi.spyOn(document, "hidden", "get").mockReturnValue(false);
  const { rerender } = render(<HistoryView active />);
  await screen.findByText(entry.summary);
  hidden.mockReturnValue(true);
  await act(async () => tick());
  expect(getHistory).toHaveBeenCalledTimes(1);
  hidden.mockReturnValue(false);
  getHistory.mockResolvedValue({
    ...response,
    entries: [{ ...entry, id: 4, summary: "New after visibility" }, entry],
  });
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
  expect(await screen.findByText("New after visibility")).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Live", exact: true }),
  );
  const calls = getHistory.mock.calls.length;
  await act(async () => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
  expect(getHistory).toHaveBeenCalledTimes(calls);
  expect(screen.getByText("Live paused")).toBeInTheDocument();
  await userEvent.click(
    screen.getByRole("checkbox", { name: "Live", exact: true }),
  );
  rerender(<HistoryView active={false} />);
  await act(async () => tick());
  expect(getHistory).toHaveBeenCalledTimes(calls);
});

it("keeps rows on a failed live request and recovers automatically without duplicates", async () => {
  const tick = ticker();
  getHistory
    .mockResolvedValueOnce(response)
    .mockRejectedValueOnce(new Error("offline"));
  render(<HistoryView active />);
  await screen.findByText(entry.summary);
  await act(async () => tick());
  expect(screen.getByText(entry.summary)).toBeInTheDocument();
  expect(screen.getByText(/Live updates interrupted/)).toBeInTheDocument();
  getHistory.mockResolvedValue(response);
  await act(async () => tick());
  expect(
    screen.queryByText(/Live updates interrupted/),
  ).not.toBeInTheDocument();
  expect(screen.getAllByText(entry.summary)).toHaveLength(1);
});

it("ignores a live response from a previous worker filter", async () => {
  const tick = ticker();
  let resolveOld!: (value: WorkerHistoryResponse) => void;
  getHistory.mockResolvedValueOnce(response).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolveOld = resolve;
      }),
  );
  render(<HistoryView active />);
  await screen.findByText(entry.summary);
  await act(async () => tick());
  getHistory.mockResolvedValueOnce({
    ...response,
    entries: [{ ...entry, instance: "Sonarr-two", summary: "Sonarr action" }],
  });
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Worker" }),
    "Sonarr-two",
  );
  expect(await screen.findByText("Sonarr action")).toBeInTheDocument();
  await act(async () =>
    resolveOld({
      ...response,
      entries: [{ ...entry, id: 99, summary: "Stale worker action" }],
    }),
  );
  expect(screen.queryByText("Stale worker action")).not.toBeInTheDocument();
});

it("uses readable columns and a far-right Details button with structured changes below the row", async () => {
  getHistory.mockResolvedValue({
    ...response,
    entries: [
      {
        ...entry,
        type: "update",
        outcome: "failed",
        clientInstance: "seedbox",
        source: "observed",
        details:
          '{"reason":"Quality upgrade","Monitored":{"before":false,"after":true}}',
      },
    ],
  });
  render(
    <div data-density="compact">
      <HistoryView active />
    </div>,
  );
  const summary = await screen.findByText(entry.summary);
  const row = summary.closest("tr")!;
  expect(
    screen
      .getAllByRole("columnheader")
      .map((header) => header.getAttribute("aria-label") ?? header.textContent),
  ).toEqual([
    "Time",
    "Worker",
    "Action",
    "Item / activity",
    "Context",
    "Outcome",
    "Row actions",
  ]);
  expect(
    within(row).getByText(/Movie #42.*Quality upgrade.*Command #9/),
  ).toBeVisible();
  expect(within(row).getByText("seedbox")).toBeVisible();
  expect(within(row).getByText("Catalog change")).toBeVisible();
  const button = within(row.lastElementChild as HTMLElement).getByRole(
    "button",
    { name: "Details", exact: true },
  );
  expect(button).toHaveAttribute("aria-expanded", "false");
  expect(screen.queryByText("Previous value")).not.toBeInTheDocument();
  await userEvent.click(button);
  expect(button).toHaveAttribute("aria-expanded", "true");
  const panel = screen.getByRole("region", {
    name: `Details for ${entry.summary}`,
  });
  expect(document.getElementById(button.getAttribute("aria-controls")!)).toBe(
    panel,
  );
  expect(
    within(panel).getByRole("heading", { name: "Event" }),
  ).toBeInTheDocument();
  expect(
    within(panel).getByRole("heading", { name: "Subject & identifiers" }),
  ).toBeInTheDocument();
  expect(
    within(panel).getByRole("heading", { name: "Parameters" }),
  ).toBeInTheDocument();
  expect(
    within(panel).getByRole("row", { name: "Monitored No Yes" }),
  ).toBeVisible();
  expect(panel.closest("tr")).toBe(row.nextElementSibling);
  expect(panel.closest("td")).toHaveAttribute("colspan", "7");
  expect(panel.querySelector("pre")).toBeNull();
  expect(row).toHaveClass("history-row--change");
  expect(within(row).getByText("failed")).toHaveClass(
    "history-outcome--failed",
  );
  await userEvent.click(button);
  expect(
    screen.queryByRole("region", { name: `Details for ${entry.summary}` }),
  ).not.toBeInTheDocument();
});

it("names command actions specifically and never labels them Other", async () => {
  getHistory.mockResolvedValue({
    ...response,
    entries: [
      { ...entry, type: "command", summary: "RssSync" },
      {
        ...entry,
        id: 4,
        type: "command",
        summary: "RefreshMonitoredDownloads",
      },
    ],
  });
  render(<HistoryView active />);
  await screen.findByText("RSS sync");
  const actions = [...document.querySelectorAll(".history-action-badge")].map(
    (node) => node.textContent,
  );
  expect(actions).toEqual(["RSS sync", "Refresh downloads"]);
  expect(screen.queryByText(/Other/)).not.toBeInTheDocument();
});

it("renders profile changes, lists and nested parameters as structured data", async () => {
  getHistory.mockResolvedValue({
    ...response,
    entries: [
      {
        ...entry,
        type: "temp-profile",
        details:
          '{"previousProfileId":1,"qualityProfileId":2,"movieIds":[42,43],"options":{"enabled":true}}',
      },
    ],
  });
  render(<HistoryView active />);
  await screen.findByText(entry.summary);
  await userEvent.click(
    screen.getByRole("button", { name: "Details", exact: true }),
  );
  const panel = screen.getByRole("region", {
    name: `Details for ${entry.summary}`,
  });
  expect(
    within(panel).getByRole("row", { name: "Quality profile 1 2" }),
  ).toBeVisible();
  expect(within(panel).getByText("Movie IDs")).toBeVisible();
  expect(within(panel).getByText("43")).toBeVisible();
  expect(within(panel).getByText("Enabled")).toBeVisible();
  expect(within(panel).getByText("Yes")).toBeVisible();
  expect(panel.querySelector("pre")).toBeNull();
});

it("automatically recovers an initial loading failure without fetching all retained history", async () => {
  const tick = ticker();
  getHistory
    .mockRejectedValueOnce(new Error("offline"))
    .mockResolvedValueOnce({ ...response, nextCursor: 3 });
  render(<HistoryView active />);
  await screen.findByRole("alert");
  await act(async () => tick());
  expect(await screen.findByText(entry.summary)).toBeInTheDocument();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  expect(getHistory).toHaveBeenCalledTimes(2);
  expect(
    screen.getByRole("button", { name: "Load older entries" }),
  ).toBeInTheDocument();
});

it("does not overlap live requests and keeps existing rows visible during loading", async () => {
  const tick = ticker();
  let resolvePending!: (value: WorkerHistoryResponse) => void;
  getHistory.mockResolvedValueOnce(response).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        resolvePending = resolve;
      }),
  );
  render(<HistoryView active />);
  await screen.findByText(entry.summary);
  await act(async () => tick());
  await act(async () => tick());
  expect(getHistory).toHaveBeenCalledTimes(2);
  expect(screen.getByText(entry.summary)).toBeInTheDocument();
  await act(async () => resolvePending(response));
  expect(screen.getAllByText(entry.summary)).toHaveLength(1);
});
