import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type JSX,
} from "react";
import { LazyLog } from "@melloware/react-logviewer";
import {
  getLogDownloadUrl,
  getLogs,
  getLogTail,
  type LogFileInfo,
} from "../api/client";
import { useToast } from "../context/ToastContext";
import { useInterval } from "../hooks/useInterval";
import { IconImage } from "../components/IconImage";
import { CopyButton } from "../components/CopyButton";
import Select, {
  type CSSObjectWithLabel,
  type OptionProps,
  type StylesConfig,
} from "react-select";

import RefreshIcon from "../icons/refresh-arrow.svg";
import DownloadIcon from "../icons/download.svg";
import LiveIcon from "../icons/live-streaming.svg";

interface LogOption {
  value: string;
  label: string;
  meta?: { size: number; modified: string };
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatRelativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

const getSelectStyles = (isDark: boolean): StylesConfig<LogOption, false> => ({
  control: (base: CSSObjectWithLabel) => ({
    ...base,
    background: isDark ? "#0f131a" : "#ffffff",
    color: isDark ? "#eaeef2" : "#1d1d1f",
    borderColor: isDark ? "#2a2f36" : "#d2d2d7",
    minHeight: "38px",
    boxShadow: "none",
    "&:hover": {
      borderColor: isDark ? "#3a4149" : "#b8b8bd",
    },
  }),
  menu: (base: CSSObjectWithLabel) => ({
    ...base,
    background: isDark ? "#0f131a" : "#ffffff",
    borderColor: isDark ? "#2a2f36" : "#d2d2d7",
    border: `1px solid ${isDark ? "#2a2f36" : "#d2d2d7"}`,
  }),
  option: (base: CSSObjectWithLabel, state: OptionProps<LogOption, false>) => ({
    ...base,
    background: state.isFocused
      ? isDark
        ? "rgba(122, 162, 247, 0.15)"
        : "rgba(0, 113, 227, 0.1)"
      : isDark
        ? "#0f131a"
        : "#ffffff",
    color: isDark ? "#eaeef2" : "#1d1d1f",
    "&:active": {
      background: isDark
        ? "rgba(122, 162, 247, 0.25)"
        : "rgba(0, 113, 227, 0.2)",
    },
  }),
  singleValue: (base: CSSObjectWithLabel) => ({
    ...base,
    color: isDark ? "#eaeef2" : "#1d1d1f",
  }),
  input: (base: CSSObjectWithLabel) => ({
    ...base,
    color: isDark ? "#eaeef2" : "#1d1d1f",
  }),
  placeholder: (base: CSSObjectWithLabel) => ({
    ...base,
    color: isDark ? "#9aa3ac" : "#6e6e73",
  }),
  menuList: (base: CSSObjectWithLabel) => ({
    ...base,
    padding: "4px",
  }),
});

interface LogsViewProps {
  active: boolean;
}

export function LogsView({ active }: LogsViewProps): JSX.Element {
  const [files, setFiles] = useState<LogFileInfo[]>([]);
  const [selected, setSelected] = useState<string>("All.log");
  const [logContent, setLogContent] = useState<string>("");
  const [follow, setFollow] = useState(true);
  const [loadingList, setLoadingList] = useState(false);
  const [loadingContent, setLoadingContent] = useState(false);
  // Track whether the browser tab/window is visible to pause polling when hidden
  const [pageVisible, setPageVisible] = useState(!document.hidden);
  // Track theme for reactive Select styling
  const [isDark, setIsDark] = useState(
    () => document.documentElement.getAttribute("data-theme") === "dark",
  );

  const lastLineRef = useRef<string>("");
  const { push } = useToast();

  // Watch for theme attribute changes so Select styles update immediately on theme switch
  useEffect(() => {
    const observer = new MutationObserver(() => {
      setIsDark(document.documentElement.getAttribute("data-theme") === "dark");
    });
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["data-theme"],
    });
    return () => observer.disconnect();
  }, []);

  // Pause auto-refresh when the browser tab is hidden or the window loses focus
  useEffect(() => {
    const handler = () => setPageVisible(!document.hidden);
    document.addEventListener("visibilitychange", handler);
    return () => document.removeEventListener("visibilitychange", handler);
  }, []);

  const selectStyles = useMemo(() => getSelectStyles(isDark), [isDark]);

  const describeError = useCallback(
    (reason: unknown, context: string): string => {
      if (reason instanceof Error && reason.message)
        return `${context}: ${reason.message}`;
      if (typeof reason === "string" && reason.trim().length)
        return `${context}: ${reason}`;
      return context;
    },
    [],
  );

  const loadList = useCallback(async () => {
    setLoadingList(true);
    try {
      const data = await getLogs();
      const list = data.files ?? [];
      setFiles(list);
      if (list.length) {
        setSelected((prev) => {
          if (prev && list.some((f) => f.name === prev)) return prev;
          return list.find((f) => f.name === "All.log")?.name ?? list[0].name;
        });
      } else {
        setSelected("");
      }
    } catch (error) {
      push(describeError(error, "Failed to refresh log list"), "error");
    } finally {
      setLoadingList(false);
    }
  }, [describeError, push]);

  useEffect(() => {
    void loadList();
  }, [loadList]);

  const fetchLogContent = useCallback(
    async (showLoading: boolean = false) => {
      if (!selected) return;
      if (showLoading) setLoadingContent(true);
      try {
        // Use the shared API client (sends Authorization header, deduplicates in-flight GETs)
        const newContent = await getLogTail(selected);
        const newLines = newContent.split("\n");
        // Detect real changes by comparing the last line, not just the count.
        // This correctly handles log rotation where the new file has the same line count.
        const newLastLine = newLines[newLines.length - 1] ?? "";
        if (newLastLine !== lastLineRef.current) {
          setLogContent(newContent);
          lastLineRef.current = newLastLine;
        }
      } catch (error) {
        push(describeError(error, `Failed to read ${selected}`), "error");
      } finally {
        if (showLoading) setLoadingContent(false);
      }
    },
    [selected, push, describeError],
  );

  // Reset and reload whenever the selected file changes
  useEffect(() => {
    if (selected) {
      lastLineRef.current = "";
      void fetchLogContent(true);
    }
  }, [selected, fetchLogContent]);

  // Auto-refresh: pause when tab is inactive OR browser tab is hidden
  useInterval(
    () => {
      void fetchLogContent(false);
    },
    active && pageVisible ? 1000 : null,
  );

  const options: LogOption[] = files.map((f) => ({
    value: f.name,
    label: f.name,
    meta: { size: f.size, modified: f.modified },
  }));

  return (
    <section
      className="card"
      style={{
        height: "calc(100vh - 140px)",
        display: "flex",
        flexDirection: "column",
        margin: 0,
        padding: 0,
      }}
    >
      <div className="card-header" style={{ flexShrink: 0 }}>
        Logs
      </div>
      <div
        className="card-body"
        style={{
          flex: 1,
          display: "flex",
          flexDirection: "column",
          overflow: "hidden",
          padding: "12px",
        }}
      >
        <div className="row" style={{ flexShrink: 0, marginBottom: "12px" }}>
          <div className="col field">
            <div className="field">
              <label>Log File</label>
              <Select
                options={options}
                value={
                  selected
                    ? options.find((o) => o.value === selected) ?? null
                    : null
                }
                onChange={(option) => setSelected(option?.value ?? "")}
                isDisabled={!files.length}
                styles={selectStyles}
                formatOptionLabel={(opt) => (
                  <div
                    style={{
                      display: "flex",
                      justifyContent: "space-between",
                      alignItems: "center",
                    }}
                  >
                    <span>{opt.value}</span>
                    {opt.meta && (
                      <span
                        style={{
                          fontSize: "11px",
                          opacity: 0.55,
                          marginLeft: "12px",
                          whiteSpace: "nowrap",
                        }}
                      >
                        {formatFileSize(opt.meta.size)} ·{" "}
                        {formatRelativeTime(opt.meta.modified)}
                      </span>
                    )}
                  </div>
                )}
              />
            </div>
          </div>
          <div className="col field">
            <label>&nbsp;</label>
            <div className="row" style={{ alignItems: "center" }}>
              <button
                className="btn ghost"
                onClick={() => void loadList()}
                disabled={loadingList}
              >
                <IconImage src={RefreshIcon} />
                Reload List
              </button>
              <button
                className="btn ghost"
                onClick={() => void fetchLogContent(true)}
                disabled={!selected || loadingContent}
              >
                <IconImage src={RefreshIcon} />
                Reload
              </button>
              <button
                className="btn"
                onClick={() =>
                  selected && window.open(getLogDownloadUrl(selected), "_blank")
                }
                disabled={!selected}
              >
                <IconImage src={DownloadIcon} />
                Download
              </button>
              <CopyButton
                text={logContent}
                label="Copy Logs"
                onCopy={() => push("Logs copied to clipboard", "success")}
              />
              <label className="hint inline" style={{ cursor: "pointer" }}>
                <input
                  type="checkbox"
                  checked={follow}
                  onChange={(event) => setFollow(event.target.checked)}
                />
                <IconImage src={LiveIcon} />
                <span>Auto-scroll</span>
              </label>
            </div>
          </div>
        </div>
        <div
          style={{
            flex: 1,
            minHeight: 0,
            overflow: "hidden",
            borderRadius: "4px",
          }}
        >
          {loadingContent ? (
            <div
              style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                height: "100%",
                color: "#666",
                backgroundColor: "#0a0e14",
              }}
            >
              <span className="spinner" style={{ marginRight: "8px" }} />
              Loading logs...
            </div>
          ) : logContent ? (
            <LazyLog
              text={logContent}
              follow={follow}
              enableSearch
              caseInsensitive
              selectableLines
              extraLines={1}
              style={{
                height: "100%",
                backgroundColor: "#0a0e14",
                color: "#e5e5e5",
                fontFamily:
                  '"Cascadia Code", "Fira Code", "Consolas", "Monaco", monospace',
                fontSize: "13px",
                lineHeight: "1.5",
              }}
            />
          ) : (
            <div
              style={{
                color: "#666",
                backgroundColor: "#0a0e14",
                padding: "16px",
              }}
            >
              Select a log file to view...
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
