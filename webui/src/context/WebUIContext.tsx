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
import {
  getConfig,
  updateConfig,
  watchConfiguration,
  invalidateGetCache,
} from "../api/client";
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

function applyThemeEarly(): void {
  try {
    const storedTheme = localStorage.getItem("theme");
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
    const storedDensity = localStorage.getItem(
      "viewDensity",
    ) as ViewDensity | null;
    const storedTheme = localStorage.getItem("theme") as Theme | null;
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

        // Check for config version warning in sessionStorage
        const warningMessage = sessionStorage.getItem("config_version_warning");
        if (warningMessage) {
          // Show error toast with longer duration for config version mismatch
          push(warningMessage, "error");
          // Clear the warning after showing it
          sessionStorage.removeItem("config_version_warning");
        }

        // Load from localStorage as fallback
        const storedDensity = localStorage.getItem(
          "viewDensity",
        ) as ViewDensity | null;
        const storedTheme = localStorage.getItem("theme") as Theme | null;

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
    let previous: import("../api/types").LiveConfigurationEvent | undefined;
    const connect = async () => {
      try {
        await watchConfiguration((event) => {
          const next = event.webui;
          if (
            previous?.application.acceptedRevision !==
            event.application.acceptedRevision
          )
            invalidateGetCache(["/web/"]);
          setUrlBaseFromMeta(next.urlBase);
          const theme = next.theme.toLowerCase() === "light" ? "light" : "dark";
          const viewDensity =
            next.viewDensity.toLowerCase() === "compact"
              ? "compact"
              : "comfortable";
          setSettings((previous) =>
            previous.theme === theme &&
            previous.viewDensity === viewDensity &&
            previous.liveArr === next.liveArr
              ? previous
              : { theme, viewDensity, liveArr: next.liveArr },
          );
          localStorage.setItem("theme", theme);
          localStorage.setItem("viewDensity", viewDensity);
          document.documentElement.setAttribute("data-theme", theme);
          window.dispatchEvent(
            new CustomEvent("torrentarr:configuration", {
              detail: event.application,
            }),
          );
          if (previous && previous.webui.urlBase !== next.urlBase) {
            const oldBase = previous.webui.urlBase;
            const suffix = window.location.pathname.startsWith(oldBase)
              ? window.location.pathname.slice(oldBase.length)
              : "/ui";
            window.history.replaceState(
              null,
              "",
              next.urlBase +
                suffix +
                window.location.search +
                window.location.hash,
            );
          }
          if (
            previous &&
            !next.behindHttpsProxy &&
            (previous.webui.port !== next.port ||
              previous.webui.host !== next.host)
          ) {
            const target = new URL(window.location.href);
            if (next.host !== "0.0.0.0" && next.host !== "::")
              target.hostname = next.host;
            target.port = String(next.port);
            target.pathname = next.urlBase + "/ui";
            window.location.assign(target.href);
          }
          if (previous && previous.webui.authRevision !== next.authRevision)
            window.dispatchEvent(new Event("torrentarr:authentication"));
          previous = event;
        }, controller.signal);
      } catch {
        /* Reconnect after listener changes or transient failures. */
      }
      if (!controller.signal.aborted)
        retry = setTimeout(() => void connect(), 2000);
    };
    void connect();
    return () => {
      controller.abort();
      if (retry) clearTimeout(retry);
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
      localStorage.setItem("viewDensity", value);
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
      localStorage.setItem("theme", value);
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
