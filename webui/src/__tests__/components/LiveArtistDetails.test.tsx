import { act, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { LidarrArtistDetailBody } from "../../components/arr/LidarrArtistDetailBody";
import { STATISTICS_UPDATED } from "../../hooks/useStatisticsUpdates";
import * as api from "../../api/client";

vi.mock("../../api/client", async (original) => ({
  ...(await original<typeof import("../../api/client")>()),
  getLidarrArtistDetail: vi.fn(),
}));

it("refreshes open artist statistics on database changes and retains them on failure", async () => {
  vi.mocked(api.getLidarrArtistDetail)
    .mockResolvedValueOnce({
      category: "music",
      counts: { available: 0, monitored: 0 },
      artist: { id: 1, name: "Artist", albumCount: 7 },
      albums: [],
    })
    .mockResolvedValueOnce({
      category: "music",
      counts: { available: 0, monitored: 0 },
      artist: { id: 1, name: "Artist", albumCount: 9 },
      albums: [],
    })
    .mockRejectedValueOnce(new Error("Temporarily offline"));
  render(<LidarrArtistDetailBody category="music" artistId={1} />);
  await screen.findByText("7");
  act(() => window.dispatchEvent(new Event(STATISTICS_UPDATED)));
  await screen.findByText("9");
  act(() => window.dispatchEvent(new Event(STATISTICS_UPDATED)));
  await screen.findByText(/Temporarily offline/);
  expect(screen.getByText("9")).toBeInTheDocument();
  expect(screen.getByText("Artist", { selector: "dd" })).toBeInTheDocument();
});
