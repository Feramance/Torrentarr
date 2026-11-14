import { QbitInstanceModal } from "../../pages/config/configModals";
import { useState } from "react";
import { expect, it, vi } from "vitest";
import { fireEvent, render, screen, within } from "@testing-library/react";
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
function Editor({ initial = "*/5 * * * *" }) {
  const [schedule, setSchedule] = useState(initial);
  return (
    <FieldGroup
      title="Operation policies"
      fields={[field]}
      basePath={[]}
      sectionKey="qBit"
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

it("keeps the operation schedule visible and focused after correcting validation", () => {
  render(<Editor initial="invalid" />);
  expect(
    screen.getByRole("heading", { name: "Operation policies" }),
  ).toBeVisible();
  const input = screen.getByLabelText("Operation schedule");
  expect(input).toBeVisible();
  input.focus();
  fireEvent.change(input, { target: { value: "*/15 * * * *" } });
  expect(input).toBeVisible();
  expect(input).toHaveFocus();
  expect(input).toHaveValue("*/15 * * * *");
});

vi.mock("../../context/ToastContext", () => ({
  useToast: () => ({ push: vi.fn() }),
}));

it("keeps maintenance forms out of the client dialog and links saved clients to Shared Settings", () => {
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
  expect(
    screen.getByRole("button", { name: "Open Shared Settings" }),
  ).toBeEnabled();
  expect(screen.queryByText("Torrent automation")).not.toBeInTheDocument();
  expect(
    screen.queryByText("Storage", { selector: "h3" }),
  ).not.toBeInTheDocument();
});

it("separates the scrollable client summary and fields from dialog actions", async () => {
  const onClose = vi.fn();
  render(
    <QbitInstanceModal
      keyName="qBit"
      fields={[]}
      state={{}}
      onChange={vi.fn()}
      onRename={vi.fn()}
      onClose={onClose}
      onSave={vi.fn(async () => true)}
      overlapWarnings={[]}
    />,
  );
  const dialog = screen.getByRole("dialog");
  const summary = within(dialog).getByRole("complementary", {
    name: "How torrents are handled",
  });
  const fields = dialog.querySelector(".config-modal__fields")!;
  expect(summary).toHaveAttribute("tabindex", "0");
  expect(summary.parentElement).toBe(fields.parentElement);
  expect(fields).not.toContainElement(summary);
  expect(fields).not.toContainElement(
    screen.getByRole("button", { name: "Save" }),
  );
  const close = screen.getByRole("button", { name: "Close" });
  expect(close).toHaveFocus();
  await userEvent.tab({ shift: true });
  expect(screen.getByRole("button", { name: "Save" })).toHaveFocus();
  await userEvent.tab();
  expect(close).toHaveFocus();
  await userEvent.click(close);
  expect(onClose).toHaveBeenCalledOnce();
});
