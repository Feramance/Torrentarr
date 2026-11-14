import type { TransferBucket } from "../api/types";

export function rateLabel(rate: number): string {
  if (rate < 1024) return `${Math.round(rate)} B/s`;
  if (rate < 1024 ** 2) return `${(rate / 1024).toFixed(1)} KiB/s`;
  return `${(rate / 1024 ** 2).toFixed(1)} MiB/s`;
}

export const TRANSFER_LEFT = 70;
export const TRANSFER_RIGHT = 780;

/** Missing or incomplete buckets break the line, rather than suggesting zero traffic. */
export function transferPath(
  points: TransferBucket[],
  field: "downloadRate" | "uploadRate",
  maximum: number,
): string {
  let connected = false;
  return points
    .map((point, index) => {
      const value = point[field];
      if (value == null || point.samples < point.expectedSamples) {
        connected = false;
        return "";
      }
      const x =
        TRANSFER_LEFT +
        (index / Math.max(1, points.length - 1)) *
          (TRANSFER_RIGHT - TRANSFER_LEFT);
      const y = 130 - (value / maximum) * 110;
      const command = `${connected ? "L" : "M"}${x.toFixed(2)},${y.toFixed(2)}`;
      connected = true;
      return command;
    })
    .join(" ");
}
