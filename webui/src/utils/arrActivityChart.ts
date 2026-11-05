import type { ArrActivityPoint, ArrActivitySeries } from "../api/types";
export type ActivityMetric =
  | "queueDepth"
  | "searchesCompleted"
  | "importsConfirmed";
const typeNames: Record<string, string> = {
  radarr: "Radarr",
  sonarr: "Sonarr",
  lidarr: "Lidarr",
  readarr: "Readarr",
};
export function groupActivity(
  series: ArrActivitySeries[],
  grouping: "type" | "worker",
): ArrActivitySeries[] {
  if (grouping === "worker") return series;
  return Object.keys(typeNames).flatMap((type) => {
    const members = series.filter((s) => s.type === type);
    if (!members.length) return [];
    return [
      {
        instance: typeNames[type]!,
        type,
        points: members[0]!.points.map((point, index) => {
          const rows = members
            .map((m) => m.points[index])
            .filter((p): p is ArrActivityPoint => !!p);
          const sum = (metric: ActivityMetric) =>
            rows.some((r) => r[metric] != null)
              ? rows.reduce((total, r) => total + (r[metric] ?? 0), 0)
              : null;
          return {
            timestamp: point.timestamp,
            queueDepth: sum("queueDepth"),
            searchesCompleted: sum("searchesCompleted"),
            importsConfirmed: sum("importsConfirmed"),
            queueSamples: rows.reduce((s, r) => s + r.queueSamples, 0),
            completionSamples: rows.reduce(
              (s, r) => s + r.completionSamples,
              0,
            ),
            expectedSamples: members.length * point.expectedSamples,
          };
        }),
      },
    ];
  });
}
export function workerHue(name: string): number {
  let hash = 2166136261;
  for (const character of name)
    hash = Math.imul(hash ^ character.charCodeAt(0), 16777619);
  return (hash >>> 0) % 360;
}
export function activityColor(
  series: ArrActivitySeries,
  grouping: "type" | "worker",
): string {
  return grouping === "type"
    ? `var(--arr-${series.type})`
    : `hsl(${workerHue(series.instance)} 58% var(--chart-worker-lightness))`;
}
export function activityCoverage(
  point: ArrActivityPoint,
  metric: ActivityMetric,
): number {
  return metric === "queueDepth" ? point.queueSamples : point.completionSamples;
}
export function activityPath(
  points: ArrActivityPoint[],
  metric: ActivityMetric,
  maximum: number,
): string {
  let joined = false;
  return points
    .map((point, index) => {
      if (
        point[metric] == null ||
        activityCoverage(point, metric) !== point.expectedSamples
      ) {
        joined = false;
        return "";
      }
      const segment = `${joined ? "L" : "M"}${(24 + (index / Math.max(1, points.length - 1)) * 752).toFixed(2)},${(142 - (point[metric]! / maximum) * 112).toFixed(2)}`;
      joined = true;
      return segment;
    })
    .join(" ");
}
export function activityValue(
  point: ArrActivityPoint | undefined,
  metric: ActivityMetric,
): string {
  if (!point || point[metric] == null) return "Unavailable";
  const value = point[metric]!.toLocaleString(undefined, {
    maximumFractionDigits: metric === "queueDepth" ? 1 : 0,
  });
  return activityCoverage(point, metric) === point.expectedSamples
    ? value
    : `${value} (partial)`;
}

export function activityDash(
  series: ArrActivitySeries,
  grouping: "type" | "worker",
): string | undefined {
  const index =
    grouping === "worker"
      ? workerHue(`line:${series.instance}`) % 4
      : ["radarr", "sonarr", "lidarr", "readarr"].indexOf(series.type);
  return [undefined, "6 3", "2 3", "8 3 2 3"][index];
}
