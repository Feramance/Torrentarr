import { describe, expect, it } from "vitest";
import {
  CONFIG_SCHEMA,
  CONFIG_SCHEMA_FIELD_COUNT,
} from "../../config/configFields.generated";

describe("generated config schema", () => {
  it("contains the pinned qBitrr 5.14.5 field inventory", () => {
    expect(CONFIG_SCHEMA_FIELD_COUNT).toBe(124);
    expect(
      CONFIG_SCHEMA.sections.Arr.some(
        (field) => field.key === "Torrent.FileExtensionAllowlist",
      ),
    ).toBe(true);
    expect(
      CONFIG_SCHEMA.sections.Arr.some(
        (field) => "arrKinds" in field && field.arrKinds.includes("readarr"),
      ),
    ).toBe(true);
  });

  it("uses Torrentarr's +1 major config version", () => {
    const version = CONFIG_SCHEMA.sections.Settings.find(
      (field) => field.key === "ConfigVersion",
    );
    expect(version?.default).toBe("6.15.2");
  });
});
