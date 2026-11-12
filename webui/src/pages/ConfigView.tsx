import type { ConfigDestination } from "../utils/navigateTab";
import { ConfigFieldSearch } from "./config/ConfigFieldSearch";
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type JSX,
} from "react";
import { produce } from "immer";
import equal from "fast-deep-equal";
import { get, set, unset } from "lodash-es";
import {
  ConfigApiError,
  getConfig,
  getConfigSchema,
  refreshUrlBaseFromMeta,
  updateConfig,
  restartProcess,
} from "../api/client";
import type { ConfigDocument, ConfigRestartRequirement } from "../api/types";
import { useToast } from "../context/ToastContext";
import {
  getCategoryCrossSectionIssues,
  getCategoryOverlapWarnings,
} from "../config/categoryConfigValidation";
import { arrTypeLabel } from "../config/arrSections";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { IconImage } from "../components/IconImage";
import { ShellDialog } from "../components/ShellDialog";
import AddIcon from "../icons/plus.svg";
import RadarrIcon from "../icons/radarr.svg";
import SonarrIcon from "../icons/sonarr.svg";
import LidarrIcon from "../icons/lidarr.svg";
import ReadarrIcon from "../icons/readarr.svg";
import {
  configureConfigFields,
  getClientFields,
  getSchemaDefaults,
  AUTH_SETTINGS_FIELDS,
  SETTINGS_FIELDS,
  WEB_SETTINGS_FIELDS,
} from "./config/configFields";
import {
  buildSectionChanges,
  buildSectionDeleteChanges,
  ensureArrDefaults,
  flatten,
  focusFirstValidationError,
  formatValidationErrors,
  getValue,
  parseFieldValue,
  prunePendingRenames,
  resolveSectionDiskKey,
  sectionKeysFromChanges,
  setValue,
  validationErrorsFromApi,
} from "./config/configDocumentUtils";
import {
  validateSectionsForSave,
  validateFieldGroup,
  sectionHasCategoryChanges,
} from "./config/configValidation";
import {
  QBIT_SECTION_REGEX,
  SERVARR_SECTION_REGEX,
  type FieldDefinition,
  type ValidationError,
} from "./config/configTypes";
import {
  ArrInstanceModal,
  ConfigModalPortal,
  QbitInstanceModal,
  SetPasswordModal,
  SimpleConfigModal,
} from "./config/configModals";
import {
  formatConfigSaveMessage,
  shouldRefreshMetaAfterSave,
} from "./config/configSaveResult";

interface ConfigViewProps {
  active?: boolean;
  onDirtyChange?: (dirty: boolean) => void;
  destination?: ConfigDestination;
}

type PendingSectionDelete = {
  kind: "qbit" | "arr";
  key: string;
};

const INTEGRATION_TYPES = [
  { type: "radarr", icon: RadarrIcon },
  { type: "sonarr", icon: SonarrIcon },
  { type: "lidarr", icon: LidarrIcon },
  { type: "readarr", icon: ReadarrIcon },
] as const;

