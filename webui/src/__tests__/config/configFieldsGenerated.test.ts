import { describe, expect, it } from "vitest";
import { getArrFieldSets } from "../../pages/config/configFields";
import {
  CONFIG_SCHEMA,
  CONFIG_SCHEMA_FIELD_COUNT,
} from "../../config/configFields.generated";

describe("generated config schema", () => {
  it("labels Seerr while editing legacy Overseerr config paths", () => {
    for (const name of ["Radarr-Movies", "Sonarr-TV"]) {
      const fields = getArrFieldSets(name).entryOverseerrFields;
      expect(fields).toHaveLength(6);
      expect(fields.every((field) => field.path?.[1] === "Overseerr")).toBe(
        true,
      );
      expect(fields).toContainEqual(
        expect.objectContaining({
          label: "Seerr API Key",
          path: ["EntrySearch", "Overseerr", "OverseerrAPIKey"],
        }),
      );
    }
    expect(getArrFieldSets("Lidarr-Music").entryOverseerrFields).toEqual([]);
  });

  it("offers opt-in import rescue for all supported Arr adapters", () => {
    const hasRescue = (instance: string) =>
      getArrFieldSets(instance).torrentFields.some(
        (field) => field.path?.join(".") === "Torrent.RescueBlockedImports",
      );
    expect(hasRescue("Radarr-Movies")).toBe(true);
    expect(hasRescue("Sonarr-TV")).toBe(true);
    expect(hasRescue("Lidarr-Music")).toBe(true);
    expect(hasRescue("Readarr-Books")).toBe(true);
  });
  it("offers shared search gates and restricts availability to Radarr", () => {
    for (const name of [
      "Radarr-Movies",
      "Sonarr-TV",
      "Lidarr-Music",
      "Readarr-Books",
    ]) {
      const fields = getArrFieldSets(name).entryFields.map((field) =>
        field.path?.join("."),
      );
      expect(fields).toContain("EntrySearch.IncludeTags");
      expect(fields).toContain("EntrySearch.ExcludeTags");
      expect(fields).toContain("EntrySearch.QuietHoursTimeZone");
      expect(fields.includes("EntrySearch.RequireRadarrAvailability")).toBe(
        name.startsWith("Radarr"),
      );
    }
  });
  it("exposes the live browser-origin allowlist", () => {
    const origins = CONFIG_SCHEMA.sections.WebUI.find(
      (field) => String(field.key) === "CorsAllowedOrigins",
    );
    expect(origins).toMatchObject({
      kind: "tags",
      default: [],
      applyLive: true,
      requiresRestart: false,
    });
  });

  it("contains the pinned qBitrr 5.14.5 field inventory", () => {
    const qbitrrFieldCount = Object.entries(CONFIG_SCHEMA.sections)
      .filter(([section]) => section !== "TorrentClient")
      .reduce((total, [, fields]) => total + fields.length, 0);

    expect(qbitrrFieldCount).toBeGreaterThan(100);
    expect(CONFIG_SCHEMA_FIELD_COUNT).toBe(
      qbitrrFieldCount + CONFIG_SCHEMA.sections.TorrentClient.length,
    );
    expect(
      CONFIG_SCHEMA.sections.Settings.some(
        (field) => String(field.key) === "FreeSpace",
      ),
    ).toBe(false);
    expect(
      CONFIG_SCHEMA.sections.TorrentClient.some(
        (field) => field.key === "FreeSpace",
      ),
    ).toBe(true);
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
    expect(
      CONFIG_SCHEMA.sections.TorrentClient.some(
        (field) => field.key === "Maintenance.Enabled",
      ),
    ).toBe(true);
  });

  it("exposes every shared torrent setting for every client type", () => {
    const keys = CONFIG_SCHEMA.sections.TorrentClient.map((field) => field.key);
    for (const field of CONFIG_SCHEMA.sections.qBit) {
      expect(keys).toContain(field.key);
    }
    expect(new Set(keys).size).toBe(keys.length);
  });

  it("declares a three-part Torrentarr config version", () => {
    const version = CONFIG_SCHEMA.sections.Settings.find(
      (field) => field.key === "ConfigVersion",
    );
    expect(version?.default).toMatch(/^\d+\.\d+\.\d+$/);
  });
});
