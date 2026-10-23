import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type JSX,
  type ReactNode,
} from "react";
import { getConfig, updateConfig, watchConfiguration } from "../api/client";
import { setUrlBaseFromMeta } from "../api/urlBase";
import { useToast } from "./ToastContext";
import { createPersistedBooleanSetter } from "./webUISettingSetters";

type ViewDensity = "comfortable" | "compact";
type Theme = "light" | "dark";

interface WebUISettings {
  liveArr: boolean;
  viewDensity: ViewDensity;
  theme: Theme;
}

interface WebUIContextValue {
  liveArr: boolean;
  viewDensity: ViewDensity;
  theme: Theme;
  setLiveArr: (value: boolean) => void;
  setViewDensity: (value: ViewDensity) => void;
  setTheme: (value: Theme) => void;
}

const WebUIContext = createContext<WebUIContextValue | null>(null);

function readPreference(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function applyThemeEarly(): void {
  try {
    const storedTheme = readPreference("theme");
    const theme =
      storedTheme === "light" || storedTheme === "dark" ? storedTheme : "dark";
    document.documentElement.setAttribute("data-theme", theme);
  } catch {
    document.documentElement.setAttribute("data-theme", "dark");
  }
}

applyThemeEarly();

export function WebUIProvider({
  children,
}: {
  children: ReactNode;
}): JSX.Element {
  const [settings, setSettings] = useState<WebUISettings>(() => {
    const storedDensity = readPreference("viewDensity") as ViewDensity | null;
    const storedTheme = readPreference("theme") as Theme | null;
    return {
      liveArr: true,
      viewDensity: storedDensity === "compact" ? "compact" : "comfortable",
      theme: storedTheme === "light" ? "light" : "dark",
    };
  });
  const { push } = useToast();

  // Load settings from backend only after AuthGate has authenticated (this provider
  // mounts inside the authenticated tree — see App.tsx).
  useEffect(() => {
    const loadSettings = async () => {
      try {
        const config = await getConfig();
        const webui = config?.WebUI as Record<string, unknown> | undefined;

        try {
          const warningMessage = sessionStorage.getItem(
            "config_version_warning",
          );
          if (warningMessage) {
            push(warningMessage, "error");
            sessionStorage.removeItem("config_version_warning");
          }
        } catch {
          /* Storage is optional; still apply the backend settings. */
        }
        const storedDensity = readPreference(
          "viewDensity",
        ) as ViewDensity | null;
        const storedTheme = readPreference("theme") as Theme | null;

        // Get theme and view density from backend or localStorage
        const backendTheme = webui?.Theme as string | undefined;
        const theme: Theme =
          storedTheme || (backendTheme?.toLowerCase() as Theme) || "dark";

        const backendDensity = webui?.ViewDensity as string | undefined;
        const viewDensity: ViewDensity =
          storedDensity ||
          (backendDensity?.toLowerCase() as ViewDensity) ||
          "comfortable";

        setSettings({
          liveArr: webui?.LiveArr === true,
          viewDensity,
          theme,
        });

        // Apply theme immediately
        document.documentElement.setAttribute("data-theme", theme);
      } catch {
        // settings load failed, defaults will be used
      }
    };

    void loadSettings();
  }, [push]);

  useEffect(() => {
    const controller = new AbortController();
    let retry: ReturnType<typeof setTimeout> | undefined;
    let navigation: AbortController | undefined;
    let navigationRetry: ReturnType<typeof setTimeout> | undefined;
    const navigateWhenReady = (target: URL, urlBase: string) => {
      navigation?.abort();
      if (navigationRetry) clearTimeout(navigationRetry);
      const pending = new AbortController();
      navigation = pending;
      const health = new URL(urlBase + "/health", target);
      const probe = async () => {
        try {
          // The destination may be on another origin and still rebinding.
          await fetch(health, {
            mode: "no-cors",
            credentials: "omit",
            cache: "no-store",
            signal: AbortSignal.any([
              pending.signal,
              AbortSignal.timeout(3000),
            ]),
          });
          if (!pending.signal.aborted) window.location.replace(target.href);
        } catch {
          if (!pending.signal.aborted)
            navigationRetry = setTimeout(() => void probe(), 500);
        }
      };
      void probe();
    };
    let previous: import("../api/types").LiveConfigurationEvent | undefined;
    const connect = async () => {
      try {
        await watchConfiguration((event) => {
          const next = event.webui;
          setUrlBaseFromMeta(next.urlBase);
          const theme = next.theme.toLowerCase() === "light" ? "light" : "dark";
          const viewDensity =
            next.viewDensity.toLowerCase() === "compact"
              ? "compact"
              : "comfortable";
          setSettings({ theme, viewDensity, liveArr: next.liveArr });
          try {
            localStorage.setItem("theme", theme);
          } catch {
            /* Backend settings remain authoritative. */
          }
          try {
            localStorage.setItem("viewDensity", viewDensity);
          } catch {
            /* Backend settings remain authoritative. */
          }
          document.documentElement.setAttribute("data-theme", theme);
          window.dispatchEvent(
            new CustomEvent("torrentarr:configuration", {
              detail: event.application,
            }),
          );
          if (previous) {
            const baseChanged = previous.webui.urlBase !== next.urlBase;
            const listenerChanged =
              !next.behindHttpsProxy &&
              (previous.webui.port !== next.port ||
                previous.webui.host !== next.host);
            if (baseChanged || listenerChanged) {
              const target = new URL(window.location.href);
              if (listenerChanged || navigation) {
                if (next.host !== "0.0.0.0" && next.host !== "::")
                  target.hostname = next.host;
                target.port = String(next.port);
                target.pathname = next.urlBase + "/ui";
              } else {
                const oldBase = previous.webui.urlBase;
                const suffix = target.pathname.startsWith(oldBase)
                  ? target.pathname.slice(oldBase.length)
                  : "/ui";
                target.pathname = next.urlBase + suffix;
              }
              if (listenerChanged || navigation)
                navigateWhenReady(target, next.urlBase);
              else window.location.replace(target.href);
            }
          }
          previous = event;
        }, controller.signal);
      } catch {
        /* Reconnect after listener changes or transient network failures. */
      }
      if (!controller.signal.aborted)
        retry = setTimeout(() => void connect(), 2000);
    };
    void connect();
    return () => {
      controller.abort();
      if (retry) clearTimeout(retry);
      navigation?.abort();
      if (navigationRetry) clearTimeout(navigationRetry);
    };
  }, []);

  // Auto-save settings to backend
  const saveSettings = useCallback(
    async (key: string, value: boolean | string) => {
      try {
        await updateConfig({ changes: { [`WebUI.${key}`]: value } });
      } catch {
        // save failed, non-critical
      }
    },
    [],
  );

  const setLiveArr = useMemo(
    () =>
      createPersistedBooleanSetter<WebUISettings, "liveArr">(
        setSettings,
        saveSettings,
        "liveArr",
        "LiveArr",
      ),
    [saveSettings],
  );

  const setViewDensity = useCallback(
    (value: ViewDensity) => {
      setSettings((prev) => ({ ...prev, viewDensity: value }));
      // Store in localStorage for instant application
      try {
        localStorage.setItem("viewDensity", value);
      } catch {
        /* Backend settings remain authoritative. */
      }
      // Save to backend with proper capitalization (Comfortable or Compact)
      const capitalizedDensity =
        value === "comfortable" ? "Comfortable" : "Compact";
      void saveSettings("ViewDensity", capitalizedDensity);
    },
    [saveSettings],
  );

  const setTheme = useCallback(
    (value: Theme) => {
      setSettings((prev) => ({ ...prev, theme: value }));
      // Store in localStorage for instant application
      try {
        localStorage.setItem("theme", value);
      } catch {
        /* Backend settings remain authoritative. */
      }
      // Apply theme immediately to DOM
      document.documentElement.setAttribute("data-theme", value);
      // Save to backend with proper capitalization (Light or Dark)
      const capitalizedTheme = value === "light" ? "Light" : "Dark";
      void saveSettings("Theme", capitalizedTheme);
    },
    [saveSettings],
  );

  const value = useMemo<WebUIContextValue>(
    () => ({
      liveArr: settings.liveArr,
      viewDensity: settings.viewDensity,
      theme: settings.theme,
      setLiveArr,
      setViewDensity,
      setTheme,
    }),
    [settings, setLiveArr, setViewDensity, setTheme],
  );

  return (
    <WebUIContext.Provider value={value}>{children}</WebUIContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useWebUI(): WebUIContextValue {
  const context = useContext(WebUIContext);
  if (!context) {
    throw new Error("useWebUI must be used within WebUIProvider");
  }
  return context;
}
