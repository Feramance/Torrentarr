import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { getSearchDecisions } from "../../api/client";
import { SearchDecisionDetails } from "../../components/SearchDecisionDetails";

vi.mock("../../api/client", () => ({
  getSearchDecisions: vi.fn(),
  getRadarrOpenMovieUrl: () => "/open/movie/2",
}));

it("shows ordered preview and separate Arr evidence for a worker", async () => {
  const item = {
    id: 2,
    type: "Movie",
    seriesId: null,
    season: null,
    artistId: null,
    authorId: null,
    reason: "Missing",
    scope: "Movie",
  };
  vi.mocked(getSearchDecisions).mockResolvedValueOnce({
    preview: {
      observedAt: "2026-11-17T10:00:00Z",
      ageSeconds: 120,
      candidateCount: 1,
      items: [item],
      deferred: [],
    },
    history: [
      {
        item: { ...item, reason: "Upgrade" },
        outcome: "submitted",
        cycleId: "1".repeat(32),
        occurrences: 1,
        observedAt: "2026-11-17T10:01:00Z",
        completedAt: "2026-11-17T10:02:00Z",
        grabbedAt: "2026-11-17T10:02:30Z",
      },
      {
        item: { ...item, id: 3 },
        outcome: "submitted",
        cycleId: "2".repeat(32),
        occurrences: 1,
        observedAt: "2026-11-17T09:00:00Z",
        completedAt: "2026-11-17T09:01:00Z",
        grabbedAt: null,
        noGrabAt: "2026-11-17T09:31:00Z",
      },
    ],
  });
  const { container } = render(
    <SearchDecisionDetails instance="Radarr-4K" category="radarr-4k" />,
  );
  container.querySelector("details")!.open = true;
  fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
  expect(await screen.findByText(/2 min old when loaded/)).toBeInTheDocument();
  expect(screen.getAllByText(/Completion confirmed/)).toHaveLength(2);
  expect(screen.getByText(/Grab observed for this item/)).toBeInTheDocument();
  expect(screen.getByText(/No grab observed through/)).toBeInTheDocument();
  expect(screen.getAllByRole("link", { name: "Movie 2" })[0]).toHaveAttribute(
    "href",
    "/open/movie/2",
  );
  expect(getSearchDecisions).toHaveBeenCalledWith("Radarr-4K");
});
