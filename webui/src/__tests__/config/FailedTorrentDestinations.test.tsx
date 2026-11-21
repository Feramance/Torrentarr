import { fireEvent, render, screen } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { getFailedTorrentDestinations } from "../../api/client";
import { FailedTorrentDestinations } from "../../pages/config/FailedTorrentDestinations";

vi.mock("../../api/client", () => ({ getFailedTorrentDestinations: vi.fn() }));

it("offers independent destinations and identifies Arr-managed categories", async () => {
  vi.mocked(getFailedTorrentDestinations).mockResolvedValue([
    {
      client: "local",
      available: true,
      categories: [
        { name: "parked", arrOwner: null, unavailableReason: null },
        { name: "tv", arrOwner: "Sonarr", unavailableReason: "Arr managed" },
      ],
    },
    { client: "seedbox", available: false, categories: [] },
  ]);
  const onChange = vi.fn();
  render(
    <FailedTorrentDestinations
      category="movies"
      value={[{ Client: "seedbox", Category: "other" }]}
      onChange={onChange}
    />,
  );
  const local = await screen.findByRole("combobox", {
    name: "Destination on local",
  });
  expect(
    screen.getByRole("option", { name: "tv — Arr managed by Sonarr" }),
  ).toBeDisabled();
  expect(
    screen.getByRole("combobox", { name: "Destination on seedbox" }),
  ).toBeDisabled();
  fireEvent.change(local, { target: { value: "parked" } });
  expect(onChange).toHaveBeenCalledWith([
    { Client: "seedbox", Category: "other" },
    { Client: "local", Category: "parked" },
  ]);
});
