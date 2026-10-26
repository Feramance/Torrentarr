import { useState } from "react";
import { describe, it, expect, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
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
    expect(
      screen.getByText("Path mappings").closest("details"),
    ).not.toHaveAttribute("open");
    await user.click(screen.getByText("Path mappings"));
    fireEvent.change(screen.getByRole("textbox", { name: "Torrentarr path" }), {
      target: { value: "/data/downloads" },
    });
    expect(result()[0]).toEqual({
      ClientPath: "/downloads",
      LocalPath: "/data/downloads",
      Future: 42,
    });
    expect(screen.queryByText(/movie\/file.mkv/)).not.toBeInTheDocument();
    expect(
      screen.getByRole("group", { name: "Path mapping 1" }),
    ).not.toHaveClass("config-section");
    await user.click(screen.getByRole("button", { name: "Add path mapping" }));
    expect(result()).toHaveLength(2);
    await user.click(
      screen.getAllByRole("button", { name: "Remove path mapping" })[1],
    );
    expect(result()).toHaveLength(1);
  });

  it("opens invalid mapping groups and keeps them open after fixing the row", () => {
    render(
      <Editor
        initial={[{ ClientPath: "relative", LocalPath: "/data", Future: 42 }]}
      />,
    );
    const details = screen.getByText("Path mappings").closest("details")!;
    expect(details).toHaveAttribute("open");
    const row = screen.getByRole("group", { name: "Path mapping 1" });
    expect(
      within(row).getByRole("textbox", { name: "Torrent client path" }),
    ).toHaveAttribute("aria-invalid", "true");
    fireEvent.change(
      within(row).getByRole("textbox", { name: "Torrent client path" }),
      { target: { value: "/downloads" } },
    );
    expect(details).toHaveAttribute("open");
    expect(result()[0].Future).toBe(42);
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
      ["TorrentClient", "one"],
      { root: {} },
    );
    expect(errors).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          path: [
            "TorrentClient",
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

  it("retains all maintenance capabilities in the schema", () => {
    const keys = getClientFields().map((field) => field.path?.join("."));
    for (const key of [
      "Maintenance.Enabled",
      "Maintenance.ErrorRecovery.MaxRepairAttempts",
      "Maintenance.Hardlinks.Tag",
      "Maintenance.CategoryMappings",
      "Maintenance.Operations.RepairPaused.ExecutionMode",
    ])
      expect(keys).toContain(key);
    expect(
      keys.some(
        (key) => key?.startsWith("Storage.") || key?.startsWith("Recovery."),
      ),
    ).toBe(false);
  });
});
