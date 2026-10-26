import { expect, it } from "vitest";
import { parseFieldValue } from "../../pages/config/configDocumentUtils";

it("preserves inherited policies and normalizes numeric settings for both editors", () => {
  expect(
    parseFieldValue({ type: "checkbox", label: "Enabled" }, null),
  ).toBeNull();
  expect(
    parseFieldValue({ type: "number", label: "Budget", default: null }, ""),
  ).toBeNull();
  expect(parseFieldValue({ type: "number", label: "Age" }, "120")).toBe(120);
  expect(parseFieldValue({ type: "number", label: "Age" }, "bad")).toBe("bad");
  expect(parseFieldValue({ type: "tags", label: "Tags" }, ["keep"])).toEqual([
    "keep",
  ]);
  expect(
    parseFieldValue(
      { type: "text", label: "Value", parse: () => undefined },
      "invalid",
    ),
  ).toBe("invalid");
});
