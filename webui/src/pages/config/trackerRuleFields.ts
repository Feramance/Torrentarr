import type { ConfigDocument } from "../../api/types";
import type { FieldDefinition } from "./configTypes";
import { ARR_TRACKER_FIELDS, getTrackerProtectionFields } from "./configFields";

const removalModes = [
  "Inherit",
  "Never remove",
  "Ratio",
  "Time",
  "Ratio or time",
  "Ratio and time",
];
const removalValues = [null, -1, 1, 2, 3, 4];

export function trackerRemovalLabel(value: unknown): string {
  return removalModes[removalValues.indexOf(Number(value))] ?? "Inherit";
}

export function trackerRuleFields(rule: ConfigDocument): FieldDefinition[] {
  const legacy =
    !rule.Protection ||
    (rule.Protection as ConfigDocument).RequireLegacyClearance;
  const protection = rule.Protection as ConfigDocument | undefined;
  const policy = protection?.Policy ?? "custom";
  const legacyFields = [
    "HitAndRunMode",
    "MinSeedRatio",
    "MinSeedingTimeDays",
    "HitAndRunMinimumDownloadPercent",
    "HitAndRunPartialSeedRatio",
    "TrackerUpdateBuffer",
  ];
  return [
    {
      label: "Remove Torrent",
      path: ["RemoveTorrent"],
      type: "select",
      options: removalModes,
      format: trackerRemovalLabel,
      parse: (value) =>
        removalValues[removalModes.indexOf(String(value))] ?? null,
    },
    ...ARR_TRACKER_FIELDS.filter(
      (field) => legacy || !legacyFields.includes(field.path?.[0] ?? ""),
    ).map((field): FieldDefinition => {
      switch (field.path?.[0]) {
        case "MaximumETA":
          return { ...field, path: ["MaxETA"] };
        case "Name":
          return { ...field, required: false };
        case "HitAndRunMode":
          return {
            ...field,
            required: false,
            options: ["Inherit", ...(field.options ?? [])],
            format: (value) =>
              value == null
                ? "Inherit"
                : String(field.format?.(value) ?? value),
            parse: (value) => (value === "Inherit" ? null : value),
          };
        case "SuperSeedMode":
          return {
            ...field,
            type: "select",
            options: ["Inherit", "Enabled", "Disabled"],
            format: (value) =>
              value == null ? "Inherit" : value ? "Enabled" : "Disabled",
            parse: (value) =>
              value === "Inherit" ? null : value === "Enabled",
          };
        case "Priority":
        case "MaxUploadRatio":
        case "DownloadRateLimit":
        case "UploadRateLimit":
        case "TrackerUpdateBuffer":
          return {
            ...field,
            required: false,
            validate: (value, context) =>
              value == null || value === ""
                ? undefined
                : field.validate?.(value, context),
          };
        default:
          return field;
      }
    }),
    ...(rule.Protection
      ? getTrackerProtectionFields().filter((field) => {
          const key = field.path?.at(-1) ?? "";
          if (legacy)
            return [
              "RequireLegacyClearance",
              "AccountingGraceMinutes",
            ].includes(key);
          if (policy === "custom")
            return (
              key !== "MinimumRatio" ||
              protection?.AllowClientRatioClearance === true
            );
          if (
            [
              "MinimumSeedHours",
              "MinimumRatio",
              "PartialPolicy",
              "DownloadThresholdPercent",
            ].includes(key)
          )
            return false;
          return (
            policy !== "onlyencodes" ||
            !["AllowClientRatioClearance", "ThresholdInclusive"].includes(key)
          );
        })
      : []),
  ];
}

export function trackerScope(rule: ConfigDocument) {
  const key = rule.ArrInstances !== undefined ? "ArrInstances" : "ClientIds";
  const ids = rule[key];
  return { key, ids: Array.isArray(ids) ? (ids as string[]) : [] } as const;
}
