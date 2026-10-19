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
