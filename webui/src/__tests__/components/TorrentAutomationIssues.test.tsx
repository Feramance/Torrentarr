import { expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { TorrentAutomationIssues } from "../../components/TorrentAutomationIssues";
import { getTorrentAutomationStatus } from "../../api/client";

vi.mock("../../api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../api/client")>()),
  getTorrentAutomationStatus: vi.fn(),
}));

it.each(["none", "critical"])(
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
        "1 issues",
      );
    }
  },
);
