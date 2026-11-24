import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
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
  summary: "MoviesSearch · 42",
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

describe("HistoryView", () => {
  it("loads a preselected worker, filters by action type, and resets on repeated navigation", async () => {
    const { rerender } = render(
      <HistoryView active selection={{ worker: "Radarr-one" }} />,
    );
    expect(await screen.findByText("MoviesSearch · 42")).toBeInTheDocument();
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
    expect(screen.getByText("MoviesSearch · 42")).toBeInTheDocument();
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
    await userEvent.click(await screen.findByText("Movie updated"));
    expect(screen.getByText("Observed catalog change")).toBeInTheDocument();
    expect(screen.getByText("movie 42")).toBeInTheDocument();
    expect(screen.getByText(/"before": "Old"/)).toBeInTheDocument();
  });
});
