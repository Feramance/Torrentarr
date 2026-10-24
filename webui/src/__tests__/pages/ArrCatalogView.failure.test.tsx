import { expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ArrCatalogView } from "../../pages/ArrCatalogView";

vi.mock("../../pages/arrCatalog/radarrDefinition", () => {
  throw new Error("old deployment chunk missing");
});
it("offers a reload when a catalog definition cannot be loaded", async () => {
  render(<ArrCatalogView kind="radarr" active />);
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Unable to load catalog",
  );
  expect(screen.getByRole("button", { name: "Reload" })).toBeInTheDocument();
});
