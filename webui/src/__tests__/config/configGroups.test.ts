import { describe, expect, it } from "vitest";
import {
  clientGroups,
  maintenanceGroups,
  arrGeneralGroups,
  arrSearchGroups,
  arrTorrentGroups,
  simpleGroups,
} from "../../pages/config/configGroups";
import {
  AUTH_SETTINGS_FIELDS,
  WEB_SETTINGS_FIELDS,
  SETTINGS_FIELDS,
  getClientFields,
  getArrFieldSets,
  validMaintenanceCron,
} from "../../pages/config/configFields";
import type { FieldDefinition } from "../../pages/config/configTypes";

function check(
  fields: FieldDefinition[],
  groups: { fields: FieldDefinition[] }[],
) {
  const rendered = groups.flatMap((g) => g.fields);
  expect(rendered.length).toBe(fields.length);
  expect(new Set(rendered).size).toBe(fields.length);
  expect(new Set(rendered)).toEqual(new Set(fields));
}

describe("configuration groups", () => {
  it("uses numeric scan dropdown values and a separate database refresh field", () => {
    const scan = getClientFields().find(
      (field) => field.path?.join(".") === "TorrentScanIntervalSeconds",
    )!;
    expect(scan.type).toBe("select");
    expect(scan.default).toBe(30);
    expect(scan.options).toEqual(["5", "15", "30", "60", "90", "120"]);
    expect(scan.parse?.("120")).toBe(120);
    expect(scan.validate?.(10, { root: {} })).toContain("must be");
    expect(scan.validate?.(30, { root: {} })).toBeUndefined();
    expect(
      SETTINGS_FIELDS.find(
        (field) => field.path?.join(".") === "Settings.LoopSleepTimer",
      )?.label,
    ).toBe("Database refresh interval");
  });

  it("renders every field exactly once and keeps connection fields together", () => {
    const fields = getClientFields();
    const groups = clientGroups(fields);
    check(fields, groups);
    const maintenance = groups.find((g) => g.title === "Manage")!.fields;
    check(maintenance, maintenanceGroups(maintenance));
    expect(
      groups
        .find((g) => g.title === "Connection")!
        .fields.map((f) => f.path?.join(".")),
    ).toEqual([
      "Host",
      "Port",
      "AuthMode",
      "UserName",
      "Password",
      "APIKey",
      "SkipTLSVerify",
    ]);
    for (const [title, definitions] of [
      ["general", SETTINGS_FIELDS],
      ["interface", WEB_SETTINGS_FIELDS],
      ["auth", AUTH_SETTINGS_FIELDS],
    ] as const)
      check(definitions, simpleGroups(title, definitions));
    for (const name of [
      "Radarr-test",
      "Sonarr-test",
      "Lidarr-test",
      "Readarr-test",
    ]) {
      const arr = getArrFieldSets(name);
      check(arr.generalFields, arrGeneralGroups(arr.generalFields));
      check(arr.entryFields, arrSearchGroups(arr.entryFields));
      check(arr.torrentFields, arrTorrentGroups(arr.torrentFields));
      expect(
        arrGeneralGroups(arr.generalFields)
          .find((g) => g.title === "Connection")!
          .fields.map((f) => f.path?.join(".")),
      ).toEqual(["URI", "APIKey", "SkipTLSVerify"]);
      expect(
        new Set([
          ...arr.entryFields,
          ...arr.qualityFields,
          ...arr.entryOmbiFields,
          ...arr.entryOverseerrFields,
        ]).size,
      ).toBe(
        arr.entryFields.length +
          arr.qualityFields.length +
          arr.entryOmbiFields.length +
          arr.entryOverseerrFields.length,
      );
    }
  });
  it("validates quiet hours using the same boundaries as the Host", () => {
    const fields = getArrFieldSets("Radarr-test").entryFields;
    const validate = (key: string, value: unknown) =>
      fields
        .find((field) => field.path?.[1] === key)
        ?.validate?.(value, {
          root: {},
          section: { EntrySearch: { QuietHoursStart: "22:00" } },
        });
    expect(validate("QuietHoursStart", "7:00")).toMatch(/HH:mm/);
    expect(validate("QuietHoursEnd", "24:00")).toMatch(/HH:mm/);
    expect(validate("QuietHoursEnd", "22:00")).toMatch(/must differ/);
    expect(validate("QuietHoursEnd", "07:00")).toBeUndefined();
    expect(validate("QuietHoursTimeZone", "")).toMatch(/IANA timezone/);
  });
  it.each(["*/15 * * * *", "0 0 29 2 *", "0,30 1-5/2 * * 0,7"])(
    "accepts cron %s",
    (cron) => expect(validMaintenanceCron(cron)).toBe(true),
  );
  it.each([
    "60 * * * *",
    "* * * *",
    "*/0 * * * *",
    "5-2 * * * *",
    "* * * * nonsense",
  ])("rejects cron %s", (cron) =>
    expect(validMaintenanceCron(cron)).toBe(false),
  );
});
