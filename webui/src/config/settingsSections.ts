import { maintenanceCategories } from "./maintenanceOperations";

export const configSections = {
  general: "General",
  interface: "Interface & Access",
  auth: "Authentication",
  clients: "Torrent Clients",
  integrations: "Integrations",
} as const;

export type ConfigSection = keyof typeof configSections;
export type SimpleConfigSection = Exclude<
  ConfigSection,
  "clients" | "integrations"
>;

export const maintenanceSections = [
  "Shared Settings",
  ...maintenanceCategories,
  "Recent Runs",
] as const;
export type MaintenanceSection = (typeof maintenanceSections)[number];
