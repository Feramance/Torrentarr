import { describe, expect, it } from "vitest";
import {
  clientGroups,
  maintenanceGroups,
  arrGeneralGroups,
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
    ).toEqual(["Host", "Port", "UserName", "Password", "SkipTLSVerify"]);
    for (const [title, definitions] of [
      ["Settings", SETTINGS_FIELDS],
      ["Web Settings", WEB_SETTINGS_FIELDS],
      ["Authentication", AUTH_SETTINGS_FIELDS],
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
