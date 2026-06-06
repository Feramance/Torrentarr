import { describe, expect, it } from "vitest";
import { durationDisplayToValue } from "../../config/durationUtils";

describe("durationDisplayToValue", () => {
  it("returns integer seconds for non-second display units", () => {
    expect(durationDisplayToValue(5, "h", "seconds", false)).toBe(18000);
  });

  it("returns integer minutes for non-minute display units", () => {
    expect(durationDisplayToValue(2, "h", "minutes", false)).toBe(120);
  });

  it("preserves -1 sentinel when allowed", () => {
    expect(durationDisplayToValue(-1, "s", "seconds", true)).toBe(-1);
  });
});
