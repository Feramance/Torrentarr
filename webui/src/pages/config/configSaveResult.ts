import type { ConfigurationApplicationStatus } from "../../api/types";

/** WebUI keys that bind a new host/port and require a full page reload after save. */
export const WEBUI_PAGE_RELOAD_KEYS = new Set(["WebUI.Host", "WebUI.Port"]);

export function webuiChangeNeedsPageReload(
  changedKeys: readonly string[],
): boolean {
  return changedKeys.some((key) => WEBUI_PAGE_RELOAD_KEYS.has(key));
}

export function formatConfigSaveMessage(
  _reloadType: string,
  _configReloaded: boolean,
  _affectedInstances: string[] | undefined,
  _changedKeys: readonly string[],
  application?: ConfigurationApplicationStatus,
): string {
  if (application?.state === "error")
    return "Configuration saved • Some changes could not be applied";
  if (application?.state === "pending")
    return "Configuration saved • Waiting for active operations to finish";
  return "Configuration saved";
}

export function shouldReloadPageAfterSave(
  reloadType: string,
  changedKeys: readonly string[],
): boolean {
  return reloadType === "webui" && webuiChangeNeedsPageReload(changedKeys);
}

export function shouldRefreshMetaAfterSave(
  reloadType: string,
  changedKeys: readonly string[],
): boolean {
  if (reloadType === "live") {
    return true;
  }
  return reloadType === "webui" && !webuiChangeNeedsPageReload(changedKeys);
}
