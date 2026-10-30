import { clearUrlBaseCache, setUrlBaseFromMeta, webPath } from "./urlBase";
import type {
  ArrListResponse,
  ConfigDocument,
  ConfigResponseWithWarning,
  ConfigUpdatePayload,
  ConfigUpdateResponse,
  LidarrTracksResponse,
  MetaResponse,
  LogsListResponse,
  LogFileInfo,
  ProcessesResponse,
  QbitCategoriesResponse,
  RadarrMoviesResponse,
  RestartResponse,
  SonarrSeriesResponse,
  LidarrAlbumsResponse,
  LidarrArtistsResponse,
  LidarrArtistDetailResponse,
  ReadarrAuthorsResponse,
  ReadarrAuthorDetailResponse,
  StatusResponse,
  TorrentDistribution,
  LogSearchResponse,
  LogTailPayload,
  QbitOverviewResponse,
  ConfigSchemaResponse,
  TorrentClientInfo,
  MaintenanceOperation,
  MaintenancePlan,
  MaintenanceRunSummary,
  MaintenanceStatus,
} from "./types";

export type { LogFileInfo };
export class AuthError extends Error {
  code?: string;
  constructor(message: string, code?: string) {
    super(message);
    this.name = "AuthError";
    this.code = code;
  }
}

/** API field validation error from config save (path is dotted, e.g. Lidarr.URI). */
export interface ConfigApiValidationError {
  path: string;
  message: string;
}

/** Thrown when config update fails validation; preserves per-field errors for UI. */
export class ConfigApiError extends Error {
  validationErrors: ConfigApiValidationError[];

  constructor(
    message: string,
    validationErrors: ConfigApiValidationError[] = [],
  ) {
    super(message);
    this.name = "ConfigApiError";
    this.validationErrors = validationErrors;
  }
}

function parseConfigApiValidationErrors(
  detail: unknown,
): ConfigApiValidationError[] {
  if (!detail || typeof detail !== "object") {
    return [];
  }
  const raw = (detail as Record<string, unknown>).validationErrors;
  if (!Array.isArray(raw)) {
    return [];
  }
  const errors: ConfigApiValidationError[] = [];
  for (const item of raw) {
    if (!item || typeof item !== "object") {
      continue;
    }
    const path = (item as Record<string, unknown>).path;
    const message = (item as Record<string, unknown>).message;
    if (
      typeof path === "string" &&
      path.trim() &&
      typeof message === "string" &&
      message.trim()
    ) {
      errors.push({ path: path.trim(), message: message.trim() });
    }
  }
  return errors;
}

function formatConfigApiValidationErrors(
  errors: ConfigApiValidationError[],
): string {
  const formatted = errors
    .map((error) => `${error.path}: ${error.message}`)
    .join("\n");
  return errors.length === 1
    ? formatted
    : `Please resolve the following issues:\n${formatted}`;
}

const JSON_HEADERS = { "Content-Type": "application/json" } as const;
const TOKEN_STORAGE_KEYS = ["token", "webui-token", "webui_token"] as const;
const MAX_AUTH_RETRIES = 1;
let transientToken: string | null = null;

// Request deduplication cache
const inflightRequests = new Map<string, Promise<unknown>>();

/** Short-lived GET response cache (status/config/meta/processes). Arr catalogs use TTL 0. */
interface TtlCacheEntry {
  expiresAt: number;
  value: unknown;
}

const ttlResponseCache = new Map<string, TtlCacheEntry>();

const GET_TTL_MS: ReadonlyArray<{
  match: (path: string) => boolean;
  ttlMs: number;
}> = [
  { match: (path) => path.includes("/web/status"), ttlMs: 2_000 },
  { match: (path) => path.includes("/web/config"), ttlMs: 30_000 },
  {
    match: (path) =>
      path.includes("/web/meta") && !/[?&]force=1(?:&|$)/.test(path),
    ttlMs: 60_000,
  },
  { match: (path) => path.includes("/web/processes"), ttlMs: 800 },
];

function extractRequestPath(input: RequestInfo | URL): string {
  const raw = input instanceof Request ? input.url : String(input);
  try {
    if (raw.startsWith("http://") || raw.startsWith("https://")) {
      const url = new URL(raw);
      return `${url.pathname}${url.search}`;
    }
  } catch {
    // fall through
  }
  return raw;
}

