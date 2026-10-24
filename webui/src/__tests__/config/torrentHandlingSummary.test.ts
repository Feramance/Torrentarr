import { expect, it } from "vitest";
import { getArrTorrentHandlingSummary } from "../../config/torrentHandlingSummary";
it("describes completion fractions as percentages", () => {
  const summary = getArrTorrentHandlingSummary({
    Torrent: { MaximumDeletablePercentage: 0.99 },
  });
  expect(summary).toContain("99% completion");
  expect(summary).not.toContain("0.99%");
});
