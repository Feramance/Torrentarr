import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FieldGroup } from "../../pages/config/configFieldComponents";
import { getArrFieldSets } from "../../pages/config/configFields";

vi.mock("../../context/WebUIContext", () => ({
  useWebUI: () => ({ theme: "dark" }),
}));

const mode = getArrFieldSets("Sonarr-TV").entryFields.find(
  (field) => field.path?.at(-1) === "SearchBySeries",
)!;

function Editor({
  initial,
  changed,
}: {
  initial: unknown;
  changed: (value: unknown) => void;
}) {
  const [value, setValue] = useState(initial);
  return (
    <FieldGroup
      title={null}
      fields={[mode]}
      basePath={[]}
      state={{ EntrySearch: { SearchBySeries: value } }}
      onChange={(_path, _field, next) => {
        setValue(next);
        changed(next);
      }}
    />
  );
}

describe("Sonarr search mode dropdown", () => {
  it.each([
    ["smart", "Smart"],
    ["series", "Series"],
    ["season", "Season"],
    ["episode", "Episode"],
    [true, "Series"],
    [false, "Episode"],
    ["true", "Series"],
    ["FALSE", "Episode"],
  ])("shows %s as %s", (initial, label) => {
    render(<Editor initial={initial} changed={vi.fn()} />);
    expect(screen.getByText(label)).toBeInTheDocument();
    expect(
      screen.getByRole("combobox", { name: "Search Mode" }),
    ).toBeInTheDocument();
  });

  it.each(["smart", "series", "season", "episode"])(
    "selects and submits canonical %s",
    async (value) => {
      const changed = vi.fn();
      render(<Editor initial="episode" changed={changed} />);
      await userEvent.click(
        screen.getByRole("combobox", { name: "Search Mode" }),
      );
      const labels = ["Smart", "Series", "Season", "Episode"];
      expect(
        screen.getAllByRole("option").map((option) => option.textContent),
      ).toEqual(labels);
      await userEvent.click(
        screen.getByRole("option", {
          name: value[0]!.toUpperCase() + value.slice(1),
        }),
      );
      expect(changed).toHaveBeenLastCalledWith(value);
    },
  );
});