function resolveGetTtlMs(path: string): number {
  // Arr catalog paged URLs keep their own caches — never TTL here.
  if (
    /\/web\/(?:radarr|sonarr|lidarr|readarr)\//.test(path) ||
    /\/web\/arr\//.test(path)
  ) {
    return 0;
  }
  for (const rule of GET_TTL_MS) {
    if (rule.match(path)) {
      return rule.ttlMs;
    }
  }
  return 0;
}

function readTtlCache<T>(key: string): T | undefined {
  const entry = ttlResponseCache.get(key);
  if (!entry) {
    return undefined;
  }
  if (Date.now() > entry.expiresAt) {
    ttlResponseCache.delete(key);
    return undefined;
  }
  return entry.value as T;
}

function writeTtlCache(key: string, value: unknown, ttlMs: number): void {
  if (ttlMs <= 0) {
    return;
  }
  ttlResponseCache.set(key, { value, expiresAt: Date.now() + ttlMs });
}

/** Invalidate TTL entries whose request path matches any of the substrings. */
export function invalidateGetCache(pathSubstrings: readonly string[]): void {
  for (const key of inflightRequests.keys()) {
    if (pathSubstrings.some((part) => key.includes(part)))
      inflightRequests.delete(key);
  }
  for (const key of [...ttlResponseCache.keys()]) {
    if (pathSubstrings.some((part) => key.includes(part))) {
      ttlResponseCache.delete(key);
    }
  }
}

function createRequestKey(
  input: RequestInfo | URL,
  init?: RequestInit,
): string {
  const url = input instanceof Request ? input.url : String(input);
  const method = init?.method || "GET";
  const body = init?.body ? String(init.body) : "";
  return `${method}:${url}:${body}`;
}

function resolveToken(): string | null {
  for (const key of TOKEN_STORAGE_KEYS) {
    for (const name of ["localStorage", "sessionStorage"] as const) {
      try {
        const value = window[name].getItem(key);
        if (value) return value;
      } catch {
        /* Storage may be unavailable; cookie authentication still works. */
      }
    }
  }
  try {
    const url = new URL(window.location.href);
    const token = url.searchParams.get("token");
    if (token) {
      transientToken = token;
      try {
        localStorage.setItem("token", token);
      } catch {
        /* Keep the token in memory for this session. */
      }
      url.searchParams.delete("token");
      window.history.replaceState({}, "", url.toString());
      return token;
    }
  } catch {
    /* Incomplete locations are also used by tests. */
  }
  return transientToken;
}

function clearStoredToken(): void {
  transientToken = null;
  for (const name of ["localStorage", "sessionStorage"] as const) {
    try {
      for (const key of TOKEN_STORAGE_KEYS) window[name].removeItem(key);
    } catch {
      /* Storage unavailable. */
    }
  }
}

function resolveRequestInput(input: RequestInfo | URL): RequestInfo | URL {
  if (typeof input === "string" && input.startsWith("/")) {
    return webPath(input);
  }
  return input;
}

function buildInit(
  init: RequestInit | undefined,
  token: string | null,
): RequestInit {
  const headers = new Headers(init?.headers || {});
  Object.entries(JSON_HEADERS).forEach(([key, value]) => {
    if (!headers.has(key)) headers.set(key, value);
  });
  if (token && !headers.has("Authorization")) {
    headers.set("Authorization", `Bearer ${token}`);
  }
  return {
    ...init,
    headers,
    credentials: "include",
  };
}

async function fetchWithAuthRetry<T>(
  input: RequestInfo | URL,
  init: RequestInit | undefined,
  handler: (response: Response) => Promise<T>,
  retries = MAX_AUTH_RETRIES,
): Promise<T> {
  const token = resolveToken();
  const response = await fetch(
    resolveRequestInput(input),
    buildInit(init, token),
  );
  if (response.status === 401 && retries > 0 && token) {
    clearStoredToken();
    return fetchWithAuthRetry(input, init, handler, retries - 1);
  }
  return handler(response);
}