export function ConfigView(props?: ConfigViewProps): JSX.Element {
  const { active = true, onDirtyChange, destination } = props ?? {};
  const { push } = useToast();
  const [application, setApplication] = useState<
    import("../api/types").ConfigurationApplicationStatus | null
  >(null);
  useEffect(() => {
    const receive = (event: Event) => {
      const next = (
        event as CustomEvent<
          import("../api/types").ConfigurationApplicationStatus
        >
      ).detail;
      setApplication((current) =>
        !current || next.acceptedRevision >= current.acceptedRevision
          ? next
          : current,
      );
    };
    window.addEventListener("torrentarr:configuration", receive);
    return () =>
      window.removeEventListener("torrentarr:configuration", receive);
  }, []);
  const [originalConfig, setOriginalConfig] = useState<ConfigDocument | null>(
    null,
  );
  const [formState, setFormState] = useState<ConfigDocument | null>(null);
  useEffect(() => {
    const receive = (event: Event) => {
      const { path, maintenance } = (
        event as CustomEvent<{ path: string[]; maintenance: unknown }>
      ).detail;
      const merge = (current: ConfigDocument | null) =>
        current &&
        produce(current, (draft) => {
          set(draft, [...path, "Maintenance"], structuredClone(maintenance));
        });
      setOriginalConfig(merge);
      setFormState(merge);
    };
    window.addEventListener("torrentarr:manage-saved", receive);
    return () => window.removeEventListener("torrentarr:manage-saved", receive);
  }, []);

  const [loading, setLoading] = useState(false);
  const [, setSavingSection] = useState<string | null>(null);
  const [pendingRenames, setPendingRenames] = useState<Map<string, string>>(
    new Map(),
  );
  const [pendingDelete, setPendingDelete] =
    useState<PendingSectionDelete | null>(null);
  const [deletingSection, setDeletingSection] = useState(false);
  const [confirmDisableAuth, setConfirmDisableAuth] = useState(false);
  const [pendingRestarts, setPendingRestarts] = useState<
    ConfigRestartRequirement[]
  >([]);
  const [confirmRestart, setConfirmRestart] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const recordRestartRequirements = useCallback(
    (requirements: ConfigRestartRequirement[] = []) => {
      if (!requirements.length) return;
      setPendingRestarts((previous) =>
        [...previous, ...requirements].filter(
          (item, index, all) =>
            all.findIndex(
              (other) =>
                other.category === item.category && other.kind === item.kind,
            ) === index,
        ),
      );
      setConfirmRestart(true);
    },
    [],
  );
  const restartSavedComponents = async () => {
    if (restarting) return;
    setRestarting(true);
    setConfirmRestart(false);
    for (const requirement of pendingRestarts) {
      try {
        const result = await restartProcess(
          requirement.category,
          requirement.kind,
        );
        if (result.status !== "restarted" || result.restarted?.length === 0)
          throw new Error(`${requirement.component} did not restart`);
        setPendingRestarts((previous) =>
          previous.filter((item) => item !== requirement),
        );
      } catch (error) {
        push(
          error instanceof Error
            ? error.message
            : `Unable to restart ${requirement.component}`,
          "error",
        );
      }
    }
    setRestarting(false);
  };

  const loadConfig = useCallback(async () => {
    setLoading(true);
    try {
      const [config, schema] = await Promise.all([
        getConfig(),
        getConfigSchema().catch(() => null),
      ]);
      configureConfigFields(schema ?? undefined);
      setOriginalConfig(config);
      // Deep clone config for form state (immer will handle immutability from here)
      setFormState(config ? JSON.parse(JSON.stringify(config)) : null);
      // Clear pending renames when config is loaded
      setPendingRenames(new Map());
    } catch (error) {
      push(
        error instanceof Error ? error.message : "Failed to load configuration",
        "error",
      );
    } finally {
      setLoading(false);
    }
  }, [push]);

  useEffect(() => {
    const id = window.setTimeout(() => {
      void loadConfig();
    }, 0);
    return () => window.clearTimeout(id);
  }, [loadConfig]);

  const handleFieldChange = useCallback(
    (path: string[], def: FieldDefinition, raw: unknown) => {
      if (!formState) return;
      const parsed = parseFieldValue(def, raw);
      if (
        path.join(".") === "WebUI.AuthDisabled" &&
        parsed === true &&
        getValue(formState, path) !== true
      ) {
        setConfirmDisableAuth(true);
        return;
      }

      setFormState(
        produce(formState, (draft) => {
          setValue(draft, path, parsed);
        }),
      );
    },
    [formState],
  );

  const arrSections = useMemo(() => {
    if (!formState) return [] as Array<[string, ConfigDocument]>;
    return Object.entries(formState).filter(
      ([key, value]) =>
        SERVARR_SECTION_REGEX.test(key) && value && typeof value === "object",
    ) as Array<[string, ConfigDocument]>;
  }, [formState]);

  const torrentClients = useMemo(() => {
    if (!formState) return [];
    return [
      ...Object.entries(formState)
        .filter(
          ([key, value]) =>
            QBIT_SECTION_REGEX.test(key) && value && typeof value === "object",
        )
        .map(([name, value]) => ({
          name,
          value: value as ConfigDocument,
          legacy: true,
        })),
      ...Object.entries((formState.TorrentClient ?? {}) as ConfigDocument).map(
        ([name, value]) => ({
          name,
          value: value as ConfigDocument,
          legacy: false,
        }),
      ),
    ].sort((a, b) =>
      a.name.localeCompare(b.name, undefined, {
        numeric: true,
        sensitivity: "base",
      }),
    );
  }, [formState]);
  const clientGroups = useMemo(() => {
    const groups = new Map<string, typeof torrentClients>([
      ["qbittorrent", []],
    ]);
    for (const client of torrentClients) {
      const type =
        String(client.value.Type || "qbittorrent")
          .trim()
          .toLowerCase() || "qbittorrent";
      if (!groups.has(type)) groups.set(type, []);
      groups.get(type)!.push(client);
    }
    return [...groups].sort(([a], [b]) =>
      a === b
        ? 0
        : a === "qbittorrent"
          ? -1
          : b === "qbittorrent"
            ? 1
            : a.localeCompare(b),
    );
  }, [torrentClients]);
  const categoryOverlapWarnings = useMemo(
    () => getCategoryOverlapWarnings(formState),
    [formState],
  );

  const groupedArrSections = useMemo(() => {
    const groups: Array<{
      label: string;
      type: "radarr" | "sonarr" | "lidarr" | "readarr" | "other";
      items: Array<[string, ConfigDocument]>;
    }> = [];
    const sorted = [...arrSections].sort((a, b) =>
      a[0].localeCompare(b[0], undefined, {
        numeric: true,
        sensitivity: "base",
      }),
    );
    const radarr: Array<[string, ConfigDocument]> = [];
    const sonarr: Array<[string, ConfigDocument]> = [];
    const lidarr: Array<[string, ConfigDocument]> = [];
    const readarr: Array<[string, ConfigDocument]> = [];
    const others: Array<[string, ConfigDocument]> = [];
    for (const entry of sorted) {
      const [key] = entry;
      const keyLower = key.toLowerCase();
      if (keyLower.startsWith("radarr")) {
        radarr.push(entry);
      } else if (keyLower.startsWith("sonarr")) {
        sonarr.push(entry);
      } else if (keyLower.startsWith("lidarr")) {
        lidarr.push(entry);
      } else if (keyLower.startsWith("readarr")) {
        readarr.push(entry);
      } else {
        others.push(entry);
      }
    }

    groups.push({
      label: `${arrTypeLabel("radarr")} Instances`,
      type: "radarr",
      items: radarr,
    });
    groups.push({
      label: `${arrTypeLabel("sonarr")} Instances`,
      type: "sonarr",
      items: sonarr,
    });
    groups.push({
      label: `${arrTypeLabel("lidarr")} Instances`,
      type: "lidarr",
      items: lidarr,
    });
    groups.push({
      label: `${arrTypeLabel("readarr")} Instances`,
      type: "readarr",
      items: readarr,
    });
    if (others.length) {
      groups.push({ label: "Other Instances", type: "other", items: others });
    }
    return groups;
  }, [arrSections]);
  const [activeClient, setActiveClient] = useState<string | null>(null);
  const clientValidationErrors = useMemo(() => {
    const errors: ValidationError[] = [];
    if (activeClient && formState) {
      const section = get(formState, [
        "TorrentClient",
        activeClient,
      ]) as ConfigDocument;
      validateFieldGroup(
        errors,
        getClientFields(),
        section,
        ["TorrentClient", activeClient],
        { root: formState, section },
      );
    }
    return errors;
  }, [activeClient, formState]);

  const [deleteClient, setDeleteClient] = useState<string | null>(null);
  const [clientSaving, setClientSaving] = useState(false);
  const [activeArrKey, setActiveArrKey] = useState<string | null>(null);
  const [activeQbitKey, setActiveQbitKey] = useState<string | null>(null);
  const [isSettingsOpen, setSettingsOpen] = useState(true);
  const destinationFocus = useRef<{
    fieldPath: string;
    monitorPath?: string;
  } | null>(null);
  const handledDestination = useRef<ConfigDestination | undefined>(undefined);
  const editorRef = useRef<HTMLDivElement>(null);
  const [editorRequest, setEditorRequest] = useState(0);
  useEffect(() => {
    const editor = editorRef.current;
    if (!editorRequest || !editor) return;
    const headerHeight =
      document.querySelector(".appbar")?.getBoundingClientRect().height ?? 0;
    const requested = destinationFocus.current;
    destinationFocus.current = null;
    const field = requested
      ? [...editor.querySelectorAll<HTMLElement>("[data-field-path]")].find(
          (element) => element.dataset.fieldPath === requested.fieldPath,
        )
      : undefined;
    const row =
      requested?.monitorPath && field
        ? [...field.querySelectorAll<HTMLElement>("[data-monitor-path]")].find(
            (element) => element.dataset.monitorPath === requested.monitorPath,
          )
        : undefined;
    const target = row || field || editor;
    for (
      let element: HTMLElement | null = target;
      element && element !== editor;
      element = element.parentElement
    ) {
      if (element instanceof HTMLDetailsElement) element.open = true;
    }
    const focus = target.querySelector<HTMLElement>(
      target instanceof HTMLDetailsElement
        ? ":scope > summary"
        : "input:not([type=hidden]), select, textarea",
    );
    target.style.scrollMarginTop = `${headerHeight + 12}px`;
    (target === editor ? editor : focus ?? editor).focus({
      preventScroll: true,
    });
    target.scrollIntoView?.({
      block: "start",
      behavior: "instant",
    });
  }, [editorRequest]);
  const [fieldSearch, setFieldSearch] = useState("");
  const [configTab, setConfigTab] = useState<
    "settings" | "web" | "auth" | "clients" | "libraries"
  >("settings");
  const [integrationPickerOpen, setIntegrationPickerOpen] = useState(false);
  const [isWebSettingsOpen, setWebSettingsOpen] = useState(false);
  const [isAuthSettingsOpen, setAuthSettingsOpen] = useState(false);
  const [isSetPasswordOpen, setSetPasswordOpen] = useState(false);
  const [isDirty, setDirty] = useState(false);
  const [serverValidationErrors, setServerValidationErrors] = useState<
    ValidationError[]
  >([]);

  const mergeValidationErrors = useCallback(
    (
      clientErrors: ValidationError[],
      sectionKey: string,
    ): ValidationError[] => {
      if (!serverValidationErrors.length) {
        return clientErrors;
      }
      const allowedRoots = new Set<string>([sectionKey]);
      for (const [oldName, newName] of pendingRenames) {
        if (oldName === sectionKey || newName === sectionKey) {
          allowedRoots.add(oldName);
          allowedRoots.add(newName);
        }
      }
      const seen = new Set(clientErrors.map((error) => error.path.join(".")));
      const merged = [...clientErrors];
      for (const error of serverValidationErrors) {
        const root = error.path[0];
        if (!root || !allowedRoots.has(root)) {
          continue;
        }
        const key = error.path.join(".");
        if (!seen.has(key)) {
          merged.push(error);
          seen.add(key);
        }
      }
      return merged;
    },
    [serverValidationErrors, pendingRenames],
  );

  const sectionSaveGate = useCallback(
    (
      sectionKey: string,
    ): {
      saveDisabled: boolean;
      saveBlockedReason?: string;
      validationErrors: ValidationError[];
    } => {
      const clientErrors = validateSectionsForSave(
        formState,
        [sectionKey],
        originalConfig,
        false,
      );
      const errors = mergeValidationErrors(clientErrors, sectionKey);
      if (!errors.length) {
        return { saveDisabled: false, validationErrors: [] };
      }
      return {
        saveDisabled: true,
        saveBlockedReason: formatValidationErrors(errors),
        validationErrors: errors,
      };
    },
    [formState, originalConfig, mergeValidationErrors],
  );

  const arrSaveGate = useMemo(
    () =>
      activeArrKey
        ? sectionSaveGate(activeArrKey)
        : { saveDisabled: false, validationErrors: [] as ValidationError[] },
    [activeArrKey, sectionSaveGate],
  );
  const qbitSaveGate = useMemo(
    () =>
      activeQbitKey
        ? sectionSaveGate(activeQbitKey)
        : { saveDisabled: false, validationErrors: [] as ValidationError[] },
    [activeQbitKey, sectionSaveGate],
  );
  const settingsSaveGate = useMemo(
    () =>
      isSettingsOpen
        ? sectionSaveGate("Settings")
        : { saveDisabled: false, validationErrors: [] as ValidationError[] },
    [isSettingsOpen, sectionSaveGate],
  );
  const webSaveGate = useMemo(
    () =>
      isWebSettingsOpen || isAuthSettingsOpen
        ? sectionSaveGate("WebUI")
        : { saveDisabled: false, validationErrors: [] as ValidationError[] },
    [isWebSettingsOpen, isAuthSettingsOpen, sectionSaveGate],
  );

  useEffect(() => {
    const id = window.setTimeout(() => {
      setServerValidationErrors([]);
    }, 0);
    return () => window.clearTimeout(id);
  }, [formState]);

  useEffect(() => {
    if (!formState || !originalConfig) {
      const id = window.setTimeout(() => {
        setDirty(false);
      }, 0);
      return () => window.clearTimeout(id);
    }
    const flattenedOriginal = flatten(originalConfig);
    const flattenedCurrent = flatten(formState);

    // Keys that are managed dynamically and should not trigger dirty state
    const liveKeys = new Set([
      "WebUI.LiveArr",
      "WebUI.Theme",
      "WebUI.ViewDensity",
    ]);

    let dirty = false;
    for (const [key, value] of Object.entries(flattenedCurrent)) {
      // Skip live WebUI settings
      if (liveKeys.has(key)) continue;

      const originalValue = flattenedOriginal[key];
      // Use fast-deep-equal for accurate comparison (handles arrays, objects, etc.)
      if (!equal(value, originalValue)) {
        dirty = true;
        break;
      }
    }
    if (!dirty) {
      for (const key of Object.keys(flattenedOriginal)) {
        // Skip live WebUI settings
        if (liveKeys.has(key)) continue;

        if (!(key in flattenedCurrent)) {
          dirty = true;
          break;
        }
      }
    }
    const id = window.setTimeout(() => {
      setDirty(dirty);
    }, 0);
    return () => window.clearTimeout(id);
  }, [formState, originalConfig]);

  useEffect(() => {
    onDirtyChange?.(isDirty);
  }, [isDirty, onDirtyChange]);

  useEffect(() => {
    if (!isDirty) return;
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", handleBeforeUnload);
    return () => {
      window.removeEventListener("beforeunload", handleBeforeUnload);
    };
  }, [isDirty]);

  useEffect(() => {
    return () => {
      onDirtyChange?.(false);
    };
  }, [onDirtyChange]);

  useEffect(() => {
    const anyModalOpen = isSetPasswordOpen || confirmDisableAuth;
    if (!active || !anyModalOpen) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        if (confirmDisableAuth) {
          setConfirmDisableAuth(false);
          return;
        }
        const target = event.target;
        if (
          target instanceof HTMLInputElement ||
          target instanceof HTMLTextAreaElement ||
          target instanceof HTMLSelectElement
        ) {
          return;
        }
        setSetPasswordOpen(false);
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [
    active,
    activeArrKey,
    activeQbitKey,
    activeClient,
    isSettingsOpen,
    isWebSettingsOpen,
    isAuthSettingsOpen,
    confirmDisableAuth,
    isSetPasswordOpen,
  ]);

  useEffect(() => {
    if (!activeArrKey) return;
    if (!arrSections.some(([key]) => key === activeArrKey)) {
      const id = window.setTimeout(() => {
        setActiveArrKey(null);
      }, 0);
      return () => window.clearTimeout(id);
    }
  }, [activeArrKey, arrSections]);

  const addArrInstance = useCallback(
    (type: "radarr" | "sonarr" | "lidarr" | "readarr") => {
      if (!formState) return;
      const prefix = type.charAt(0).toUpperCase() + type.slice(1);
      let index = 1;
      let key = `${prefix}-${index}`;
      while (
        Object.keys(formState).some(
          (name) => name.toLowerCase() === key.toLowerCase(),
        )
      ) {
        index += 1;
        key = `${prefix}-${index}`;
      }
      const defaults = ensureArrDefaults(type);
      if (defaults && typeof defaults === "object") {
        (defaults as Record<string, unknown>).Name = key;
      }
      setFormState(
        produce(formState, (draft) => {
          draft[key] = defaults;
        }),
      );
      setActiveArrKey(key);
      setEditorRequest((request) => request + 1);
    },
    [formState],
  );
  const requestDeleteArrInstance = useCallback(
    (key: string) => {
      if (!formState || !(key in formState)) return;
      const keyLower = key.toLowerCase();
      if (
        !keyLower.startsWith("radarr") &&
        !keyLower.startsWith("sonarr") &&
        !keyLower.startsWith("lidarr") &&
        !keyLower.startsWith("readarr")
      ) {
        return;
      }
      setActiveArrKey(null);
      setPendingDelete({ kind: "arr", key });
    },
    [formState],
  );

  const requestDeleteQbitInstance = useCallback(
    (key: string) => {
      if (!formState || !(key in formState)) return;
      setActiveQbitKey(null);
      setPendingDelete({ kind: "qbit", key });
    },
    [formState],
  );

  const handleRenameSection = useCallback(
    (oldName: string, rawNewName: string) => {
      if (!formState) return;
      const newName = rawNewName.trim();
      if (!newName || newName === oldName) {
        return;
      }
      if (formState[newName]) {
        push(`An instance named "${newName}" already exists`, "error");
        return;
      }
      setFormState(
        produce(formState, (draft) => {
          const section = draft[oldName];
          delete draft[oldName];
          draft[newName] = section;
          if (section && typeof section === "object") {
            (section as Record<string, unknown>).Name = newName;
          }
        }),
      );
      // Track this rename to ensure old section is fully deleted on save
      setPendingRenames((prev) => {
        const next = new Map(prev);
        const diskName =
          [...prev].find(([, target]) => target === oldName)?.[0] ?? oldName;
        next.delete(oldName);
        if (diskName === newName) next.delete(diskName);
        else next.set(diskName, newName);
        return next;
      });
      if (activeArrKey === oldName) {
        setActiveArrKey(newName);
      }
    },
    [formState, push, activeArrKey],
  );

  const handleRenameQbitSection = useCallback(
    (oldName: string, rawNewName: string) => {
      if (!formState) return;
      const newName = rawNewName.trim();
      if (!newName || newName === oldName) {
        return;
      }
      if (formState[newName]) {
        push(`An instance named "${newName}" already exists`, "error");
        return;
      }
      setFormState(
        produce(formState, (draft) => {
          const section = draft[oldName];
          delete draft[oldName];
          draft[newName] = section;
        }),
      );
      // Track this rename to ensure old section is fully deleted on save
      setPendingRenames((prev) => {
        const next = new Map(prev);
        const diskName =
          [...prev].find(([, target]) => target === oldName)?.[0] ?? oldName;
        next.delete(oldName);
        if (diskName === newName) next.delete(diskName);
        else next.set(diskName, newName);
        return next;
      });
      if (activeQbitKey === oldName) {
        setActiveQbitKey(newName);
      }
    },
    [formState, push, activeQbitKey],
  );

  const applyConfigSaveResult = useCallback(
    async (
      configReloaded: boolean,
      reloadType: string,
      affectedInstances: string[] | undefined,
      savedSectionKeys: Iterable<string> | "all",
      changedKeys: readonly string[] = [],
      applicationStatus?: import("../api/types").ConfigurationApplicationStatus,
    ) => {
      const message = formatConfigSaveMessage(
        reloadType,
        configReloaded,
        affectedInstances,
        changedKeys,
        applicationStatus,
      );
      push(message, "success");

      if (shouldRefreshMetaAfterSave(reloadType, changedKeys)) {
        try {
          await refreshUrlBaseFromMeta();
        } catch {
          // meta refresh failed; config was saved — user can reload manually if needed
        }
      }

      if (configReloaded && "caches" in window) {
        try {
          const cacheNames = await caches.keys();
          await Promise.all(
            cacheNames.map((cacheName) => caches.delete(cacheName)),
          );
        } catch {
          // cache clear failed, non-critical
        }
      }

      const saved = await getConfig();
      const keys =
        savedSectionKeys === "all" ? Object.keys(saved) : [...savedSectionKeys];
      setOriginalConfig(saved);
      setFormState((previous) => {
        if (!previous || savedSectionKeys === "all")
          return structuredClone(saved);
        return produce(previous, (draft) => {
          for (const key of keys) {
            if (Object.hasOwn(saved, key))
              draft[key] = structuredClone(saved[key]);
            else delete draft[key];
          }
        });
      });
      setPendingRenames((prev) => prunePendingRenames(prev, savedSectionKeys));
    },
    [push],
  );

  const persistPendingDelete = useCallback(async () => {
    if (!pendingDelete || !formState || deletingSection) {
      return;
    }
    const { kind, key } = pendingDelete;
    if (!(key in formState)) {
      setPendingDelete(null);
      return;
    }

    setDeletingSection(true);
    try {
      const changes = buildSectionDeleteChanges(
        key,
        originalConfig,
        pendingRenames,
      );
      if (changes === null) {
        const diskKey = resolveSectionDiskKey(key, pendingRenames);
        setFormState(
          produce(formState, (draft) => {
            delete draft[key];
          }),
        );
        setPendingRenames((prev) => prunePendingRenames(prev, [key, diskKey]));
        if (kind === "qbit") {
          setActiveQbitKey(null);
        } else {
          setActiveArrKey(null);
        }
        setPendingDelete(null);
        push(`${key} removed`, "success");
        return;
      }

      const {
        configReloaded,
        reloadType,
        affectedInstances,
        application: applicationStatus,
        restartRequirements,
      } = await updateConfig({
        changes,
      });
      recordRestartRequirements(restartRequirements);
      setServerValidationErrors([]);
      if (kind === "qbit") {
        setActiveQbitKey(null);
      } else {
        setActiveArrKey(null);
      }
      setPendingDelete(null);
      const savedKeys = sectionKeysFromChanges(changes);
      savedKeys.push(key, resolveSectionDiskKey(key, pendingRenames));
      await applyConfigSaveResult(
        configReloaded,
        reloadType,
        affectedInstances,
        savedKeys,
        Object.keys(changes),
        applicationStatus,
      );
    } catch (error) {
      setPendingDelete(null);
      if (error instanceof ConfigApiError && error.validationErrors.length) {
        const apiErrors = validationErrorsFromApi(error.validationErrors);
        setServerValidationErrors(apiErrors);
        push(error.message, "error");
        return;
      }
      push(
        error instanceof Error
          ? error.message
          : "Failed to delete configuration section",
        "error",
      );
    } finally {
      setDeletingSection(false);
    }
  }, [
    pendingDelete,
    formState,
    deletingSection,
    originalConfig,
    pendingRenames,
    push,
    applyConfigSaveResult,
    recordRestartRequirements,
  ]);

  const saveSection = useCallback(
    async (sectionKey: string, keepProtectionOff = false): Promise<boolean> => {
      if (!formState) return false;
      setSavingSection(sectionKey);
      try {
        const validationErrors = validateSectionsForSave(
          formState,
          [sectionKey],
          originalConfig,
          false,
        );
        if (validationErrors.length) {
          push(formatValidationErrors(validationErrors), "error");
          focusFirstValidationError(validationErrors);
          return false;
        }

        const changes = buildSectionChanges(
          formState,
          originalConfig,
          sectionKey,
          pendingRenames,
        );
        const freeSpaceField = `${sectionKey}.FreeSpace`;
        if (keepProtectionOff)
          changes[freeSpaceField] =
            get(formState, [sectionKey, "FreeSpace"]) ?? [];
        if (Object.keys(changes).length === 0) {
          push(`No changes detected for ${sectionKey}`, "info");
          return false;
        }

        const renames = Object.fromEntries(
          [...pendingRenames].filter(
            ([oldName, newName]) =>
              (oldName === sectionKey || newName === sectionKey) &&
              oldName !== newName,
          ),
        );
        const {
          configReloaded,
          reloadType,
          affectedInstances,
          warning,
          application: applicationStatus,
          restartRequirements,
        } = await updateConfig({
          changes,
          ...(Object.keys(renames).length ? { renames } : {}),
          ...(keepProtectionOff
            ? { keepFreeSpaceProtectionOff: freeSpaceField }
            : {}),
        });
        recordRestartRequirements(restartRequirements);
        if (warning) push(warning, "warning");
        setServerValidationErrors([]);
        const savedKeys = sectionKeysFromChanges(changes);
        for (const [oldName, newName] of pendingRenames) {
          if (oldName === sectionKey || newName === sectionKey) {
            savedKeys.push(oldName, newName);
          }
        }
        await applyConfigSaveResult(
          configReloaded,
          reloadType,
          affectedInstances,
          savedKeys,
          Object.keys(changes),
          applicationStatus,
        );
        return true;
      } catch (error) {
        if (error instanceof ConfigApiError && error.validationErrors.length) {
          const apiErrors = validationErrorsFromApi(error.validationErrors);
          setServerValidationErrors(apiErrors);
          push(error.message, "error");
          focusFirstValidationError(apiErrors);
          return false;
        }
        push(
          error instanceof Error
            ? error.message
            : "Failed to update configuration",
          "error",
        );
        return false;
      } finally {
        setSavingSection(null);
      }
    },
    [
      formState,
      originalConfig,
      pendingRenames,
      push,
      applyConfigSaveResult,
      recordRestartRequirements,
    ],
  );

  const saveClient = async (
    name: string,
    remove = false,
    keepProtectionOff = false,
  ): Promise<boolean> => {
    if (!formState || clientSaving) return false;
    setClientSaving(true);
    const path = ["TorrentClient", name];
    try {
      const value = get(formState, path) as ConfigDocument;
      const changes = remove
        ? { [`TorrentClient.${name}`]: null }
        : buildSectionChanges(
            formState,
            originalConfig,
            `TorrentClient.${name}`,
            new Map(),
          );
      if (!remove) {
        const errors: ValidationError[] = [];
        validateFieldGroup(errors, getClientFields(), value, path, {
          root: formState,
          section: value,
        });
        if (
          sectionHasCategoryChanges(
            `TorrentClient.${name}`,
            formState,
            originalConfig,
          )
        ) {
          errors.push(...getCategoryCrossSectionIssues(formState));
        }
        if (errors.length) {
          push(formatValidationErrors(errors), "error");
          focusFirstValidationError(errors);
          return false;
        }
        const host = String(value?.Host ?? "").trim();
        const port = Number(value?.Port);
        if (!host || !Number.isInteger(port) || port < 1 || port > 65535)
          throw new Error("Enter a host and a port between 1 and 65535.");
      }
      const freeSpaceField = `TorrentClient.${name}.FreeSpace`;
      if (keepProtectionOff) changes[freeSpaceField] = value.FreeSpace ?? [];
      const result = Object.keys(changes).length
        ? await updateConfig({
            changes,
            ...(keepProtectionOff
              ? { keepFreeSpaceProtectionOff: freeSpaceField }
              : {}),
          })
        : null;
      recordRestartRequirements(result?.restartRequirements);
      const saved = await getConfig();
      const merge = (previous: ConfigDocument | null) =>
        produce(previous ?? {}, (draft) => {
          const value = get(saved, path);
          if (value === undefined) unset(draft, path);
          else set(draft, path, structuredClone(value));
        });
      setOriginalConfig(merge);
      setFormState(merge);
      setActiveClient(null);
      setDeleteClient(null);
      if (result?.warning) push(result.warning, "warning");
      push(
        remove
          ? "Torrent client removed"
          : result
            ? formatConfigSaveMessage(
                result.reloadType,
                result.configReloaded,
                result.affectedInstances,
                Object.keys(changes),
                result.application,
              )
            : "Configuration saved",
        "success",
      );
      return true;
    } catch (error) {
      push(
        error instanceof Error
          ? error.message
          : "Unable to save torrent client",
        "error",
      );
      return false;
    } finally {
      setClientSaving(false);
    }
  };
  const addClient = (type: string) => {
    let index = 1;
    const names = torrentClients.map((client) => client.name.toLowerCase());
    while (
      names.includes(`client-${index}`) ||
      Object.keys(formState ?? {}).some(
        (name) => name.toLowerCase() === `client-${index}`,
      )
    )
      index++;
    const name = `client-${index}`;
    setFormState((previous) =>
      produce(previous ?? {}, (draft) => {
        set(draft, ["TorrentClient", name], {
          ...getSchemaDefaults("TorrentClient"),
          Type: type,
        });
      }),
    );
    setActiveClient(name);
    setEditorRequest((request) => request + 1);
  };

  const chooseEditor = (open: () => void) => {
    setActiveArrKey(null);
    setActiveQbitKey(null);
    setActiveClient(null);
    setSettingsOpen(false);
    setWebSettingsOpen(false);
    setAuthSettingsOpen(false);
    setFieldSearch("");
    open();
  };
  useEffect(() => {
    if (activeClient || activeQbitKey) setConfigTab("clients");
    else if (activeArrKey) setConfigTab("libraries");
  }, [activeClient, activeQbitKey, activeArrKey]);
  useEffect(() => {
    if (
      !active ||
      !formState ||
      !destination ||
      handledDestination.current === destination
    )
      return;
    handledDestination.current = destination;
    const clientName = (
      pendingRenames.get(destination.client ?? "") ?? destination.client
    )?.toLowerCase();
    const matchingClients = torrentClients.filter(
      ({ name }) => name.toLowerCase() === clientName,
    );
    const client =
      matchingClients.find(({ legacy }) => !legacy) ?? matchingClients[0];
    const arrName = (
      pendingRenames.get(destination.arr ?? "") ?? destination.arr
    )?.toLowerCase();
    const arr = arrSections.find(([name]) => name.toLowerCase() === arrName);
    chooseEditor(() => {
      const section =
        client && !client.legacy
          ? `TorrentClient.${client.name}`
          : client?.name ?? arr?.[0] ?? "Settings";
      if (client) {
        setConfigTab("clients");
        if (client.legacy) setActiveQbitKey(client.name);
        else setActiveClient(client.name);
      } else if (arr) {
        setConfigTab("libraries");
        setActiveArrKey(arr[0]);
      } else {
        setConfigTab("settings");
        setSettingsOpen(true);
      }
      if (destination.field)
        destinationFocus.current = {
          fieldPath: `${section}.${destination.field}`,
          monitorPath: destination.monitorPath,
        };
      setEditorRequest((request) => request + 1);
    });
  }, [
    active,
    formState,
    destination,
    torrentClients,
    arrSections,
    pendingRenames,
  ]);

  const openConfigTab = (tab: typeof configTab) => {
    setConfigTab(tab);
    chooseEditor(() => {
      if (tab === "settings") setSettingsOpen(true);
      else if (tab === "web") setWebSettingsOpen(true);
      else if (tab === "auth") setAuthSettingsOpen(true);
    });
  };
  const sectionDirty = (key: string) =>
    !equal(formState?.[key], originalConfig?.[key]);

  const applicationNotice = application && application.state !== "applied" && (
    <p role="status" className="card-body">
      {application.error ??
        (application.state === "pending"
          ? "Configuration saved; waiting for active operations to finish"
          : "Some configuration changes could not be applied")}
      {Object.entries(application.components)
        .filter(([, value]) => value.error)
        .map(([name, value]) => (
          <span key={name}>
            {" "}
            {name}: {value.error}
          </span>
        ))}
    </p>
  );

  if (loading || !formState) {
    return (
      <section className="card" aria-label="Config">
        {applicationNotice}
        {pendingRestarts.length > 0 && (
          <p role="status" className="card-body">
            Pending restart:{" "}
            {pendingRestarts.map((item) => item.component).join(", ")}.{" "}
            <button
              type="button"
              className="btn small"
              disabled={restarting}
              onClick={() => setConfirmRestart(true)}
            >
              {restarting ? "Restarting…" : "Retry restart"}
            </button>
          </p>
        )}
        <div className="card-body">
          <div className="loading">
            <span className="spinner" /> Loading configuration…
          </div>
        </div>
      </section>
    );
  }

  return (
    <>
      <section className="card config-workspace" aria-label="Config">
        {isDirty && (
          <div className="card-header">
            <span className="badge">Unsaved changes</span>
          </div>
        )}
        {applicationNotice}
        {pendingRestarts.length > 0 && (
          <p role="status" className="card-body">
            Pending restart:{" "}
            {pendingRestarts.map((item) => item.component).join(", ")}.{" "}
            <button
              type="button"
              className="btn small"
              disabled={restarting}
              onClick={() => setConfirmRestart(true)}
            >
              {restarting ? "Restarting…" : "Retry restart"}
            </button>
          </p>
        )}
        <div className="config-workspace__layout">
          <nav className="config-tabs" aria-label="Configuration sections">
            {(
              [
                ["settings", "Settings"],
                ["web", "Web Settings"],
                ["auth", "Authentication"],
                ["clients", "Torrent Clients"],
                ["libraries", "Integrations"],
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                type="button"
                aria-current={configTab === key ? "page" : undefined}
                className={configTab === key ? "active" : ""}
                onClick={() => openConfigTab(key)}
              >
                {label}
                {(
                  key === "settings"
                    ? sectionDirty("Settings")
                    : key === "web" || key === "auth"
                      ? sectionDirty("WebUI")
                      : false
                )
                  ? " •"
                  : ""}
              </button>
            ))}
          </nav>
          {configTab === "clients" && (
            <section
              className="config-arr-overview"
              aria-label="Configured torrent clients"
            >
              {clientGroups.map(([type, clients]) => (
                <section
                  className="config-arr-group"
                  key={type}
                  aria-label={`${type === "qbittorrent" ? "qBittorrent" : type} Instances`}
                >
                  <header className="config-instance-header">
                    <h3>
                      {type === "qbittorrent" ? "qBittorrent" : type}{" "}
                      <span>
                        {clients.length}{" "}
                        {clients.length === 1 ? "instance" : "instances"}
                      </span>
                    </h3>
                    <button
                      className="btn small"
                      type="button"
                      onClick={() => chooseEditor(() => addClient(type))}
                    >
                      <IconImage src={AddIcon} />
                      Add Instance
                    </button>
                  </header>
                  {type !== "qbittorrent" && (
                    <p className="hint">
                      Connection requires a supported client type.
                    </p>
                  )}
                  <div className="config-arr-list">
                    {clients.length === 0 && (
                      <p className="hint">No instances configured.</p>
                    )}
                    {clients.map(({ name, value, legacy }) => {
                      const active = legacy
                        ? activeQbitKey === name
                        : activeClient === name;
                      const original = legacy
                        ? originalConfig?.[name]
                        : get(originalConfig, ["TorrentClient", name]);
                      const categories = Array.isArray(value.ManagedCategories)
                        ? value.ManagedCategories
                        : [];
                      return (
                        <button
                          type="button"
                          key={`${legacy}:${name}`}
                          className={`config-arr-row${active ? " active" : ""}`}
                          aria-label={`Configure ${name}${torrentClients.filter((client) => client.name === name).length > 1 ? (legacy ? " (legacy)" : " (TorrentClient)") : ""}`}
                          aria-pressed={active}
                          onClick={() =>
                            chooseEditor(() => {
                              if (legacy) setActiveQbitKey(name);
                              else setActiveClient(name);
                              setEditorRequest((request) => request + 1);
                            })
                          }
                        >
                          <span className="config-arr-name">
                            {name}
                            {!equal(value, original) && (
                              <span className="config-arr-dirty">Unsaved</span>
                            )}
                          </span>
                          <span className="config-arr-cell">
                            <small>Address</small>
                            <span>
                              {String(value.Host || "Not configured")}
                              {value.Port ? `:${value.Port}` : ""}
                            </span>
                          </span>
                          <span className="config-arr-cell">
                            <small>Categories</small>
                            <span>
                              {categories.join(", ") || "None assigned"}
                            </span>
                          </span>
                          <span className="config-arr-cell">
                            <small>Scan interval</small>
                            <span>
                              {String(value.TorrentScanIntervalSeconds ?? 30)} s
                            </span>
                          </span>
                          <span
                            className={`config-arr-state${value.Disabled === true ? " config-arr-state--disabled" : ""}`}
                          >
                            {value.Disabled === true ? "Disabled" : "Enabled"}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </section>
              ))}
            </section>
          )}
          {configTab === "libraries" && (
            <section
              className="config-arr-overview"
              aria-label="Configured integrations"
            >
              <div className="config-arr-toolbar">
                <button
                  className="btn small"
                  type="button"
                  onClick={() => setIntegrationPickerOpen(true)}
                >
                  <IconImage src={AddIcon} />
                  Add Instance
                </button>
              </div>
              {groupedArrSections.map((group) => (
                <section
                  className="config-arr-group"
                  key={group.type}
                  aria-label={group.label}
                >
                  <header className="config-instance-header">
                    <h3>
                      {group.label.replace(/ Instances$/, "")}{" "}
                      <span>
                        {group.items.length}{" "}
                        {group.items.length === 1 ? "instance" : "instances"}
                      </span>
                    </h3>
                  </header>
                  <div className="config-arr-list">
                    {group.items.length === 0 && (
                      <p className="hint">No instances configured.</p>
                    )}
                    {group.items.map(([name, section]) => {
                      const managed = section.Managed !== false;
                      let address = "Not configured";
                      try {
                        const url = new URL(String(section.URI ?? ""));
                        address = url.host + url.pathname.replace(/\/$/, "");
                      } catch {
                        /* The editor shows URL validation errors. */
                      }
                      return (
                        <button
                          key={name}
                          type="button"
                          className={`config-arr-row${activeArrKey === name ? " active" : ""}`}
                          aria-label={`Configure ${name}`}
                          aria-pressed={activeArrKey === name}
                          onClick={() =>
                            chooseEditor(() => {
                              setActiveArrKey(name);
                              setEditorRequest((request) => request + 1);
                            })
                          }
                        >
                          <span className="config-arr-name">
                            {name}
                            {sectionDirty(name) && (
                              <span className="config-arr-dirty">Unsaved</span>
                            )}
                          </span>
                          <span className="config-arr-cell">
                            <small>Address</small>
                            <span>{address}</span>
                          </span>
                          <span className="config-arr-cell">
                            <small>Category</small>
                            <span>{String(section.Category || "Not set")}</span>
                          </span>
                          <span className="config-arr-cell">
                            <small>Mode</small>
                            <span>
                              {section.ProcessingOnly === true
                                ? "Torrents only"
                                : section.SearchOnly === true
                                  ? "Search only"
                                  : "Search + torrents"}
                            </span>
                          </span>
                          <span
                            className={`config-arr-state${managed ? "" : " config-arr-state--disabled"}`}
                          >
                            {managed ? "Enabled" : "Disabled"}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                </section>
              ))}
            </section>
          )}
          {active &&
            (activeArrKey ||
              activeQbitKey ||
              activeClient ||
              isSettingsOpen ||
              isWebSettingsOpen ||
              isAuthSettingsOpen) && (
              <div
                className="config-workspace__editor"
                ref={editorRef}
                tabIndex={-1}
              >
                <div className="field config-field-search">
                  <label htmlFor="config-field-search">
                    Find a setting in this section
                  </label>
                  <input
                    id="config-field-search"
                    type="search"
                    value={fieldSearch}
                    onChange={(event) => setFieldSearch(event.target.value)}
                    placeholder="Search field names and descriptions…"
                  />
                </div>
                <ConfigFieldSearch.Provider value={fieldSearch}>
                  {active && activeArrKey && formState ? (
                    <ArrInstanceModal
                      inline
                      keyName={activeArrKey}
                      diskKey={resolveSectionDiskKey(
                        activeArrKey,
                        pendingRenames,
                      )}
                      state={
                        (formState[activeArrKey] as ConfigDocument) ?? null
                      }
                      onChange={handleFieldChange}
                      onRename={handleRenameSection}
                      onClose={() => setActiveArrKey(null)}
                      onSave={() => saveSection(activeArrKey)}
                      onDelete={
                        /^(radarr|sonarr|lidarr|readarr)/i.test(activeArrKey)
                          ? () => requestDeleteArrInstance(activeArrKey)
                          : undefined
                      }
                      overlapWarnings={categoryOverlapWarnings}
                      saveDisabled={arrSaveGate.saveDisabled}
                      saveBlockedReason={arrSaveGate.saveBlockedReason}
                      validationErrors={arrSaveGate.validationErrors}
                    />
                  ) : null}
                  {active && isSettingsOpen ? (
                    <SimpleConfigModal
                      inline
                      title="Settings"
                      fields={SETTINGS_FIELDS}
                      state={formState}
                      basePath={[]}
                      onChange={handleFieldChange}
                      onClose={() => setSettingsOpen(false)}
                      onSave={() => saveSection("Settings")}
                      saveDisabled={settingsSaveGate.saveDisabled}
                      saveBlockedReason={settingsSaveGate.saveBlockedReason}
                      validationErrors={settingsSaveGate.validationErrors}
                    />
                  ) : null}
                  {active && isWebSettingsOpen ? (
                    <SimpleConfigModal
                      inline
                      title="Web Settings"
                      fields={WEB_SETTINGS_FIELDS}
                      state={formState}
                      basePath={[]}
                      onChange={handleFieldChange}
                      onClose={() => setWebSettingsOpen(false)}
                      onSave={() => saveSection("WebUI")}
                      showLiveSettings={true}
                      saveDisabled={webSaveGate.saveDisabled}
                      saveBlockedReason={webSaveGate.saveBlockedReason}
                      validationErrors={webSaveGate.validationErrors}
                    />
                  ) : null}
                  {active && isAuthSettingsOpen ? (
                    <SimpleConfigModal
                      inline
                      title="Authentication"
                      fields={AUTH_SETTINGS_FIELDS}
                      state={formState}
                      basePath={[]}
                      onChange={handleFieldChange}
                      onClose={() => setAuthSettingsOpen(false)}
                      onSave={() => saveSection("WebUI")}
                      onSetPassword={() => setSetPasswordOpen(true)}
                      saveDisabled={webSaveGate.saveDisabled}
                      saveBlockedReason={webSaveGate.saveBlockedReason}
                      validationErrors={webSaveGate.validationErrors}
                    />
                  ) : null}
                  {active && isSetPasswordOpen ? (
                    <SetPasswordModal
                      onClose={() => setSetPasswordOpen(false)}
                    />
                  ) : null}
                  {active && confirmDisableAuth ? (
                    <ConfigModalPortal>
                      <ConfirmDialog
                        title="Disable authentication?"
                        message="Anyone who can reach this WebUI will be able to change settings and control Torrentarr without logging in. Protect access with an authenticated reverse proxy, VPN, or firewall if you disable authentication."
                        confirmLabel="Disable authentication"
                        danger
                        onCancel={() => setConfirmDisableAuth(false)}
                        onConfirm={() => {
                          setFormState((previous) =>
                            previous
                              ? produce(previous, (draft) => {
                                  setValue(
                                    draft,
                                    ["WebUI", "AuthDisabled"],
                                    true,
                                  );
                                })
                              : previous,
                          );
                          setConfirmDisableAuth(false);
                        }}
                      />
                    </ConfigModalPortal>
                  ) : null}
                  {active && activeQbitKey && formState ? (
                    <QbitInstanceModal
                      inline
                      manageClientId={
                        originalConfig?.[activeQbitKey] &&
                        !pendingRenames.has(activeQbitKey) &&
                        ![...pendingRenames.values()].includes(activeQbitKey)
                          ? activeQbitKey
                          : undefined
                      }
                      keyName={activeQbitKey}
                      state={
                        (formState[activeQbitKey] as ConfigDocument) ?? null
                      }
                      onChange={handleFieldChange}
                      onRename={handleRenameQbitSection}
                      onClose={() => setActiveQbitKey(null)}
                      onSave={(keepProtectionOff) =>
                        saveSection(activeQbitKey, keepProtectionOff)
                      }
                      onDelete={() => requestDeleteQbitInstance(activeQbitKey)}
                      overlapWarnings={categoryOverlapWarnings}
                      saveDisabled={qbitSaveGate.saveDisabled}
                      saveBlockedReason={qbitSaveGate.saveBlockedReason}
                      validationErrors={qbitSaveGate.validationErrors}
                    />
                  ) : null}
                  {active && activeClient ? (
                    <QbitInstanceModal
                      inline
                      manageClientId={
                        (originalConfig?.TorrentClient as ConfigDocument)?.[
                          activeClient
                        ]
                          ? activeClient
                          : undefined
                      }
                      keyName={activeClient}
                      fields={getClientFields()}
                      fieldPathPrefix={`TorrentClient.${activeClient}`}
                      validationErrors={clientValidationErrors}
                      state={
                        (formState.TorrentClient as ConfigDocument)?.[
                          activeClient
                        ] as ConfigDocument
                      }
                      onChange={(path, def, value) =>
                        handleFieldChange(
                          ["TorrentClient", ...path],
                          def,
                          value,
                        )
                      }
                      onRename={() => {}}
                      overlapWarnings={categoryOverlapWarnings}
                      onClose={() => setActiveClient(null)}
                      onSave={(keepProtectionOff) =>
                        saveClient(activeClient, false, keepProtectionOff)
                      }
                      onDelete={() => {
                        setDeleteClient(activeClient);
                        setActiveClient(null);
                      }}
                      saveDisabled={clientSaving}
                    />
                  ) : null}
                </ConfigFieldSearch.Provider>
              </div>
            )}
        </div>
      </section>
      {active && integrationPickerOpen && (
        <ShellDialog
          title="Add integration"
          className="integration-dialog"
          onClose={() => setIntegrationPickerOpen(false)}
        >
          <div
            className="integration-picker"
            role="group"
            aria-label="Integration type"
          >
            {INTEGRATION_TYPES.map(({ type, icon }) => (
              <button
                key={type}
                type="button"
                className="integration-picker__option"
                onClick={() => {
                  setIntegrationPickerOpen(false);
                  chooseEditor(() => addArrInstance(type));
                }}
              >
                <IconImage src={icon} alt="" />
                <span>{arrTypeLabel(type)}</span>
              </button>
            ))}
          </div>
        </ShellDialog>
      )}
      {active && deleteClient ? (
        <ConfirmDialog
          title={`Remove ${deleteClient}?`}
          message="Remove this torrent client from the configuration?"
          danger
          onConfirm={() => void saveClient(deleteClient, true)}
          onCancel={() => setDeleteClient(null)}
        />
      ) : null}
      {active && confirmRestart && (
        <ConfirmDialog
          title="Restart required"
          message={`Configuration saved. Restart these components: ${pendingRestarts.map((item) => item.component).join(", ")}.`}
          confirmLabel="Restart now"
          cancelLabel="Later"
          onConfirm={() => void restartSavedComponents()}
          onCancel={() => setConfirmRestart(false)}
        />
      )}
      {active && pendingDelete ? (
        <ConfirmDialog
          title={`Remove ${pendingDelete.key}?`}
          message={
            pendingDelete.kind === "qbit"
              ? `This removes ${pendingDelete.key} from the config file and cannot be undone.`
              : `Delete ${pendingDelete.key}? This action cannot be undone.`
          }
          confirmLabel={deletingSection ? "Removing…" : "Remove"}
          cancelLabel="Cancel"
          danger
          onConfirm={() => {
            if (!deletingSection) {
              void persistPendingDelete();
            }
          }}
          onCancel={() => {
            if (!deletingSection) {
              setPendingDelete(null);
            }
          }}
        />
      ) : null}
    </>
  );
}
