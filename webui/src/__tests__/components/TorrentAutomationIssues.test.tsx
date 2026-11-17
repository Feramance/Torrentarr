import { expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { TorrentAutomationIssues } from "../../components/TorrentAutomationIssues";
import { getTorrentAutomationStatus } from "../../api/client";

vi.mock("../../api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../api/client")>()),
  getTorrentAutomationStatus: vi.fn(),
}));

it("shows handled tracker rejections without counting them as issues", async () => {
  vi.mocked(getTorrentAutomationStatus).mockResolvedValue({
    protection: [],
    recovery: [],
    inspections: [],
    trackerIssues: [
      {
        clientInstanceId: "client",
        torrentHash: "managed",
        torrentName: "Managed torrent",
        reason: "Automatic cleanup is waiting for the configured grace period.",
        requiresAttention: false,
      },
      {
        clientInstanceId: "client",
        torrentHash: "unmanaged",
        torrentName: "Unmanaged torrent",
        reason: "Tracker rejection needs attention.",
      },
    ],
  });
  render(<TorrentAutomationIssues clientId="client" />);
  await screen.findByText("Managed torrent");
  expect(screen.getByText("Unmanaged torrent")).toBeInTheDocument();
  expect(screen.getByText(/Torrent automation/)).toHaveTextContent("1 issues");
});

it.each(["none", "unknown-eta", "critical"])(
  "reports actual deadline risk (%s) while respecting an ordinary user pause",
  async (deadlineRisk) => {
    vi.mocked(getTorrentAutomationStatus).mockResolvedValue({
      protection: [
        {
          clientInstanceId: "client",
          torrentHash: "abc",
          torrentName: "Example",
          tracker: "tracker.example",
          state: "protected",
          reason: "User/unknown pause ownership blocks automatic resume.",
          deadlineRisk,
          nativeStopLimitRisk: false,
          observedSeedSeconds: 0,
          remainingSeedSeconds: 3600,
          localEstimate: true,
        },
      ],
      recovery: [],
      inspections: [],
    });
    render(<TorrentAutomationIssues clientId="client" />);
    if (deadlineRisk === "none") {
      await screen.findByText(/No reported recovery or protection issues/);
      expect(screen.getByText(/Torrent automation/)).toHaveTextContent(
        "0 issues",
      );
    } else {
      await screen.findByText("Example");
      expect(screen.getByText(/Torrent automation/)).toHaveTextContent(
        deadlineRisk === "critical" ? "1 issues" : "0 issues",
      );
    }
  },
);

it.each([
  ["tracker-accounting-unconfirmed", "Tracker accounting unconfirmed"],
  ["no-tracker-reported", "No tracker reported"],
  ["no-matching-tracker-rule", "No matching tracker rule"],
  [undefined, undefined],
] as const)(
  "shows the protection explanation for %s",
  async (unknownReason, label) => {
    vi.mocked(getTorrentAutomationStatus).mockResolvedValue({
      protection: [
        {
          clientInstanceId: "client",
          torrentHash: "abc",
          torrentName: "Example",
          tracker: "tracker.example",
          state: "unknown",
          unknownReason,
          reason: "Protection evidence retained.",
          deadlineRisk: "none",
          observedSeedSeconds: 0,
          remainingSeedSeconds: 0,
          localEstimate: true,
        },
      ],
      recovery: [],
      inspections: [],
    });
    render(<TorrentAutomationIssues clientId="client" />);
    await screen.findByText(/Protection evidence retained/);
    if (label) expect(screen.getByText(label)).toBeInTheDocument();
    expect(screen.getByText(/Torrent automation/)).toHaveTextContent(
      "0 issues",
    );
  },
);
