import { QbitInstanceModal } from "../../pages/config/configModals";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { FieldGroup } from "../../pages/config/configFieldComponents";
import type { FieldDefinition } from "../../pages/config/configTypes";

vi.mock("../../context/WebUIContext", () => ({
  useWebUI: () => ({ theme: "dark" }),
}));

const field: FieldDefinition = {
  path: ["Maintenance", "Operations", "ErrorRecovery", "Schedule"],
  label: "Operation schedule",
  type: "text",
};
function Editor({ initial = "*/5 * * * *", defaultOpen = false }) {
  const [schedule, setSchedule] = useState(initial);
  return (
    <FieldGroup
      title="Operation policies"
      fields={[field]}
      basePath={[]}
      sectionKey="qBit"
      defaultOpen={defaultOpen}
      state={{
        Maintenance: { Operations: { ErrorRecovery: { Schedule: schedule } } },
      }}
      validationErrors={
        schedule === "invalid"
          ? [
              {
                path: ["qBit", ...field.path!],
                message: "Invalid cron",
              },
            ]
          : []
      }
      onChange={(_path, _field, value) => setSchedule(String(value))}
    />
  );
}

describe("configuration section disclosure", () => {
  it("keeps an error-opened operation policy open after correcting its cron", async () => {
    render(<Editor initial="invalid" />);
    const details = screen.getByText("Operation policies").closest("details")!;
    expect(details.open).toBe(true);
    const input = screen.getByLabelText("Operation schedule");
    input.focus();
    fireEvent.change(input, { target: { value: "*/15 * * * *" } });
    expect(details.open).toBe(true);
    expect(input).toHaveFocus();
    expect(input).toHaveValue("*/15 * * * *");
    await userEvent.type(input, " ");
    expect(details.open).toBe(true);
  });
  it("preserves manual expansion across repeated schedule edits", async () => {
    render(<Editor />);
    const summary = screen.getByText("Operation policies");
    const details = summary.closest("details")!;
    expect(details.open).toBe(false);
    await userEvent.click(summary);
    const input = screen.getByLabelText("Operation schedule");
    await userEvent.clear(input);
    await userEvent.type(input, "0 */2 * * *");
    expect(details.open).toBe(true);
    expect(input).toHaveFocus();
    expect(input).toHaveValue("0 */2 * * *");
  });
});

vi.mock("../../context/ToastContext", () => ({
  useToast: () => ({ push: vi.fn() }),
}));

it("keeps maintenance forms out of the client dialog and links saved clients to Manage", () => {
  render(
    <QbitInstanceModal
      keyName="qBit"
      manageClientId="qBit"
      fields={[]}
      state={{}}
      onChange={vi.fn()}
      onRename={vi.fn()}
      onClose={vi.fn()}
      onSave={vi.fn(async () => true)}
      overlapWarnings={[]}
    />,
  );
  expect(screen.getByRole("button", { name: "Open Manage" })).toBeEnabled();
  expect(screen.queryByText("Torrent automation")).not.toBeInTheDocument();
  expect(
    screen.queryByText("Storage", { selector: "summary" }),
  ).not.toBeInTheDocument();
});
