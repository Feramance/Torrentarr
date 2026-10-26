import {
  SonarrSeriesGroupDetailBody,
  type SonarrSeriesGroup,
} from "../../components/arr/SonarrSeriesGroupDetailBody";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PersistentDetails } from "../../components/PersistentDetails";

function Section({ defaultOpen = false, reveal = false }) {
  return (
    <PersistentDetails defaultOpen={defaultOpen} reveal={reveal}>
      <summary>Section</summary>
      <input aria-label="Setting" defaultValue="value" />
    </PersistentDetails>
  );
}
const details = () => screen.getByText("Section").closest("details")!;

describe("persistent disclosure", () => {
  it("keeps a validation-opened section open when its last error clears", async () => {
    const { rerender } = render(<Section reveal />);
    expect(details().open).toBe(true);
    const input = screen.getByLabelText("Setting");
    input.focus();
    rerender(<Section reveal={false} />);
    expect(details().open).toBe(true);
    expect(input).toHaveFocus();
    await userEvent.type(input, " edited");
    expect(input).toHaveValue("value edited");
    expect(details().open).toBe(true);
  });
  it("does not close an expanded catalog section when its group count grows", () => {
    const { rerender } = render(<Section defaultOpen />);
    rerender(<Section defaultOpen={false} />);
    expect(details().open).toBe(true);
  });
  it("does not reopen a manually collapsed catalog section when its group count shrinks", async () => {
    const { rerender } = render(<Section />);
    await userEvent.click(screen.getByText("Section"));
    await userEvent.click(screen.getByText("Section"));
    expect(details().open).toBe(false);
    rerender(<Section defaultOpen />);
    expect(details().open).toBe(false);
  });
  it("opens a manually collapsed section when a new validation error appears", async () => {
    const { rerender } = render(<Section reveal />);
    await userEvent.click(screen.getByText("Section"));
    expect(details().open).toBe(false);
    rerender(<Section />);
    expect(details().open).toBe(false);
    rerender(<Section reveal />);
    expect(details().open).toBe(true);
  });
});

it("preserves existing Sonarr season expansion when a fourth season arrives", () => {
  const group = (count: number): SonarrSeriesGroup => ({
    instance: "Sonarr",
    series: "Example",
    episodes: Array.from({ length: count }, (_, i) => ({
      __instance: "Sonarr",
      series: "Example",
      season: i + 1,
      episode: 1,
      title: "Example",
      monitored: true,
      hasFile: true,
      airDate: "",
    })),
  });
  const { rerender } = render(
    <SonarrSeriesGroupDetailBody group={group(3)} category="series" />,
  );
  const season = screen.getByText("Season 1 (1 episodes)").closest("details")!;
  expect(season.open).toBe(true);
  rerender(<SonarrSeriesGroupDetailBody group={group(4)} category="series" />);
  expect(season.open).toBe(true);
  expect(
    screen.getByText("Season 4 (1 episodes)").closest("details")!.open,
  ).toBe(false);
});
