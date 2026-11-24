import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TagInput } from "../../components/TagInput";

describe("TagInput", () => {
  it("renders each value as a row", () => {
    render(<TagInput value={["alpha", "beta"]} onChange={() => {}} />);
    expect(screen.getByText("alpha")).toBeInTheDocument();
    expect(screen.getByText("beta")).toBeInTheDocument();
  });

  it("typing and pressing Enter adds a tag", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={[]} onChange={onChange} placeholder="Add tag" />);
    const input = screen.getByRole("textbox");

    await user.type(input, "newtag");
    await user.keyboard("{Enter}");

    expect(onChange).toHaveBeenCalledWith(["newtag"]);
  });

  it("adds a literal comma with the Add button", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={[]} onChange={onChange} />);
    const input = screen.getByRole("textbox");

    await user.type(input, "tag,one");
    expect(onChange).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Add" }));
    expect(onChange).toHaveBeenCalledWith(["tag,one"]);
  });

  it("clicking the remove button deletes that tag", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={["foo", "bar"]} onChange={onChange} />);
    const removeBtn = screen.getByLabelText("Remove foo");
    await user.click(removeBtn);

    expect(onChange).toHaveBeenCalledWith(["bar"]);
  });

  it("does not add a duplicate tag", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={["existing"]} onChange={onChange} />);
    const input = screen.getByRole("textbox");

    await user.type(input, "existing");
    await user.keyboard("{Enter}");

    expect(onChange).not.toHaveBeenCalled();
  });

  it("edits an existing value and restores focus to its edit button", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={["alpha", "beta"]} onChange={onChange} />);
    await user.click(screen.getByRole("button", { name: "Edit beta" }));
    const input = screen.getByRole("textbox", { name: "Edit value 2" });
    expect(input).toHaveFocus();
    await user.type(input, ",gamma{Enter}");

    expect(onChange).toHaveBeenCalledWith(["alpha", "beta,gamma"]);
    expect(screen.getByRole("button", { name: "Edit beta" })).toHaveFocus();
  });

  it("does not show remove buttons when disabled", () => {
    render(<TagInput value={["locked"]} onChange={() => {}} disabled />);
    expect(screen.queryByLabelText("Remove locked")).not.toBeInTheDocument();
  });
});
