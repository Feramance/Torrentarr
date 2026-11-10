export type Theme = "system" | "light" | "dark" | "midnight";

export const THEME_OPTIONS: ReadonlyArray<{ value: Theme; label: string }> = [
  { value: "system", label: "System" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
  { value: "midnight", label: "Midnight" },
];

export const SAVED_THEME: Record<Theme, string> = {
  system: "System",
  light: "Light",
  dark: "Dark",
  midnight: "Midnight",
};

/** Parse a persisted theme; missing or unknown values fall back to the system. */
export function themeFromValue(value: unknown): Theme {
  const key = String(value ?? "")
    .trim()
    .toLowerCase();
  if (key === "light" || key === "dark" || key === "midnight") return key;
  return "system";
}

function prefersDark(): boolean {
  return typeof window !== "undefined" &&
    typeof window.matchMedia === "function"
    ? window.matchMedia("(prefers-color-scheme: dark)").matches
    : true;
}

export function resolveTheme(theme: Theme): "light" | "dark" {
  if (theme === "light") return "light";
  if (theme === "dark" || theme === "midnight") return "dark";
  return prefersDark() ? "dark" : "light";
}

export function isDarkTheme(theme: Theme): boolean {
  return resolveTheme(theme) === "dark";
}

export function applyTheme(theme: Theme): void {
  const root = document.documentElement;
  root.dataset.theme = resolveTheme(theme);
  if (theme === "midnight") root.dataset.palette = "midnight";
  else delete root.dataset.palette;
}
