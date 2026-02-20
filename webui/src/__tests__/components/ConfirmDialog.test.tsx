import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ConfirmDialog } from "../../components/ConfirmDialog";

describe("ConfirmDialog", () => {
  it("renders the title and message", () => {
    render(
      <ConfirmDialog
        title="Delete item?"
        message="This action cannot be undone."
        onConfirm={() => {}}
        onCancel={() => {}}
      />
    );
    expect(screen.getByText("Delete item?")).toBeInTheDocument();
    expect(screen.getByText("This action cannot be undone.")).toBeInTheDocument();
  });

  it("confirm button calls onConfirm", async () => {
    const onConfirm = vi.fn();
    const user = userEvent.setup();

    render(
      <ConfirmDialog
        title="Title"
        message="Message"
        onConfirm={onConfirm}
        onCancel={() => {}}
      />
    );

    await user.click(screen.getByText("Confirm"));
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it("cancel button calls onCancel", async () => {
    const onCancel = vi.fn();
    const user = userEvent.setup();

    render(
      <ConfirmDialog
        title="Title"
        message="Message"
        onConfirm={() => {}}
        onCancel={onCancel}
      />
    );

    // Click the Cancel button in the footer (not the ✕ icon button)
    const cancelBtns = screen.getAllByText("Cancel");
    await user.click(cancelBtns[0]);
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("clicking backdrop calls onCancel", async () => {
    const onCancel = vi.fn();
    const user = userEvent.setup();

    const { container } = render(
      <ConfirmDialog
        title="Title"
        message="Message"
        onConfirm={() => {}}
        onCancel={onCancel}
      />
    );

    const backdrop = container.querySelector(".modal-backdrop")!;
    await user.click(backdrop);
    expect(onCancel).toHaveBeenCalled();
  });

  it("applies danger class to confirm button when danger=true", () => {
    render(
      <ConfirmDialog
        title="Title"
        message="Message"
        onConfirm={() => {}}
        onCancel={() => {}}
        danger={true}
      />
    );

    const confirmBtn = screen.getByText("Confirm");
    expect(confirmBtn.className).toContain("danger");
    expect(confirmBtn.className).not.toContain("primary");
  });

  it("applies primary class to confirm button when danger=false", () => {
    render(
      <ConfirmDialog
        title="Title"
        message="Message"
        onConfirm={() => {}}
        onCancel={() => {}}
        danger={false}
      />
    );

    const confirmBtn = screen.getByText("Confirm");
    expect(confirmBtn.className).toContain("primary");
    expect(confirmBtn.className).not.toContain("danger");
  });

  it("uses custom confirmLabel and cancelLabel", () => {
    render(
      <ConfirmDialog
        title="Title"
        message="Message"
        confirmLabel="Yes, delete"
        cancelLabel="No, keep"
        onConfirm={() => {}}
        onCancel={() => {}}
      />
    );

    expect(screen.getByText("Yes, delete")).toBeInTheDocument();
    expect(screen.getByText("No, keep")).toBeInTheDocument();
  });
});
