import { useState } from "react";
import { describe, it, expect, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ConfigDocument } from "../../api/types";
import { PathMappingsEditor } from "../../pages/config/PathMappingsEditor";
import { getClientFields } from "../../pages/config/configFields";
import { validateFieldGroup } from "../../pages/config/configValidation";
import type { ValidationError } from "../../pages/config/configTypes";

vi.mock("../../context/WebUIContext", () => ({
  useWebUI: () => ({ theme: "dark" }),
}));

function Editor({ initial = [] }: { initial?: ConfigDocument[] }) {
  const field = getClientFields().find(
    (f) => f.path?.at(-1) === "PathMappings",
  )!;
  const [rows, setRows] = useState(initial);
  const errors: ValidationError[] = [];
  validateFieldGroup(
    errors,
    [field],
    { Maintenance: { PathMappings: rows } },
    ["qBit"],
    { root: {} },
  );
  return (
    <>
      <PathMappingsEditor
        field={field}
        value={rows}
        dataPath="qBit.Maintenance.PathMappings"
        validationErrors={errors}
        onChange={setRows}
      />
      <output aria-label="Saved shape">{JSON.stringify(rows)}</output>
    </>
  );
}
const result = () =>
  JSON.parse(screen.getByLabelText("Saved shape").textContent!);

describe("path mapping forms", () => {
  it("adds, edits and removes mappings while retaining unknown properties", async () => {
    const user = userEvent.setup();
    render(
      <Editor
        initial={[{ ClientPath: "/downloads", LocalPath: "/data", Future: 42 }]}
      />,
    );
    fireEvent.change(screen.getByRole("textbox", { name: "Torrentarr path" }), {
      target: { value: "/data/downloads" },
    });
    expect(result()[0]).toEqual({
      ClientPath: "/downloads",
      LocalPath: "/data/downloads",
      Future: 42,
    });
    expect(
      screen.getByText(
        /\/downloads\/movie\/file.mkv → \/data\/downloads\/movie\/file.mkv/,
      ),
    ).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Add path mapping" }));
    expect(result()).toHaveLength(2);
    await user.click(
      screen.getAllByRole("button", { name: "Remove path mapping" })[1],
    );
    expect(result()).toHaveLength(1);
  });

  it("reports indexed field paths for duplicate mappings", () => {
    const errors: ValidationError[] = [];
    validateFieldGroup(
      errors,
      getClientFields().filter((f) => f.type === "collection"),
      {
        Maintenance: {
          PathMappings: [
            { ClientPath: "/d", LocalPath: "/data" },
            { ClientPath: "/d/", LocalPath: "/data" },
          ],
        },
      },
      ["TorrentClients", "one"],
      { root: {} },
    );
    expect(errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          path: [
            "TorrentClients",
            "one",
            "Maintenance",
            "PathMappings",
            "1",
            "ClientPath",
          ],
        }),
      ]),
    );
  });

  it("exposes only path mappings as a maintenance collection", () => {
    expect(
      getClientFields()
        .filter((f) => f.type === "collection")
        .map((f) => f.path?.join(".")),
    ).toEqual(["Maintenance.PathMappings"]);
    render(<Editor />);
    expect(
      screen.queryByText(/Share policies|Notifications/i),
    ).not.toBeInTheDocument();
  });
});
