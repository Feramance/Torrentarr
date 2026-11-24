import { expect, it } from "vitest";
import {
  getArrTorrentHandlingSummary,
  getQbitTorrentHandlingSummary,
  getTrackerSummary,
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
  const tracker = getTrackerSummary({
    Name: "OnlyEncodes",
    Protection: { Policy: "onlyencodes" },
  });
  expect(tracker).toContain("48 mandatory completed seed hours");
  expect(tracker).toContain("Ratio never replaces mandatory time");
  expect(summary).not.toContain("OnlyEncodes");
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

it("describes observation confirmation of legacy OR clearance", () => {
  const summary = getTrackerSummary({
    HitAndRunMode: "or",
    MinSeedRatio: 1,
    MinSeedingTimeDays: 8,
    Protection: { RequireLegacyClearance: true, MinimumSeedHours: 192 },
  });
  expect(summary).toContain("ratio 1 or 8 days of seeding");
  expect(summary).toContain("60 minutes of accounting grace");
  expect(summary).not.toContain("mandatory completed seed hours");
});

it("shows retirement requirements with inherited and explicitly disabled settings", () => {
  const summary = getQbitTorrentHandlingSummary({
    CategorySeeding: {
      MinimumReportedSeeders: 3,
      MinimumUploadInactivitySeconds: 3600,
      Categories: [{ Name: "movies", MinimumUploadInactivitySeconds: 0 }],
    },
  });
  expect(summary).toContain("at least 3 reported seeders and");
  expect(summary).toContain("verified upload inactivity");
  expect(summary).toContain(
    "For movies, automatic retirement also requires at least 3 reported seeders.",
  );
});
