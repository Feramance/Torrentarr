import { WEBUI_FIELD_OVERLAYS } from "./configFieldOverlays";
import { parseDurationToSeconds } from "../../config/durationUtils";
import { CONFIG_SCHEMA } from "../../config/configFields.generated";
import type { ConfigSchemaResponse } from "../../api/types";
import type { FieldDefinition } from "./configTypes";
import { arrTypeFromSectionName } from "../../config/arrSections";
let schema = CONFIG_SCHEMA as unknown as ConfigSchemaResponse;
const fieldTypes = new Set([
  "text",
  "number",
  "checkbox",
  "password",
  "select",
  "tags",
  "duration",
]);
function isStructuredArray(path: string[]): boolean {
  return (
    path[0] === "Maintenance" &&
    ["PathMappings", "SharePolicies", "Notifications"].includes(path[1] ?? "")
  );
}

function fields(
  section: string,
  prefix: string[] = [],
  arrType?: string | null,
): FieldDefinition[] {
  return (schema.sections[section] ?? [])
    .filter(
      (f) =>
        f.uiExpose &&
        fieldTypes.has(f.kind) &&
        (!arrType || !f.arrKinds?.length || f.arrKinds.includes(arrType)),
    )
    .map((f) => ({
      label: f.label,
      path: [...prefix, ...f.path],
      type: (isStructuredArray(f.path)
        ? "json"
        : f.kind) as FieldDefinition["type"],
      format: isStructuredArray(f.path)
        ? (value) =>
            typeof value === "string"
              ? value
              : JSON.stringify(value ?? [], null, 2)
        : f.kind === "select"
          ? (value) => (value == null ? "" : String(value))
          : undefined,
      parse: isStructuredArray(f.path)
        ? (value) => {
            try {
              return JSON.parse(String(value));
            } catch {
              return value;
            }
          }
        : f.kind === "select" && typeof f.default === "number"
          ? (value) => Number(value)
          : undefined,
      options: f.options,
      placeholder: f.placeholder,
      description:
        f.description ??
        (Array.isArray(f.comments) ? f.comments.join(" ") : f.comments),
      secure: f.secure,
      required: f.required,
      nativeUnit: f.nativeUnit,
      allowNegative: f.allowNegative,
      applyLive: f.applyLive ?? undefined,
      requiresRestart: f.requiresRestart ?? undefined,
      minimum: f.minimum,
      maximum: f.maximum,
      validate: (value: unknown, context) => {
        if (isStructuredArray(f.path) && !Array.isArray(value ?? []))
          return `${f.label} must be a JSON array.`;
        const webError =
          section === "WebUI"
            ? WEBUI_FIELD_OVERLAYS[
                [...prefix, ...f.path].join(".")
              ]?.validate?.(value, context)
            : undefined;
        if (webError) return webError;
        if (value == null || value === "") return undefined;
        const number = Number(value);
        if (f.minimum != null && number < f.minimum)
          return `${f.label} must be at least ${f.minimum}.`;
        if (f.maximum != null && number > f.maximum)
          return `${f.label} must be at most ${f.maximum}.`;
        return undefined;
      },
    }));
}
const displayName: FieldDefinition = {
  label: "Display Name",
  type: "text",
  sectionName: true,
};
export let SETTINGS_FIELDS: FieldDefinition[];
export let WEB_SETTINGS_FIELDS: FieldDefinition[];
export let AUTH_SETTINGS_FIELDS: FieldDefinition[];
export let QBIT_FIELDS: FieldDefinition[];
export function configureConfigFields(
  value: ConfigSchemaResponse = CONFIG_SCHEMA as unknown as ConfigSchemaResponse,
): void {
  schema = value;
  SETTINGS_FIELDS = fields("Settings", ["Settings"]);
  const core = new Set([
    "Host",
    "Port",
    "Token",
    "BehindHttpsProxy",
    "UrlBase",
    "AllowInsecureExposure",
  ]);
  const web = fields("WebUI", ["WebUI"]);
  WEB_SETTINGS_FIELDS = web.filter((f) => core.has(f.path![1]!));
  AUTH_SETTINGS_FIELDS = web.filter((f) => !core.has(f.path![1]!));
  QBIT_FIELDS = [
    { ...displayName, placeholder: "qBit-seedbox" },
    ...fields("qBit"),
  ];
}
configureConfigFields(schema);
export function getClientFields(): FieldDefinition[] {
  return fields("TorrentClient");
}
export function getSchemaDefaults(
  section: string,
  arrType?: string,
): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const f of schema.sections[section] ?? []) {
    if (arrType && f.arrKinds?.length && !f.arrKinds.includes(arrType))
      continue;
    let target = result;
    for (const key of f.path.slice(0, -1))
      target = (target[key] ??= {}) as Record<string, unknown>;
    target[f.path.at(-1)!] = structuredClone(f.default);
  }
  return result;
}
export function extractTooltipSummary(tooltip?: string): string | undefined {
  return tooltip?.split(/(?<=[.!?])\s/)[0];
}
export const ARR_TRACKER_FIELDS: FieldDefinition[] = [
  { label: "Name", path: ["Name"], type: "text", required: true },
  { label: "URI", path: ["URI"], type: "text", required: true },
  {
    label: "Priority",
    path: ["Priority"],
    type: "number",
    validate: (value) => {
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < 0) {
        return "Priority must be a non-negative number.";
      }
      return undefined;
    },
  },
  {
    label: "Sort Torrents by Tracker Priority",
    path: ["SortTorrents"],
    type: "checkbox",
  },
  {
    label: "Maximum ETA",
    path: ["MaximumETA"],
    type: "duration",
    nativeUnit: "seconds",
    allowNegative: true,
    validate: (value) => {
      const total = parseDurationToSeconds(value, -2);
      if (total === -1) return undefined;
      if (!Number.isFinite(total) || total < -1) {
        return "Maximum ETA must be -1 or a non-negative duration.";
      }
      return undefined;
    },
  },
  {
    label: "Download Rate Limit",
    path: ["DownloadRateLimit"],
    type: "number",
    validate: (value) => {
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < -1) {
        return "Download Rate Limit must be -1 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Upload Rate Limit",
    path: ["UploadRateLimit"],
    type: "number",
    validate: (value) => {
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < -1) {
        return "Upload Rate Limit must be -1 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Max Upload Ratio",
    path: ["MaxUploadRatio"],
    type: "number",
    validate: (value) => {
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < -1) {
        return "Max Upload Ratio must be -1 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Max Seeding Time",
    path: ["MaxSeedingTime"],
    type: "duration",
    nativeUnit: "seconds",
    allowNegative: true,
    validate: (value) => {
      const total = parseDurationToSeconds(value, -2);
      if (total === -1) return undefined;
      if (!Number.isFinite(total) || total < -1) {
        return "Max Seeding Time must be -1 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Add Tracker If Missing",
    path: ["AddTrackerIfMissing"],
    type: "checkbox",
  },
  { label: "Remove If Exists", path: ["RemoveIfExists"], type: "checkbox" },
  { label: "Super Seed Mode", path: ["SuperSeedMode"], type: "checkbox" },
  {
    label: "Add Tags",
    path: ["AddTags"],
    type: "tags",
  },
  {
    label: "Hit and Run Mode",
    path: ["HitAndRunMode"],
    type: "select",
    options: ["and", "or", "disabled"],
    format: (v: unknown) =>
      v === true ? "and" : v === false ? "disabled" : (v as string),
    parse: (v: string | boolean) =>
      typeof v === "string" ? v : v ? "and" : "disabled",
  },
  {
    label: "Min Seed Ratio",
    path: ["MinSeedRatio"],
    type: "number",
    required: false,
    validate: (value) => {
      if (value === null || value === undefined || value === "")
        return undefined;
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < 0) {
        return "Min Seed Ratio must be 0 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Min Seeding Time (days)",
    path: ["MinSeedingTimeDays"],
    type: "number",
    required: false,
    validate: (value) => {
      if (value === null || value === undefined || value === "")
        return undefined;
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < 0) {
        return "Min Seeding Time must be 0 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Min Download % for HnR",
    path: ["HitAndRunMinimumDownloadPercent"],
    type: "number",
    required: false,
    validate: (value) => {
      if (value === null || value === undefined || value === "")
        return undefined;
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < 0 || num > 100) {
        return "Min Download % must be between 0 and 100.";
      }
      return undefined;
    },
  },
  {
    label: "Partial Download Seed Ratio",
    path: ["HitAndRunPartialSeedRatio"],
    type: "number",
    required: false,
    validate: (value) => {
      if (value === null || value === undefined || value === "")
        return undefined;
      const num = typeof value === "number" ? value : Number(value);
      if (!Number.isFinite(num) || num < 0) {
        return "Partial Download Seed Ratio must be 0 or greater.";
      }
      return undefined;
    },
  },
  {
    label: "Tracker Update Buffer",
    path: ["TrackerUpdateBuffer"],
    type: "duration",
    nativeUnit: "seconds",
    required: false,
    validate: (value) => {
      if (value === null || value === undefined || value === "")
        return undefined;
      const total = parseDurationToSeconds(value, -1);
      if (!Number.isFinite(total) || total < 0) {
        return "Tracker Update Buffer must be 0 or greater.";
      }
      return undefined;
    },
  },
];

export function getArrFieldSets(arrKey: string) {
  const type = arrTypeFromSectionName(arrKey);
  const arr = fields("Arr", [], type);
  return {
    generalFields: [displayName, ...arr.filter((f) => f.path?.length === 1)],
    entryFields: arr.filter(
      (f) =>
        f.path?.[0] === "EntrySearch" &&
        !["Ombi", "Overseerr"].includes(f.path[1]!),
    ),
    entryOmbiFields: arr.filter((f) => f.path?.[1] === "Ombi"),
    entryOverseerrFields: arr.filter((f) => f.path?.[1] === "Overseerr"),
    torrentFields: arr.filter(
      (f) => f.path?.[0] === "Torrent" && f.path[1] !== "SeedingMode",
    ),
    seedingFields: arr.filter(
      (f) => f.path?.[0] === "Torrent" && f.path[1] === "SeedingMode",
    ),
    trackerFields: ARR_TRACKER_FIELDS,
  };
}
