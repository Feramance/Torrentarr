import { expect, it } from "vitest";
import {
  getArrTorrentHandlingSummary,
  getQbitTorrentHandlingSummary,
} from "../../config/torrentHandlingSummary";
it("describes completion fractions as percentages", () => {
  const summary = getArrTorrentHandlingSummary({
    Torrent: { MaximumDeletablePercentage: 0.99 },
  });
  expect(summary).toContain("99% completion");
  expect(summary).not.toContain("0.99%");
});

it("explains mandatory completion-based protection without claiming legacy-off clearance", () => {
  const summary = getQbitTorrentHandlingSummary({
    CategorySeeding: { HitAndRunMode: "disabled" },
    Trackers: [{ Name: "OnlyEncodes", Protection: { Policy: "onlyencodes" } }],
  });
  expect(summary).toContain("48 mandatory completed seed hours");
  expect(summary).toContain("Ratio never replaces mandatory time");
  expect(summary).toContain("unknown private obligations are preserved");
  expect(summary).not.toContain("may be removed as soon as");
});

it("describes legacy tracker-message deletion as disabled", () => {
  const summary = getArrTorrentHandlingSummary({
    Torrent: {
      SeedingMode: {
        RemoveDeadTrackers: true,
        RemoveTrackerWithMessage: ["unregistered"],
      },
    },
  });
  expect(summary).toContain("Tracker messages cannot directly delete torrents");
  expect(summary).not.toContain("can trigger removal");
});
