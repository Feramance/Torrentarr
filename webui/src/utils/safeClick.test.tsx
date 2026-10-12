import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { safeClick } from "./safeClick";

describe("safeClick", () => {
  it("keeps pointer tracking until the click handler has rejected a drag", async () => {
    const onClick = vi.fn();
    const { getByRole } = render(
      <button onClick={safeClick(onClick)}>Open</button>,
    );
    const button = getByRole("button");
    fireEvent(
      button,
      new MouseEvent("pointerdown", {
        bubbles: true,
        clientX: 10,
        clientY: 10,
      }),
    );
    fireEvent(
      button,
      new MouseEvent("pointermove", {
        bubbles: true,
        clientX: 30,
        clientY: 10,
      }),
    );
    fireEvent.click(button, { clientX: 30, clientY: 10 });
    expect(onClick).not.toHaveBeenCalled();
    await Promise.resolve();
    fireEvent(
      button,
      new MouseEvent("pointerdown", {
        bubbles: true,
        clientX: 10,
        clientY: 10,
      }),
    );
    fireEvent.click(button, { clientX: 10, clientY: 10 });
    expect(onClick).toHaveBeenCalledOnce();
  });
});
