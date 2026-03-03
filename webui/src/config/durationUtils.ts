/**
 * Duration parsing/formatting for config time values.
 * Supports integers (legacy) and suffixed strings (e.g. "1w", "60m").
 * Must match backend DurationParser.cs.
 */

export type DurationUnit = "s" | "m" | "h" | "d" | "w" | "M";

const SUFFIX_TO_SECONDS: Record<string, number> = {
  s: 1,
  m: 60,
  h: 3600,
  d: 86400,
  w: 604800,
  M: 2592000, // 30 days
};

const SUFFIX_TO_MINUTES: Record<string, number> = {
  s: 1 / 60,
  m: 1,
  h: 60,
  d: 1440,
  w: 10080,
  M: 43200, // 30 days
};

const DURATION_PATTERN = /^\s*(-?\d+)\s*([smhdwM]?)\s*$/i;

export const DURATION_UNITS: { value: DurationUnit; label: string }[] = [
  { value: "s", label: "seconds" },
  { value: "m", label: "minutes" },
  { value: "h", label: "hours" },
  { value: "d", label: "days" },
  { value: "w", label: "weeks" },
  { value: "M", label: "months" },
];

function parseSuffixed(
  value: unknown,
  toSeconds: boolean,
  fallback: number
): number {
  if (value === null || value === undefined) return fallback;
  if (typeof value === "number" && Number.isFinite(value)) return value;
  const s = String(value).trim();
  if (!s) return fallback;
  const m = s.match(DURATION_PATTERN);
  if (!m) {
    const n = Number(s);
    return Number.isFinite(n) ? n : fallback;
  }
  const num = parseInt(m[1], 10);
  const rawSuffix = (m[2] || (toSeconds ? "s" : "m")).trim();
  const suffix = rawSuffix === "M" ? "M" : rawSuffix.toLowerCase();
  const mult = toSeconds
    ? SUFFIX_TO_SECONDS[suffix] ?? 1
    : SUFFIX_TO_MINUTES[suffix] ?? 1;
  return num * mult;
}

export function parseDurationToSeconds(value: unknown, fallback = -1): number {
  return parseSuffixed(value, true, fallback);
}

export function parseDurationToMinutes(value: unknown, fallback = -1): number {
  return Math.floor(parseSuffixed(value, false, fallback));
}
