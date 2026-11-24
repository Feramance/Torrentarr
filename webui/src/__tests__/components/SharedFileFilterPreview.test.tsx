import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { getFileFilterPreview } from "../../api/client";
import { SharedFileFilterPreview } from "../../components/SharedFileFilterPreview";

vi.mock("../../api/client", () => ({ getFileFilterPreview: vi.fn() }));

it("shows saved matches, source freshness, and last-valid errors", async () => {
  vi.mocked(getFileFilterPreview).mockResolvedValueOnce({
    available: true,
    reason: "Shared list 'reviewed' matched.",
    lists: [
      {
        name: "reviewed",
        source: "filters/reviewed.txt",
        loadedAt: "2026-11-17T10:00:00Z",
        checkedAt: "2026-11-17T10:05:00Z",
        error: "List could not be read.",
      },
    ],
  });
  render(<SharedFileFilterPreview instance="Sonarr-TV" />);
  fireEvent.change(screen.getByRole("textbox"), {
    target: { value: "Movie/extra.exe" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Check saved filters" }));
  expect(
    await screen.findByText("Shared list 'reviewed' matched."),
  ).toBeInTheDocument();
  expect(
    screen.getByText(
      /filters\/reviewed.txt.*Loaded.*Checked.*List could not be read/,
    ),
  ).toBeInTheDocument();
  expect(getFileFilterPreview).toHaveBeenCalledWith(
    "Sonarr-TV",
    "Movie/extra.exe",
  );
});

it("shows unavailable lists as a processing hold", async () => {
  vi.mocked(getFileFilterPreview).mockResolvedValueOnce({
    available: false,
    reason: null,
    lists: [],
  });
  render(<SharedFileFilterPreview instance="Sonarr-TV" />);
  fireEvent.click(screen.getByRole("button", { name: "Check saved filters" }));
  expect(
    await screen.findByText("Processing is waiting for a usable shared list."),
  ).toBeInTheDocument();
});
