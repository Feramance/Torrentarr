import React from "react";
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { TagInput } from "../../components/TagInput";

describe("TagInput", () => {
  it("renders each tag as a chip", () => {
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

  it("pressing comma adds a tag", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={[]} onChange={onChange} />);
    const input = screen.getByRole("textbox");

    await user.type(input, "tagone,");

    expect(onChange).toHaveBeenCalledWith(["tagone"]);
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

  it("backspace on empty input removes the last tag", async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();

    render(<TagInput value={["alpha", "beta"]} onChange={onChange} />);
    const input = screen.getByRole("textbox");

    await user.click(input);
    await user.keyboard("{Backspace}");

    expect(onChange).toHaveBeenCalledWith(["alpha"]);
  });

  it("does not show remove buttons when disabled", () => {
    render(<TagInput value={["locked"]} onChange={() => {}} disabled />);
    expect(screen.queryByLabelText("Remove locked")).not.toBeInTheDocument();
  });
});
