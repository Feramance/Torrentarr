import type { FieldDefinition } from "./configTypes";

export interface ConfigFieldGroup {
  title: string;
  fields: FieldDefinition[];
  defaultOpen?: boolean;
  description?: string;
}

type GroupSpec = [string, string[]];
// Consume each field once, keeping unknown schema fields visible in General.
function groupFields(
  fields: FieldDefinition[],
  specs: GroupSpec[],
): ConfigFieldGroup[] {
  const remaining = new Set(fields);
  const groups = specs.map(([title, keys]) => {
    const selected: FieldDefinition[] = [];
    for (const key of keys) {
      for (const field of remaining) {
        const path = field.path?.join(".") ?? "__name";
        if (
          path === key ||
          (key.endsWith(".*") && path.startsWith(key.slice(0, -1)))
        ) {
          selected.push(field);
          remaining.delete(field);
        }
      }
    }
    return {
      title,
      fields: selected,
      defaultOpen: title === "General" || title === "Connection",
    };
  });
  if (remaining.size) {
    const general = groups.find((group) => group.title === "General");
    if (general) general.fields.push(...remaining);
    else
      groups.push({
        title: "General",
        fields: [...remaining],
        defaultOpen: true,
      });
  }
  return groups.filter((group) => group.fields.length);
}

export function clientGroups(fields: FieldDefinition[]): ConfigFieldGroup[] {
  return groupFields(fields, [
    ["General", ["__name", "Type", "Disabled"]],
    ["Connection", ["Host", "Port", "UserName", "Password", "SkipTLSVerify"]],
    ["Torrent scope", ["ManagedCategories", "MatchSubcategories"]],
    ["Download location", ["DownloadPath"]],
    ["Free-space monitors", ["FreeSpace"]],
    ["Seeding and Hit & Run", ["CategorySeeding.*"]],
    ["Manage", ["Maintenance.*", "Storage.*", "Recovery.*"]],
  ]);
}

export function storageGroups(fields: FieldDefinition[]): ConfigFieldGroup[] {
  return groupFields(fields, [
    [
      "Storage settings",
      [
        "Storage.AutomaticCleanup",
        "Storage.PathMappings",
        "Storage.Orphans.Enabled",
        "Storage.Orphans.ScanRoots",
        "Storage.Orphans.MinimumAgeMinutes",
        "Storage.RecycleBin.Enabled",
        "Storage.RecycleBin.Path",
        "Storage.RecycleBin.RetentionEnabled",
        "Storage.RecycleBin.RetentionDays",
      ],
    ],
    ["Advanced", ["Storage.*"]],
  ]);
}
export function recoveryGroups(fields: FieldDefinition[]): ConfigFieldGroup[] {
  return groupFields(fields, [
    ["Torrent automation", ["Recovery.Enabled"]],
    ["Advanced", ["Recovery.*"]],
  ]);
}
// Compatibility export for extensions using the old grouping helper.
export const maintenanceGroups = storageGroups;

export function arrGeneralGroups(
  fields: FieldDefinition[],
): ConfigFieldGroup[] {
  return groupFields(fields, [
    ["General", ["__name", "Managed"]],
    ["Connection", ["URI", "APIKey", "SkipTLSVerify"]],
    [
      "Categories and torrent clients",
      ["Category", "TorrentClient", "TorrentClients", "QBitInstance"],
    ],
    [
      "Processing",
      [
        "ReSearch",
        "importMode",
        "RssSyncTimer",
        "RefreshDownloadsTimer",
        "ArrErrorCodesToBlocklist",
      ],
    ],
  ]);
}

export function simpleGroups(
  title: string,
  fields: FieldDefinition[],
): ConfigFieldGroup[] {
  if (title === "Settings")
    return groupFields(fields, [
      ["Logging", ["Settings.ConsoleLevel", "Settings.Logging"]],
      [
        "Processing and categories",
        [
          "Settings.ConfigVersion",
          "Settings.LoopSleepTimer",
          "Settings.SearchLoopDelay",
          "Settings.FailedCategory",
          "Settings.RecheckCategory",
          "Settings.Tagless",
          "Settings.IgnoreTorrentsYoungerThan",
        ],
      ],
      ["Connectivity", ["Settings.PingURLS", "Settings.NoInternetSleepTimer"]],
      [
        "Updates and ffprobe",
        [
          "Settings.AutoUpdateEnabled",
          "Settings.AutoUpdateChannel",
          "Settings.AutoUpdateCron",
          "Settings.FFprobeAutoUpdate",
        ],
      ],
      [
        "Worker recovery",
        [
          "Settings.AutoRestartProcesses",
          "Settings.MaxProcessRestarts",
          "Settings.ProcessRestartWindow",
          "Settings.ProcessRestartDelay",
        ],
      ],
    ]);
  if (title === "Web Settings")
    return groupFields(fields, [
      [
        "Listener and URL settings",
        ["WebUI.Host", "WebUI.Port", "WebUI.UrlBase"],
      ],
      [
        "Proxy and exposure",
        ["WebUI.BehindHttpsProxy", "WebUI.AllowInsecureExposure"],
      ],
      ["API access", ["WebUI.Token", "WebUI.AllowInsecureTokenQuery"]],
      [
        "Appearance and live display",
        ["WebUI.Theme", "WebUI.ViewDensity", "WebUI.LiveArr"],
      ],
    ]);
  return groupFields(fields, [
    ["Access controls", ["WebUI.AuthDisabled"]],
    [
      "Local login",
      ["WebUI.LocalAuthEnabled", "WebUI.Username", "WebUI.PasswordHash"],
    ],
    ["OIDC", ["WebUI.OIDCEnabled", "WebUI.OIDC.*"]],
  ]);
}
