import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { ArrMiniProgress } from "../../components/arr/ArrMiniProgress";

it("shows exact segment values on hover, keyboard focus, and mouse click without pinning", async () => {
  const user = userEvent.setup();
  render(<ArrMiniProgress label="Episodes" available={75} missing={25} />);
  const available = screen.getByRole("button", {
    name: "Available: 75 (75.0%)",
  });
  await user.hover(available);
  expect(screen.getByRole("tooltip")).toHaveTextContent(
    "Episodes · Available: 75 items (75.0%) · 100 monitored",
  );
  await user.unhover(available);
  expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
  await user.tab();
  expect(screen.getByRole("tooltip")).toHaveTextContent("Available: 75");
  await user.tab();
  expect(screen.getByRole("tooltip")).toHaveTextContent("Missing: 25");
  await user.keyboard("{Escape}");
  expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
  await user.click(available);
  expect(screen.getByRole("tooltip")).toHaveTextContent("Available: 75");
  await user.unhover(available);
  expect(screen.queryByRole("tooltip")).not.toBeInTheDocument();
});

it("represents a successful empty observation without NaN percentages", () => {
  render(<ArrMiniProgress label="Books" available={0} missing={0} />);
  expect(screen.getByRole("meter")).toHaveAttribute("aria-valuenow", "0");
  expect(screen.queryByText(/NaN/)).not.toBeInTheDocument();
});
