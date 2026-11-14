import type { ConfigDocument } from "../../api/types";
import type { FieldDefinition } from "./configTypes";
import { ARR_TRACKER_FIELDS, getTrackerProtectionFields } from "./configFields";

export function trackerRuleFields(rule: ConfigDocument): FieldDefinition[] {
  return [
    ...ARR_TRACKER_FIELDS.map((field): FieldDefinition => {
      switch (field.path?.[0]) {
        case "MaximumETA":
          return { ...field, path: ["MaxETA"] };
        case "Name":
          return { ...field, required: false };
        case "HitAndRunMode":
          return { ...field, required: false };
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
    ...(rule.Protection ? getTrackerProtectionFields() : []),
  ];
}

export function trackerScope(rule: ConfigDocument) {
  const key = rule.ArrInstances !== undefined ? "ArrInstances" : "ClientIds";
  const ids = rule[key];
  return { key, ids: Array.isArray(ids) ? (ids as string[]) : [] } as const;
}
