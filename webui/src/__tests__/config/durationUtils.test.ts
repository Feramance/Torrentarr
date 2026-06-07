import { describe, expect, it } from "vitest";
import { durationDisplayToValue } from "../../config/durationUtils";

describe("durationDisplayToValue", () => {
  it("returns base-unit seconds when user picks hours", () => {
    expect(durationDisplayToValue(3, "h", "seconds", false)).toBe(10800);
  });

  it("returns base-unit minutes when user picks hours", () => {
    expect(durationDisplayToValue(2, "h", "minutes", false)).toBe(120);
  });

  it("returns -1 when allowNegative and number is -1", () => {
    expect(durationDisplayToValue(-1, "s", "seconds", true)).toBe(-1);
  });
});