async function fetchJson<T>(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<T> {
  // Only deduplicate GET requests (safe to share)
  const method = init?.method || "GET";
  if (method === "GET") {
    const key = createRequestKey(input, init);
    const path = extractRequestPath(input);
    const ttlMs = resolveGetTtlMs(path);

    if (ttlMs > 0) {
      const cached = readTtlCache<T>(key);
      if (cached !== undefined) {
        return cached;
      }
    }

    const existingRequest = inflightRequests.get(key) as Promise<T> | undefined;
    if (existingRequest) {
      return existingRequest;
    }

    const promise = fetchWithAuthRetry<T>(input, init, (response) =>
      handleJson<T>(response),
    )
      .then((value) => {
        if (inflightRequests.get(key) === promise)
          writeTtlCache(key, value, ttlMs);
        return value;
      })
      .finally(() => {
        if (inflightRequests.get(key) === promise) inflightRequests.delete(key);
      });

    inflightRequests.set(key, promise);
    return promise;
  }

  return fetchWithAuthRetry<T>(input, init, (response) =>
    handleJson<T>(response),
  );
}

async function fetchTextResponse(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<string> {
  return fetchWithAuthRetry<string>(input, init, (response) =>
    handleText(response),
  );
}

async function handleJson<T>(res: Response): Promise<T> {
  if (!res.ok) {
    let detail: unknown = null;
    try {
      detail = await res.json();
    } catch {
      // ignore
    }
    let message = `${res.status} ${res.statusText}`;
    if (
      detail &&
      typeof detail === "object" &&
      "error" in detail &&
      typeof (detail as Record<string, unknown>).error === "string"
    ) {
      const errorText = (detail as Record<string, unknown>).error as string;
      if (errorText.trim()) {
        message = errorText;
      }
    }
    const validationErrors = parseConfigApiValidationErrors(detail);
    if (validationErrors.length) {
      throw new ConfigApiError(
        formatConfigApiValidationErrors(validationErrors),
        validationErrors,
      );
    }
    throw new Error(message);
  }
  return (await res.json()) as T;
}

async function handleText(res: Response): Promise<string> {
  if (!res.ok) {
    throw new Error(`${res.status} ${res.statusText}`);
  }
  return res.text();
}

export async function getMeta(params?: {
  force?: boolean;
}): Promise<MetaResponse> {
  const query = params?.force ? "?force=1" : "";
  const meta = await fetchJson<MetaResponse>(`/web/meta${query}`);
  setUrlBaseFromMeta(meta.url_base);
  return meta;
}

export async function getStatus(): Promise<StatusResponse> {
  return fetchJson<StatusResponse>("/web/status");
}

export async function getQbitCategories(): Promise<QbitCategoriesResponse> {
  return fetchJson<QbitCategoriesResponse>("/web/qbit/categories");
}

export async function getQbitOverview(
  instance?: string,
): Promise<QbitOverviewResponse> {
  const params = new URLSearchParams();
  if (instance) params.set("instance", instance);
  const query = params.toString();
  const data = await fetchJson<QbitOverviewResponse>(
    `/web/qbit/overview${query ? `?${query}` : ""}`,
  );
  return {
    ...data,
    categories: data.categories.map((cat) => ({
      ...cat,
      qbitInstance: cat.qbitInstance ?? cat.instance ?? "",
      torrents: cat.torrents.map((t) => ({
        ...t,
        tags: Array.isArray(t.tags)
          ? t.tags
          : String(t.tags ?? "")
              .split(",")
              .map((tag) => tag.trim())
              .filter(Boolean),
      })),
    })),
  };
}

export async function getConfigSchema(): Promise<ConfigSchemaResponse> {
  return fetchJson<ConfigSchemaResponse>("/web/config/schema");
}

export async function getProcesses(): Promise<ProcessesResponse> {
  return fetchJson<ProcessesResponse>("/web/processes");
}

export async function restartProcess(
  category: string,
  kind: string,
): Promise<RestartResponse> {
  const url = `/web/processes/${encodeURIComponent(
    category,
  )}/${encodeURIComponent(kind)}/restart`;
  return fetchJson<RestartResponse>(url, { method: "POST" });
}

export async function restartAllProcesses(): Promise<RestartResponse> {
  return fetchJson<RestartResponse>("/web/processes/restart_all", {
    method: "POST",
  });
}

export async function rebuildArrs(): Promise<RestartResponse> {
  return fetchJson<RestartResponse>("/web/arr/rebuild", { method: "POST" });
}

export async function setLogLevel(level: string): Promise<void> {
  await fetchJson<void>("/web/loglevel", {
    method: "POST",
    body: JSON.stringify({ level }),
  });
}

export async function getLogs(): Promise<LogsListResponse> {
  return fetchJson<LogsListResponse>("/web/logs");
}

export async function getLogTail(name: string): Promise<string> {
  return fetchTextResponse(`/web/logs/${encodeURIComponent(name)}`);
}

export function getLogDownloadUrl(name: string): string {
  return webPath(`/web/logs/${encodeURIComponent(name)}/download`);
}

export function getLogStreamUrl(
  name: string,
  sinceBytes?: number,
  inode?: number,
  lines?: number,
): string {
  if (sinceBytes == null)
    return webPath(`/web/logs/${encodeURIComponent(name)}/stream`);
  const params = new URLSearchParams({
    since_bytes: String(sinceBytes),
    format: "json",
  });
  if (inode != null && inode > 0) {
    params.set("inode", String(inode));
  }
  if (lines != null && lines > 0) {
    params.set("lines", String(lines));
  }
  return webPath(
    `/web/logs/${encodeURIComponent(name)}/stream?${params.toString()}`,
  );
}

export async function searchLogs(
  name: string,
  query: string,
  options?: {
    caseSensitive?: boolean;
    regex?: boolean;
    maxMatches?: number;
    context?: number;
    includeRotated?: boolean;
  },
): Promise<LogSearchResponse> {
  const params = new URLSearchParams();
  params.set("q", query);
  if (options?.caseSensitive) params.set("case", "1");
  if (options?.regex) params.set("regex", "1");
  if (options?.maxMatches != null)
    params.set("max_matches", String(options.maxMatches));
  if (options?.context != null) params.set("context", String(options.context));
  if (options?.includeRotated === false) params.set("include_rotated", "0");
  return fetchJson<LogSearchResponse>(
    `/web/logs/${encodeURIComponent(name)}/search?${params}`,
  );
}

export async function getArrList(): Promise<ArrListResponse> {
  return fetchJson<ArrListResponse>("/web/arr");
}

export async function getRadarrMovies(
  category: string,
  page: number,
  pageSize: number,
  q: string,
  options?: { missingOnly?: boolean; reasonFilter?: string | null },
): Promise<RadarrMoviesResponse> {
  const params = new URLSearchParams();
  params.set("page", String(page));
  params.set("page_size", String(pageSize));
  if (q) params.set("q", q);
  if (options?.missingOnly) params.set("has_file", "false");
  if (options?.reasonFilter) params.set("reason", options.reasonFilter);
  return fetchJson<RadarrMoviesResponse>(
    `/web/radarr/${encodeURIComponent(category)}/movies?${params}`,
  );
}

export async function getSonarrSeries(
  category: string,
  page: number,
  pageSize: number,
  q: string,
  options?: { missingOnly?: boolean; reasonFilter?: string | null },
): Promise<SonarrSeriesResponse> {
  const params = new URLSearchParams();
  params.set("page", String(page));
  params.set("page_size", String(pageSize));
  if (q) params.set("q", q);
  if (options?.missingOnly) {
    params.set("missing", "true");
  }
  if (options?.reasonFilter) params.set("reason", options.reasonFilter);
  return fetchJson<SonarrSeriesResponse>(
    `/web/sonarr/${encodeURIComponent(category)}/series?${params}`,
  );
}

export async function getLidarrArtists(
  category: string,
  page: number,
  pageSize: number,
  query?: string,
  options?: { missingOnly?: boolean; reasonFilter?: string | null },
): Promise<LidarrArtistsResponse> {
  const params = new URLSearchParams();
  params.set("page", page.toString());
  params.set("page_size", pageSize.toString());
  if (query) {
    params.set("q", query);
  }
  if (options?.missingOnly) params.set("missing", "true");
  if (options?.reasonFilter) params.set("reason", options.reasonFilter);
  return fetchJson<LidarrArtistsResponse>(
    `/web/lidarr/${encodeURIComponent(category)}/artists?${params}`,
  );
}

export function getRadarrOpenMovieUrl(
  category: string,
  id: number,
): string | null {
  return webPath(`/web/arr/${encodeURIComponent(category)}/open/movie/${id}`);
}

export function getSonarrOpenSeriesUrl(
  category: string,
  id: number,
): string | null {
  return webPath(`/web/arr/${encodeURIComponent(category)}/open/series/${id}`);
}

export function getLidarrOpenArtistUrl(
  category: string,
  id: number,
): string | null {
  return webPath(`/web/arr/${encodeURIComponent(category)}/open/artist/${id}`);
}

export async function getLidarrArtistDetail(
  category: string,
  artistId: number,
): Promise<LidarrArtistDetailResponse> {
  return fetchJson<LidarrArtistDetailResponse>(
    `/web/lidarr/${encodeURIComponent(category)}/artist/${artistId}`,
  );
}

export async function getReadarrAuthors(
  category: string,
  page: number,
  pageSize: number,
  query?: string,
  filters?: { missing?: boolean; reason?: string; monitored?: boolean },
): Promise<ReadarrAuthorsResponse> {
  const params = new URLSearchParams();
  params.set("page", page.toString());
  params.set("page_size", pageSize.toString());
  if (query) {
    params.set("q", query);
  }
  if (filters?.missing) params.set("missing", "true");
  if (filters?.reason) params.set("reason", filters.reason);
  if (filters?.monitored != null)
    params.set("monitored", String(filters.monitored));
  return fetchJson<ReadarrAuthorsResponse>(
    `/web/readarr/${encodeURIComponent(category)}/authors?${params}`,
  );
}

export async function getReadarrAuthorDetail(
  category: string,
  authorId: number,
  signal?: AbortSignal,
): Promise<ReadarrAuthorDetailResponse> {
  return fetchJson<ReadarrAuthorDetailResponse>(
    `/web/readarr/${encodeURIComponent(category)}/author/${authorId}`,
    signal ? { signal } : undefined,
  );
}

export async function getLidarrAlbums(
  category: string,
  page: number,
  pageSize: number,
  query?: string,
): Promise<LidarrAlbumsResponse> {
  const params = new URLSearchParams();
  params.set("page", page.toString());
  params.set("page_size", pageSize.toString());
  if (query) {
    params.set("q", query);
  }
  // Always include tracks
  params.set("include_tracks", "true");
  return fetchJson<LidarrAlbumsResponse>(
    `/web/lidarr/${encodeURIComponent(category)}/albums?${params}`,
  );
}

export async function restartArr(category: string): Promise<void> {
  await fetchJson<void>(`/web/arr/${encodeURIComponent(category)}/restart`, {
    method: "POST",
  });
}

export async function getConfig(): Promise<ConfigDocument> {
  // Response might be ConfigDocument OR ConfigResponseWithWarning
  const response = await fetchJson<ConfigDocument | ConfigResponseWithWarning>(
    "/web/config",
  );

  // Check if response contains a warning structure
  if (
    response &&
    typeof response === "object" &&
    "warning" in response &&
    "config" in response
  ) {
    // Response has warning structure - store warning for display
    const warningResponse = response as ConfigResponseWithWarning;
    if (warningResponse.warning?.message) {
      try {
        sessionStorage.setItem(
          "config_version_warning",
          warningResponse.warning.message,
        );
      } catch {
        /* The configuration is still usable without a persisted warning. */
      }
    }
    // Return the actual config (always present in warning structure)
    return warningResponse.config;
  }

  // Normal response - just a plain config object
  return response as ConfigDocument;
}

export async function updateConfig(
  payload: ConfigUpdatePayload,
): Promise<ConfigUpdateResponse> {
  const result = await fetchJson<ConfigUpdateResponse>("/web/config", {
    method: "POST",
    body: JSON.stringify(payload),
  });
  if (result.application)
    window.dispatchEvent(
      new CustomEvent("torrentarr:configuration", {
        detail: result.application,
      }),
    );
  invalidateGetCache(["/web/config", "/web/meta", "/web/status"]);
  window.dispatchEvent(new Event("torrentarr:config-saved"));
  return result;
}

export async function triggerUpdate(): Promise<void> {
  const result = await fetchJson<{ success: boolean; message: string }>(
    "/web/update",
    { method: "POST" },
  );
  if (!result.success) throw new Error(result.message);
}

export interface TestConnectionRequest {
  arrType: "radarr" | "sonarr" | "lidarr" | "readarr";
  /** Resolve the stored secret; explicit URI and TLS settings still take precedence. */
  instanceKey?: string;
  uri?: string;
  apiKey?: string;
  skipTlsVerify?: boolean;
}

export interface TestConnectionResponse {
  success: boolean;
  message: string;
  systemInfo?: {
    version: string;
    branch?: string;
  };
  qualityProfiles?: Array<{ id: number; name: string }>;
}

export async function testArrConnection(
  request: TestConnectionRequest,
): Promise<TestConnectionResponse> {
  return fetchJson("/web/arr/test-connection", {
    method: "POST",
    body: JSON.stringify(request),
  });
}

export async function getToken(): Promise<{ token: string }> {
  return fetchJson<{ token: string }>("/web/token");
}

export interface SetPasswordRequest {
  username: string;
  password: string;
  setupToken?: string;
}

export async function setPassword(
  request: SetPasswordRequest,
): Promise<{ success: boolean }> {
  const token = resolveToken();
  const response = await fetch(
    webPath("/web/auth/set-password"),
    buildInit(
      {
        method: "POST",
        body: JSON.stringify(request),
      },
      token,
    ),
  );
  if (!response.ok) {
    const detail = (await response.json()) as { error?: string };
    throw new Error(detail?.error ?? response.statusText);
  }
  return (await response.json()) as { success: boolean };
}

export interface LoginRequest {
  username: string;
  password: string;
}

/** Error thrown by `login()`. Preserves the `code` field from the server response (e.g. "SETUP_REQUIRED"). */
export async function login(
  request: LoginRequest,
): Promise<{ success: boolean }> {
  const response = await fetch(webPath("/web/login"), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "include",
    body: JSON.stringify(request),
  });
  if (!response.ok) {
    const detail = (await response.json()) as { error?: string; code?: string };
    throw new AuthError(detail?.error ?? response.statusText, detail?.code);
  }
  return (await response.json()) as { success: boolean };
}

/** Navigate to /web/logout to sign out the session cookie; server redirects to /login. Clears stored API token. */
export function logout(): void {
  clearStoredToken();
  window.location.href = webPath("/web/logout");
}

export async function getTorrentsDistribution(): Promise<TorrentDistribution> {
  return fetchJson<TorrentDistribution>("/web/torrents/distribution");
}

export async function getLidarrTracks(
  category: string,
  page = 0,
  pageSize = 50,
): Promise<LidarrTracksResponse> {
  const params = new URLSearchParams();
  params.set("page", String(page));
  params.set("page_size", String(pageSize));
  return fetchJson<LidarrTracksResponse>(
    `/web/lidarr/${encodeURIComponent(category)}/tracks?${params}`,
  );
}

export const getTorrentClients = (): Promise<TorrentClientInfo[]> =>
  fetchJson<TorrentClientInfo[]>("/web/torrent-clients");

export const getMaintenanceStatus = (): Promise<MaintenanceStatus> =>
  fetchJson<MaintenanceStatus>("/web/maintenance/status");

export const getMaintenanceHistory = (): Promise<MaintenanceRunSummary[]> =>
  fetchJson<MaintenanceRunSummary[]>("/web/maintenance/history");

export const previewMaintenance = (
  clientInstanceIds: string[],
  operations: MaintenanceOperation[],
  maintenanceOverrides?: Record<string, unknown>,
): Promise<MaintenancePlan> =>
  fetchJson<MaintenancePlan>("/web/maintenance/preview", {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify({
      clientInstanceIds,
      operations,
      maintenanceOverrides,
    }),
  });

export const applyMaintenance = (
  planId: string,
  authorizeOnce = false,
): Promise<MaintenanceRunSummary> =>
  fetchJson<MaintenanceRunSummary>("/web/maintenance/apply", {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify({ planId, authorizeOnce }),
  });

export const maintenanceCommand = (
  command: "run" | "cancel",
  clientInstanceIds: string[] = [],
  operations: MaintenanceOperation[] = [],
): Promise<MaintenanceStatus | MaintenanceRunSummary> =>
  fetchJson(`/web/maintenance/${command}`, {
    method: "POST",
    headers: JSON_HEADERS,
    body: JSON.stringify({ clientInstanceIds, operations }),
  });

export function getReadarrOpenAuthorUrl(
  category: string,
  authorId: number,
): string {
  return webPath(
    `/web/arr/${encodeURIComponent(category)}/open/author/${authorId}`,
  );
}

export interface GetLogTailJsonOptions {
  beforeBytes?: number;
  lines?: number;
  offset?: number;
  sinceBytes?: number;
  inode?: number;
  aroundLine?: number;
}

/** Fetch log content as JSON (initial tail, older window, or byte-offset delta). */
export async function getLogTailJson(
  name: string,
  options: GetLogTailJsonOptions = {},
): Promise<LogTailPayload> {
  const base = `/web/logs/${encodeURIComponent(name)}`;
  const params = new URLSearchParams({ format: "json" });
  if (options.lines != null && options.lines > 0) {
    params.set("lines", String(options.lines));
  }
  if (options.offset != null && options.offset > 0) {
    params.set("offset", String(options.offset));
  }
  if (options.beforeBytes != null)
    params.set("before_bytes", String(options.beforeBytes));
  if (options.sinceBytes != null) {
    params.set("since_bytes", String(options.sinceBytes));
  }
  if (options.inode != null && options.inode > 0) {
    params.set("inode", String(options.inode));
  }
  if (options.aroundLine != null && options.aroundLine > 0) {
    params.set("around_line", String(options.aroundLine));
  }
  return fetchJson<LogTailPayload>(`${base}?${params.toString()}`);
}

/** Alias for delta polling. */
export async function getLogDelta(
  name: string,
  sinceBytes: number,
  inode?: number,
  lines?: number,
): Promise<LogTailPayload> {
  return getLogTailJson(name, { sinceBytes, inode, lines });
}

export async function fetchWebToken(): Promise<string | null> {
  try {
    return (await getToken()).token || null;
  } catch {
    return null;
  }
}
export async function refreshUrlBaseFromMeta(): Promise<MetaResponse> {
  clearUrlBaseCache();
  invalidateGetCache(["/web/meta"]);
  return getMeta({ force: true });
}
export function getQbitOverviewPath(instance?: string): string {
  return `/web/qbit/overview${instance ? `?instance=${encodeURIComponent(instance)}` : ""}`;
}

/** Uses the normal authentication path, including API tokens, for the live stream. */
export async function watchConfiguration(
  receive: (event: import("./types").LiveConfigurationEvent) => void,
  signal: AbortSignal,
): Promise<void> {
  await fetchWithAuthRetry<void>(
    "/web/config/events",
    { signal },
    async (response) => {
      if (
        !response.ok ||
        !response.body ||
        !response.headers.get("content-type")?.includes("text/event-stream")
      )
        throw new Error("Configuration stream unavailable");
      const reader = response.body
        .pipeThrough(new TextDecoderStream())
        .getReader();
      let pending = "";
      try {
        while (!signal.aborted) {
          const { value, done } = await reader.read();
          if (done) break;
          pending += value;
          let end: number;
          while ((end = pending.indexOf("\n\n")) >= 0) {
            const event = pending.slice(0, end);
            pending = pending.slice(end + 2);
            if (event.startsWith("data: ")) receive(JSON.parse(event.slice(6)));
          }
        }
      } finally {
        reader.releaseLock();
      }
    },
  );
}

export const getTorrentAutomationStatus = (): Promise<
  import("./types").TorrentAutomationStatus
> => fetchJson("/web/torrent-automation/status");

export async function getIssues(
  filters: Record<string, string | number> = {},
): Promise<import("./types").IssuesResponse> {
  const params = new URLSearchParams(
    Object.entries(filters).map(([key, value]) => [key, String(value)]),
  );
  return fetchJson(`/web/issues?${params}`);
}
export async function getIssuesSummary(): Promise<
  import("./types").IssuesSummary
> {
  return fetchJson("/web/issues/summary");
}
export async function getIssueDetails(
  id: number,
  page = 0,
): Promise<{
  available: boolean;
  total: number;
  items: import("./types").OperationalIssue[];
  page: number;
  pageSize: number;
}> {
  return fetchJson(`/web/issues/${id}?page=${page}`);
}
