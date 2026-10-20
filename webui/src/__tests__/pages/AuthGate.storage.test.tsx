import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { AuthGate } from "../../AuthGate";
vi.mock("../../api/client", () => ({
  getMeta: vi.fn(async () => ({ auth_required: true })),
  fetchWebToken: vi.fn(async () => "cookie-token"),
  logout: vi.fn(),
}));
afterEach(() => vi.restoreAllMocks());
it("accepts a valid cookie session when token storage is blocked", async () => {
  vi.spyOn(window, "localStorage", "get").mockImplementation(() => {
    throw new Error("blocked");
  });
  render(<AuthGate>{() => <p>Authenticated content</p>}</AuthGate>);
  expect(await screen.findByText("Authenticated content")).toBeInTheDocument();
});
