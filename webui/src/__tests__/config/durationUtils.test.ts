import { describe, expect, it } from "vitest";
import { durationDisplayToValue } from "../../config/durationUtils";

describe("durationDisplayToValue", () => {
  it("returns base-unit seconds for hour selections", () => {
    expect(durationDisplayToValue(2, "h", "seconds", false)).toBe(7200);
  });

  it("returns base-unit minutes for day selections", () => {
    expect(durationDisplayToValue(1, "d", "minutes", false)).toBe(1440);
  });

  it("preserves -1 sentinel values", () => {
    expect(durationDisplayToValue(-1, "h", "seconds", true)).toBe(-1);
  });
});
