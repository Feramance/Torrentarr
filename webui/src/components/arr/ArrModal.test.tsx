import { fireEvent, render } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { ArrModal } from "./ArrModal";

it("keeps keyboard focus in the modal, closes with Escape, and restores focus", async () => {
  const opener = document.createElement("button");
  document.body.appendChild(opener);
  opener.focus();
  const close = vi.fn();
  const user = userEvent.setup();
  const { getByRole, unmount } = render(
    <ArrModal title="Movie" onClose={close}>
      <a href="/detail">Details</a>
    </ArrModal>,
  );
  const closeButton = getByRole("button", { name: "Close" });
  expect(document.activeElement).toBe(closeButton);
  await user.tab({ shift: true });
  expect(document.activeElement).toBe(getByRole("link"));
  await user.tab();
  expect(document.activeElement).toBe(closeButton);
  fireEvent.keyDown(closeButton, { key: "Escape" });
  expect(close).toHaveBeenCalledOnce();
  unmount();
  expect(document.activeElement).toBe(opener);
  opener.remove();
});
