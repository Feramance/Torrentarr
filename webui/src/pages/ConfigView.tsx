import React, {
  useCallback,
  useEffect,
  useMemo,
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
} from "../api/client";
import type { ConfigDocument } from "../api/types";
import { useToast } from "../context/ToastContext";
import {
  getCategoryCrossSectionIssues,
  getCategoryOverlapWarnings,
} from "../config/categoryConfigValidation";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { IconImage } from "../components/IconImage";
import ConfigureIcon from "../icons/gear.svg";
import AddIcon from "../icons/plus.svg";
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
}

type PendingSectionDelete = {
  kind: "qbit" | "arr";
  key: string;
};

export function ConfigView(props?: ConfigViewProps): JSX.Element {
  const { active = true, onDirtyChange } = props ?? {};
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

    groups.push({ label: "Radarr Instances", type: "radarr", items: radarr });
    groups.push({ label: "Sonarr Instances", type: "sonarr", items: sonarr });
    groups.push({ label: "Lidarr Instances", type: "lidarr", items: lidarr });
    groups.push({
      label: "Readarr Instances",
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
  const [isSettingsOpen, setSettingsOpen] = useState(false);
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
    const anyModalOpen = Boolean(
      activeArrKey ||
        activeQbitKey ||
        activeClient ||
        isSettingsOpen ||
        isWebSettingsOpen ||
        isAuthSettingsOpen ||
        isSetPasswordOpen,
    );
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
        setActiveArrKey(null);
        setActiveQbitKey(null);
        setActiveClient(null);
        setSettingsOpen(false);
        setWebSettingsOpen(false);
        setAuthSettingsOpen(false);
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
      while (formState[key]) {
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
      // Open modal for immediate configuration
      setActiveArrKey(key);
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
      } = await updateConfig({
        changes,
      });
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
  ]);

  const saveSection = useCallback(
    async (sectionKey: string): Promise<boolean> => {
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
        } = await updateConfig({
          changes,
          ...(Object.keys(renames).length ? { renames } : {}),
        });
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
    [formState, originalConfig, pendingRenames, push, applyConfigSaveResult],
  );

  const saveClient = async (name: string, remove = false): Promise<boolean> => {
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
      const result = Object.keys(changes).length
        ? await updateConfig({ changes })
        : null;
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
            : "Torrent client saved",
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
  const addClient = () => {
    let index = 1;
    const clients = (formState?.TorrentClient ?? {}) as ConfigDocument;
    while (clients[`client-${index}`] || formState?.[`client-${index}`])
      index++;
    const name = `client-${index}`;
    setFormState((previous) =>
      produce(previous ?? {}, (draft) => {
        set(draft, ["TorrentClient", name], getSchemaDefaults("TorrentClient"));
      }),
    );
    setActiveClient(name);
  };

  const applicationNotice = application && (
    <p role="status" className="card-body">
      {application.error ??
        (application.state === "applied"
          ? "Configuration applied live"
          : application.state === "pending"
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
      <section className="card">
        <div className="card-header">Config</div>
        {applicationNotice}
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
      <section className="card">
        <div className="card-header">Config</div>
        {applicationNotice}
        <div className="card-body config-layout">
          <section className="config-arr-group">
            <details className="config-arr-group__details" open>
              <summary>
                <span>Core Configuration</span>
              </summary>
              <div className="config-grid">
                <ConfigSummaryCard
                  title="Settings"
                  description="Core application configuration"
                  onConfigure={() => setSettingsOpen(true)}
                />
                <ConfigSummaryCard
                  title="Web Settings"
                  description="Host, port, and proxy"
                  onConfigure={() => setWebSettingsOpen(true)}
                />
                <ConfigSummaryCard
                  title="Authentication"
                  description="Login, local auth, and OIDC settings"
                  onConfigure={() => setAuthSettingsOpen(true)}
                />
              </div>
            </details>
          </section>
          <section className="config-arr-group">
            <details className="config-arr-group__details" open>
              <summary>
                <span>Torrent Clients</span>
                <span className="config-arr-group__count">
                  {torrentClients.length}
                </span>
                <button className="btn small" type="button" onClick={addClient}>
                  <IconImage src={AddIcon} />
                  Add Instance
                </button>
              </summary>
              <div className="config-arr-grid">
                {torrentClients.map(({ name: key, value, legacy }) => {
                  const host = getValue(value as ConfigDocument, ["Host"]);
                  const port = getValue(value as ConfigDocument, ["Port"]);
                  const disabled = getValue(value as ConfigDocument, [
                    "Disabled",
                  ]);
                  return (
                    <div
                      className="card config-card config-arr-card"
                      key={`${legacy ? "legacy" : "client"}:${key}`}
                    >
                      <div className="card-header">{key}</div>
                      <div className="card-body">
                        <dl className="config-arr-summary">
                          <div className="config-arr-summary__item">
                            <dt>Client Type</dt>
                            <dd>{String(value.Type ?? "qbittorrent")}</dd>
                          </div>
                          <div className="config-arr-summary__item">
                            <dt>Status</dt>
                            <dd>{disabled ? "Disabled" : "Enabled"}</dd>
                          </div>
                          <div className="config-arr-summary__item">
                            <dt>Host</dt>
                            <dd>
                              {host ? `${String(host)}:${port ?? 8080}` : "-"}
                            </dd>
                          </div>
                        </dl>
                        <div className="config-arr-actions">
                          <button
                            className="btn primary"
                            type="button"
                            onClick={() =>
                              legacy
                                ? setActiveQbitKey(key)
                                : setActiveClient(key)
                            }
                          >
                            <IconImage src={ConfigureIcon} />
                            Configure
                          </button>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            </details>
          </section>
          {groupedArrSections.length ? (
            <div className="config-arr-groups">
              {groupedArrSections.map((group) => (
                <section className="config-arr-group" key={group.type}>
                  <details className="config-arr-group__details" open>
                    <summary>
                      <span>{group.label}</span>
                      <span className="config-arr-group__count">
                        {group.items.length}
                      </span>
                      {(group.type === "radarr" ||
                        group.type === "sonarr" ||
                        group.type === "lidarr" ||
                        group.type === "readarr") && (
                        <button
                          className="btn small"
                          type="button"
                          onClick={() =>
                            addArrInstance(
                              group.type as
                                | "radarr"
                                | "sonarr"
                                | "lidarr"
                                | "readarr",
                            )
                          }
                        >
                          <IconImage src={AddIcon} />
                          Add Instance
                        </button>
                      )}
                    </summary>
                    <div className="config-arr-grid">
                      {group.items.length === 0 ? (
                        <div className="empty-state config-arr-empty">
                          No {group.label.replace(/ Instances$/i, "")} instances
                          configured. Use Add Instance to create one.
                        </div>
                      ) : (
                        group.items.map(([key, value]) => {
                          const uri = getValue(value as ConfigDocument, [
                            "URI",
                          ]);
                          const category = getValue(value as ConfigDocument, [
                            "Category",
                          ]);
                          const managed = getValue(value as ConfigDocument, [
                            "Managed",
                          ]);
                          return (
                            <div
                              className="card config-card config-arr-card"
                              key={key}
                            >
                              <div className="card-header">{key}</div>
                              <div className="card-body">
                                <dl className="config-arr-summary">
                                  <div className="config-arr-summary__item">
                                    <dt>Managed</dt>
                                    <dd>{managed ? "Enabled" : "Disabled"}</dd>
                                  </div>
                                  <div className="config-arr-summary__item">
                                    <dt>Category</dt>
                                    <dd>{category ? String(category) : "-"}</dd>
                                  </div>
                                  <div className="config-arr-summary__item">
                                    <dt>URI</dt>
                                    <dd className="config-arr-summary__uri">
                                      {uri ? String(uri) : "-"}
                                    </dd>
                                  </div>
                                </dl>
                                <div className="config-arr-actions">
                                  <button
                                    className="btn primary"
                                    type="button"
                                    onClick={() => setActiveArrKey(key)}
                                  >
                                    <IconImage src={ConfigureIcon} />
                                    Configure
                                  </button>
                                </div>
                              </div>
                            </div>
                          );
                        })
                      )}
                    </div>
                  </details>
                </section>
              ))}
            </div>
          ) : null}
        </div>
      </section>
      {active && activeArrKey && formState ? (
        <ArrInstanceModal
          keyName={activeArrKey}
          diskKey={resolveSectionDiskKey(activeArrKey, pendingRenames)}
          state={(formState[activeArrKey] as ConfigDocument) ?? null}
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
        <SetPasswordModal onClose={() => setSetPasswordOpen(false)} />
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
                      setValue(draft, ["WebUI", "AuthDisabled"], true);
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
          manageClientId={
            originalConfig?.[activeQbitKey] &&
            !pendingRenames.has(activeQbitKey) &&
            ![...pendingRenames.values()].includes(activeQbitKey)
              ? activeQbitKey
              : undefined
          }
          keyName={activeQbitKey}
          state={(formState[activeQbitKey] as ConfigDocument) ?? null}
          onChange={handleFieldChange}
          onRename={handleRenameQbitSection}
          onClose={() => setActiveQbitKey(null)}
          onSave={() => saveSection(activeQbitKey)}
          onDelete={() => requestDeleteQbitInstance(activeQbitKey)}
          overlapWarnings={categoryOverlapWarnings}
          saveDisabled={qbitSaveGate.saveDisabled}
          saveBlockedReason={qbitSaveGate.saveBlockedReason}
          validationErrors={qbitSaveGate.validationErrors}
        />
      ) : null}
      {active && activeClient ? (
        <QbitInstanceModal
          manageClientId={
            (originalConfig?.TorrentClient as ConfigDocument)?.[activeClient]
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
            handleFieldChange(["TorrentClient", ...path], def, value)
          }
          onRename={() => {}}
          overlapWarnings={categoryOverlapWarnings}
          onClose={() => setActiveClient(null)}
          onSave={() => saveClient(activeClient)}
          onDelete={() => {
            setDeleteClient(activeClient);
            setActiveClient(null);
          }}
          saveDisabled={clientSaving}
        />
      ) : null}
      {active && deleteClient ? (
        <ConfirmDialog
          title={`Remove ${deleteClient}?`}
          message="Remove this torrent client from the configuration?"
          danger
          onConfirm={() => void saveClient(deleteClient, true)}
          onCancel={() => setDeleteClient(null)}
        />
      ) : null}
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

interface ConfigSummaryCardProps {
  title: string;
  description: string;
  onConfigure: () => void;
}

function ConfigSummaryCard({
  title,
  description,
  onConfigure,
}: ConfigSummaryCardProps): JSX.Element {
  return (
    <div className="card config-card">
      <div className="card-header">{title}</div>
      <div className="card-body config-summary-card">
        <p>{description}</p>
        <div className="config-arr-actions">
          <button className="btn primary" type="button" onClick={onConfigure}>
            <IconImage src={ConfigureIcon} />
            Configure
          </button>
        </div>
      </div>
    </div>
  );
}
