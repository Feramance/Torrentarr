import { useState } from "react";
import { describe, it, expect, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ConfigDocument } from "../../api/types";
import { ConfigCollectionEditor } from "../../pages/config/ConfigCollectionEditor";
import { getClientFields } from "../../pages/config/configFields";
import { focusFirstValidationError } from "../../pages/config/configDocumentUtils";
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
      <ConfigCollectionEditor
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
    await user.click(screen.getByText("/downloads → /data", { exact: true }));
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
      within(screen.getByRole("group", { name: "Path mapping 2" })).getByRole(
        "button",
        { name: "Remove path mapping" },
      ),
    );
    expect(result()).toHaveLength(1);
  });

  it("opens a collapsed invalid mapping and focuses the affected field", () => {
    render(
      <Editor
        initial={[{ ClientPath: "relative", LocalPath: "/data", Future: 42 }]}
      />,
    );
    const row = screen.getByRole("group", { name: "Path mapping 1" });
    expect(within(row).getByText("Needs attention")).toBeVisible();
    expect(
      within(row).getByRole("textbox", { name: "Torrent client path" }),
    ).not.toBeVisible();
    focusFirstValidationError([
      {
        path: ["qBit", "Maintenance", "PathMappings", "0", "ClientPath"],
        message: "",
      },
    ]);
    expect(
      within(row).getByRole("textbox", { name: "Torrent client path" }),
    ).toHaveFocus();
    expect(
      within(row).getByRole("textbox", { name: "Torrent client path" }),
    ).toHaveAttribute("aria-invalid", "true");
    fireEvent.change(
      within(row).getByRole("textbox", { name: "Torrent client path" }),
      { target: { value: "/downloads" } },
    );
    expect(result()[0].Future).toBe(42);
  });

  it("defers required messages on a fresh row until blur or Done", async () => {
    const user = userEvent.setup();
    render(<Editor />);
    await user.click(screen.getByRole("button", { name: "Add path mapping" }));
    const clientPath = screen.getByRole("textbox", {
      name: "Torrent client path",
    });
    const localPath = screen.getByRole("textbox", { name: "Torrentarr path" });
    expect(clientPath).toHaveFocus();
    expect(clientPath).not.toHaveAttribute("aria-invalid");
    await user.tab();
    expect(clientPath).toHaveAttribute("aria-invalid", "true");
    expect(localPath).not.toHaveAttribute("aria-invalid");
    await user.click(screen.getByRole("button", { name: "Done" }));
    await user.click(screen.getByText("Path mapping 1", { exact: true }));
    expect(localPath).toHaveAttribute("aria-invalid", "true");
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
