import { describe, expect, it } from "vitest";
import {
  getArrTorrentHandlingSummary,
  getQbitTorrentHandlingSummary,
} from "../../config/torrentHandlingSummary";

describe("torrentHandlingSummary", () => {
  it("includes queue sorting line for Arr tracker when SortTorrents is enabled", () => {
    const summary = getArrTorrentHandlingSummary({
      Torrent: {
        Trackers: [
          {
            Name: "Private",
            HitAndRunMode: "disabled",
            SortTorrents: true,
          },
        ],
      },
    } as never);

    expect(summary).toContain("Queue sorting is enabled for this tracker.");
  });

  it("does not include queue sorting line when SortTorrents is absent", () => {
    const summary = getQbitTorrentHandlingSummary({
      Trackers: [
        {
          Name: "Private",
          HitAndRunMode: "disabled",
        },
      ],
    } as never);

    expect(summary).not.toContain("Queue sorting is enabled for this tracker.");
  });
});
