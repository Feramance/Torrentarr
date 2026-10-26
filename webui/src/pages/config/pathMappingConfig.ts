import type { ConfigDocument } from "../../api/types";
import type { ValidationError } from "./configTypes";

export function pathMappingErrors(
  row: ConfigDocument,
  rows: ConfigDocument[],
  index: number,
): ValidationError[] {
  const errors: ValidationError[] = [];
  const error = (key: string, message: string) =>
    errors.push({ path: [key], message });
  const normalize = (v: unknown) =>
    String(v ?? "")
      .replaceAll("\\", "/")
      .replace(/\/+$/, "");
  for (const key of ["ClientPath", "LocalPath"]) {
    const path = String(row[key] ?? "").replaceAll("\\", "/");
    if (
      !/^(\/|[a-z]:\/)/i.test(path) ||
      path.split("/").some((p) => p === "." || p === "..")
    )
      error(key, "Use an absolute path without traversal segments.");
  }
  const path = normalize(row.ClientPath);
  const same = (other: ConfigDocument) =>
    /^(\/\/|[a-z]:)/i.test(path)
      ? normalize(other.ClientPath).toLowerCase() === path.toLowerCase()
      : normalize(other.ClientPath) === path;
  if (rows.some((other, i) => i !== index && same(other)))
    error("ClientPath", "This torrent client path is already mapped.");
  return errors;
}

export function freeSpaceErrors(
  row: ConfigDocument,
  rows: ConfigDocument[],
  index: number,
): ValidationError[] {
  const errors: ValidationError[] = [];
  const path = String(row.Path ?? "").replaceAll("\\", "/");
  if (
    !/^(\/|[a-z]:\/)/i.test(path) ||
    path.split("/").some((p) => p === "." || p === "..")
  )
    errors.push({
      path: ["Path"],
      message: "Use an absolute local path without traversal segments.",
    });
  const normalize = (value: unknown) => {
    const p = String(value ?? "")
      .replaceAll("\\", "/")
      .replace(/\/+$/, "");
    return /^(\/\/|[a-z]:)/i.test(p) ? p.toLowerCase() : p;
  };
  if (
    rows.some(
      (other, i) => i !== index && normalize(other?.Path) === normalize(path),
    )
  )
    errors.push({
      path: ["Path"],
      message: "This monitor path is already configured.",
    });
  const limit = String(row.Limit ?? "").trim();
  const match = /^(\d+)([KMGT]?)$/i.exec(limit);
  if (
    !match ||
    BigInt(match[1]) *
      1024n ** BigInt("KMGT".indexOf(match[2].toUpperCase()) + 1) >
      9223372036854775807n
  )
    errors.push({
      path: ["Limit"],
      message: "Use a nonnegative byte limit or K, M, G, T suffix.",
    });
  return errors;
}
