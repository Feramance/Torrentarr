export type ArrType = "radarr" | "sonarr" | "lidarr" | string;

export interface WorkerLoopProgress {
  phase: string;
  startedAt: string;
  wakeAt?: string | null;
  lastSuccess?: string | null;
}

export interface ProcessInfo {
  torrent?: WorkerLoopProgress | null;
  search?: WorkerLoopProgress | null;
  freeSpacePaused?: number;
  restartable?: boolean;
  category: string;
  name: string;
  kind: string;
  pid: number | null;
  alive: boolean;
  rebuilding?: boolean;
  searchSummary?: string;
  searchTimestamp?: string;
  queueCount?: number;
  categoryCount?: number;
  metricType?: string;
  status?: string;
  restartKey?: string;
}

export interface ProcessesResponse {
  processes: ProcessInfo[];
}

export interface ArrInfo {
  category: string;
  name: string;
  type: ArrType;
  alive?: boolean;
}

export interface ArrListResponse {
  arr: ArrInfo[];
  counts?: {
    radarr?: RadarrCounts;
    sonarr?: {
      available: number;
      monitored: number;
      missing?: number;
    };
    lidarr?: LidarrCounts;
  };
  ready?: boolean;
}

export interface QbitInstance {
  alive: boolean;
  host: string;
  port: number;
  version: string | null;
}

export interface TorrentDistribution {
  distribution: {
    [category: string]: {
      [instanceName: string]: number;
    };
  };
}

export interface QbitCategorySeedingConfig {
  maxRatio: number;
  maxTime: number;
  removeMode: number;
  downloadLimit: number;
  uploadLimit: number;
}

export interface QbitCategory {
  category: string;
  instance: string;
  managedBy: "qbit" | "arr";
  torrentCount: number;
  seedingCount: number;
  totalSize: number;
  avgRatio: number;
  avgSeedingTime: number;
  seedingConfig: QbitCategorySeedingConfig | null;
}

export interface QbitCategoriesResponse {
  categories: QbitCategory[];
  ready: boolean;
}

export interface LogSearchMatch {
  file: string;
  line: number;
  text: string;
  context_before?: string[];
  context_after?: string[];
}

export interface LogSearchResponse {
  query: string;
  truncated: boolean;
  matches: LogSearchMatch[];
  files_searched: string[];
}

export interface QbitTorrentOverview {
  hash: string;
  name: string;
  category: string;
  tags: string[];
  state: string;
  progress: number;
  priority: number;
  eta: number;
  availability: number;
  size: number;
  totalSize: number;
  downloaded: number;
  uploaded: number;
  amountLeft: number;
  ratio: number;
  dlspeed: number;
  upspeed: number;
  numSeeds: number;
  numLeechs: number;
  numComplete: number;
  numIncomplete: number;
  addedOn: number;
  completionOn: number;
  seedingTime: number;
  timeActive: number;
  lastActivity: number;
  ratioLimit: number;
  seedingTimeLimit: number;
  dlLimit: number;
  upLimit: number;
}

export interface QbitOverviewCategory {
  category: string;
  qbitInstance: string;
  instance?: string;
  managedBy: "qbit" | "arr";
  arrName: string | null;
  torrentCount: number;
  seedingCount: number;
  totalSize: number;
  avgRatio: number;
  avgSeedingTime: number;
  seedingConfig: QbitCategorySeedingConfig | null;
  torrents: QbitTorrentOverview[];
  /** True when the server capped the torrent list for this category. */
  torrentsTruncated?: boolean;
}

export interface QbitOverviewResponse {
  instances: string[];
  categories: QbitOverviewCategory[];
  ready: boolean;
}

export type QbitOverviewTorrent = QbitTorrentOverview;

export interface ConfigSchemaField {
  section: string;
  path: string[];
  key: string;
  kind: string;
  label: string;
  default: unknown;
  comments: string | string[];
  required: boolean;
  secure: boolean;
  uiExpose: boolean;
  applyLive: boolean | null;
  requiresRestart: boolean | null;
  options?: string[];
  description?: string;
  placeholder?: string;
  nativeUnit?: "seconds" | "minutes";
  allowNegative?: boolean;
  minimum?: number;
  maximum?: number;
  arrKinds?: string[];
  itemFields?: ConfigSchemaField[];
}

export interface ConfigSchemaResponse {
  version: number;
  sections: Record<string, ConfigSchemaField[]>;
}

export interface StatusResponse {
  qbitInstances: { [instanceName: string]: QbitInstance }; // Multi-instance info
  arrs: ArrInfo[];
  ready?: boolean;
}

export interface RestartResponse {
  status: string;
  restarted?: string[];
}

export interface LogFileInfo {
  name: string;
  size: number;
  modified: string;
}

export interface LogsListResponse {
  files: LogFileInfo[];
}

export interface RadarrCounts {
  available: number;
  monitored: number;
}

export interface RadarrMovie {
  id?: number;
  title?: string;
  year?: number;
  monitored?: boolean;
  hasFile?: boolean;
  reason?: string | null;
  qualityProfileId?: number | null;
  qualityProfileName?: string | null;
  [key: string]: unknown;
}

export interface RadarrMoviesResponse {
  category: string;
  counts: RadarrCounts;
  total: number;
  page: number;
  page_size: number;
  movies: RadarrMovie[];
}

export interface SonarrEpisode {
  id?: number;
  title?: string;
  episodeNumber?: number;
  seasonNumber?: number;
  monitored?: boolean;
  hasFile?: boolean;
  airDateUtc?: string;
  reason?: string | null;
  [key: string]: unknown;
}

export interface SonarrSeason {
  monitored: number;
  available: number;
  missing?: number;
  episodes: SonarrEpisode[];
}

export interface SonarrSeriesEntry {
  series: Record<string, unknown> & {
    qualityProfileId?: number | null;
    qualityProfileName?: string | null;
  };
  totals: {
    available: number;
    monitored: number;
    missing?: number;
  };
  seasons: Record<string, SonarrSeason>;
}

export interface SonarrSeriesResponse {
  category: string;
  total: number;
  page: number;
  page_size: number;
  counts: {
    available: number;
    monitored: number;
    missing?: number;
  };
  series: SonarrSeriesEntry[];
}

export interface LidarrCounts {
  available: number;
  monitored: number;
  missing?: number;
}

export interface LidarrTrack {
  reason?: string | null;
  id?: number;
  trackNumber?: number | string;
  title?: string;
  duration?: number;
  hasFile?: boolean;
  trackFileId?: number | null;
  monitored?: boolean;
  albumId?: number;
  albumTitle?: string;
  artistTitle?: string;
  artistId?: number;
}

export interface LidarrAlbum {
  id?: number;
  title?: string;
  artistId?: number;
  artistName?: string;
  releaseDate?: string;
  monitored?: boolean;
  hasFile?: boolean;
  reason?: string | null;
  tracks?: LidarrTrack[];
  trackCount?: number;
  trackFileCount?: number;
  percentOfTracks?: number;
  qualityProfileId?: number | null;
  qualityProfileName?: string | null;
  [key: string]: unknown;
}

export interface LidarrAlbumEntry {
  album: Record<string, unknown> & {
    qualityProfileId?: number | null;
    qualityProfileName?: string | null;
  };
  totals: {
    available: number;
    monitored: number;
    missing?: number;
  };
  tracks: LidarrTrack[];
  [key: string]: unknown;
}

export interface LidarrAlbumsResponse {
  category: string;
  counts: LidarrCounts;
  counts_tracks?: LidarrCounts;
  total: number;
  page: number;
  page_size: number;
  albums: LidarrAlbumEntry[];
}

export interface LidarrArtistEntry {
  artist: {
    id: number;
    name?: string;
    monitored?: boolean;
    qualityProfileName?: string | null;
    searched?: boolean;
    albumsMonitored?: number;
    albumsAvailable?: number;
    albumsMissing?: number;
    albumCount?: number;
    trackTotalCount?: number;
  };
}

export type LidarrArtistBrowseEntry = LidarrArtistEntry;

export interface LidarrArtistsResponse {
  category: string;
  counts: LidarrCounts;
  counts_tracks?: LidarrCounts;
  album_total?: number;
  total: number;
  page: number;
  page_size: number;
  artists: LidarrArtistEntry[];
}

export interface LidarrArtistDetailResponse {
  category: string;
  counts: LidarrCounts;
  counts_tracks?: LidarrCounts;
  artist: LidarrArtistEntry["artist"];
  albums: LidarrAlbumEntry[];
}

export interface LidarrTracksResponse {
  category: string;
  counts: {
    available: number;
    monitored: number;
    missing: number;
  };
  total: number;
  page: number;
  page_size: number;
  tracks: LidarrTrack[];
}

export interface ReadarrCounts {
  available: number;
  monitored: number;
  missing?: number;
  quality_met?: number;
  requests?: number;
}

export interface ReadarrAuthorEntry {
  author: {
    id: number;
    arrId?: number;
    name?: string;
    monitored?: boolean;
    qualityProfileName?: string | null;
    searched?: boolean;
    bookCount?: number;
    booksMonitored?: number;
    booksAvailable?: number;
    booksMissing?: number;
  };
}

export interface ReadarrAuthorsResponse {
  category: string;
  counts: ReadarrCounts;
  book_total?: number;
  total: number;
  page: number;
  page_size: number;
  authors: ReadarrAuthorEntry[];
}

export interface ReadarrBook {
  id?: number;
  title?: string;
  authorId?: number;
  authorName?: string;
  releaseDate?: string;
  monitored?: boolean;
  hasFile?: boolean;
  reason?: string | null;
  qualityProfileName?: string | null;
}

export interface ReadarrAuthorDetailResponse {
  category: string;
  counts: ReadarrCounts;
  author: ReadarrAuthorEntry["author"];
  books: Array<{ book: ReadarrBook }>;
}

export interface ConfigVersionWarning {
  type: "config_version_mismatch";
  message: string;
  currentVersion: string;
}

export interface ConfigResponseWithWarning {
  config: Record<string, unknown>;
  warning: ConfigVersionWarning;
}

// ConfigDocument is always a plain object with string keys
// The warning structure is handled internally by getConfig()
export type ConfigDocument = Record<string, unknown>;

export interface ConfigUpdatePayload {
  changes: Record<string, unknown>;
  renames?: Record<string, string>;
}

export interface UpdateState {
  in_progress: boolean;
  last_result: "success" | "error" | null;
  last_error: string | null;
  completed_at: string | null;
}

export interface MetaResponse {
  update_channel?: "latest" | "stable" | "nightly";
  auto_update_supported?: boolean;
  current_version: string;
  latest_version: string | null;
  update_available: boolean;
  changelog: string | null; // Latest version changelog
  current_version_changelog: string | null; // Current version changelog
  changelog_url: string | null;
  repository_url: string;
  homepage_url: string;
  last_checked: string | null;
  error?: string | null;
  update_state: UpdateState;
  installation_type:
    | "docker"
    | "dotnet"
    | "binary"
    | "git"
    | "source"
    | "unknown";
  binary_download_url: string | null;
  binary_download_name: string | null;
  binary_download_size: number | null;
  binary_download_error: string | null;
  /** When false, login screen is skipped. */
  auth_required?: boolean;
  /** When true, show username/password form on login page. */
  local_auth_enabled?: boolean;
  /** When true, show "Sign in with OIDC" on login page. */
  oidc_enabled?: boolean;
  /** When true, show welcome/setup screen to create username and password before accessing the app. */
  setup_required?: boolean;
  /** Public path prefix when behind a reverse proxy (e.g. /qbitrr). */
  url_base?: string;
}

export interface ConfigUpdateResponse {
  application?: ConfigurationApplicationStatus;
  warning?: string | null;
  status: string;
  configReloaded: boolean;
  reloadType:
    | "live"
    | "none"
    | "frontend"
    | "webui"
    | "single_arr"
    | "multi_arr"
    | "full";
  affectedInstances: string[];
}

export interface TorrentClientInfo {
  id: string;
  QbitInstance: string;
  type: string;
  connected: boolean;
  version: string | null;
  capabilities: Record<string, boolean>;
  managedCategories: string[];
}

export type MaintenanceOperation =
  | "ErrorRecovery"
  | "OrphanScan"
  | "HardlinkAudit"
  | "UnregisteredCleanup"
  | "CategoryReconcile"
  | "AutomaticManagement"
  | "PrivateTagging"
  | "TrackerErrorTagging"
  | "RepairPaused"
  | "RecycleRetention";

export interface MaintenanceAction {
  operation: MaintenanceOperation;
  id: string;
  kind: string;
  clientInstanceId: string;
  torrentHash?: string;
  torrentName?: string;
  path?: string;
  bytes: number;
  reason: string;
  arguments?: Record<string, string>;
  destructive: boolean;
  blockedReason?: string;
  requiredCapabilities: string[];
}

export interface MaintenanceDiagnostic {
  code: string;
  severity: "warning" | "error";
  clientInstanceId: string;
  operations: (MaintenanceOperation | number)[];
  message: string;
  remediation: string;
  affectedFiles: number;
  affectedTorrents: number;
  examples: {
    torrentHash?: string;
    torrentName?: string;
    clientPath?: string;
    localPath?: string;
  }[];
}

export interface MaintenanceOperationResult {
  clientInstanceId: string;
  operation: MaintenanceOperation | number;
  state: "ready" | "partial" | "blocked" | "disabled";
  examined: number;
  excluded: number;
  uninspectable: number;
  actionable: number;
  blockedActions: number;
  reason?: string;
}

export interface MaintenancePlan {
  isDraft?: boolean;
  planId: string;
  expiresAt: string;
  configurationFingerprint: string;
  clientInstanceIds: string[];
  operations: MaintenanceOperation[];
  actions: MaintenanceAction[];
  warnings: string[];
  criticalErrors: string[];
  diagnostics: MaintenanceDiagnostic[];
  operationResults: MaintenanceOperationResult[];
  candidateCount: number;
  blockedCount: number;
  candidateBytes: number;
}

export interface MaintenanceRunSummary {
  skippedReasons?: string[];
  blocked: number;
  pending: number;
  operationResults: MaintenanceOperationResult[];
  diagnostics: MaintenanceDiagnostic[];
  runId: string;
  startedAt: string;
  completedAt?: string;
  trigger: string;
  clientInstanceIds: string[];
  planned: number;
  applied: number;
  skipped: number;
  failed: number;
  bytesMoved: number;
  bytesReclaimed?: number;
  result: string;
  errors: string[];
}

export interface MaintenanceStatus {
  configurationFingerprint?: string;
  cleanup?: {
    clientInstanceId: string;
    automatic: boolean;
    nextRun?: string;
    notices: string[];
  }[];
  inspections?: {
    clientInstanceId: string;
    lastSucceededAt?: string;
    nextAttempt?: string;
    consecutiveFailures: number;
  }[];
  recovery?: {
    clientInstanceId: string;
    torrentHash: string;
    torrentName: string;
    operation: MaintenanceOperation;
    stage: string;
    reason: string;
    attempts: number;
    failureSince?: string;
  }[];
  operations?: {
    clientInstanceId: string;
    operation: MaintenanceOperation;
    enabled: boolean;
    executionMode: string;
    schedule: string;
    nextRun?: string;
    authorized: boolean;
  }[];
  protection?: {
    clientInstanceId: string;
    torrentHash: string;
    torrentName: string;
    tracker: string;
    state: string;
    reason: string;
    observedSeedSeconds: number;
    remainingSeedSeconds: number;
    deadline?: string;
    latestRecoveryStart?: string;
    latestCompletion?: string;
    estimatedCompletion?: string;
    completionRisk?: string;
    deadlineRisk: string;
    nativeStopLimitRisk?: boolean;
    localEstimate: boolean;
  }[];
  running: boolean;
  currentRun?: MaintenanceRunSummary;
  lastRun?: MaintenanceRunSummary;
  armed: Record<string, boolean>;
  armedOperations: Record<string, MaintenanceOperation[]>;
  nextScheduledRun: Record<string, string | null>;
}

export interface LogTailPayload {
  start_bytes?: number;
  start_inode?: number;
  start_line?: number;
  content: string;
  next_bytes: number;
  size: number;
  inode: number;
  rotated: boolean;
  truncated: boolean;
}

export type ReadarrBookEntry = {
  book: ReadarrBook;
  totals?: { available: number; monitored: number; missing?: number };
};

export interface ConfigurationApplicationStatus {
  acceptedRevision: number;
  state: "applied" | "pending" | "error";
  components: Record<
    string,
    { appliedRevision: number; error?: string | null }
  >;
  error?: string | null;
}

export interface LiveConfigurationEvent {
  application: ConfigurationApplicationStatus;
  webui: {
    host: string;
    port: number;
    urlBase: string;
    behindHttpsProxy: boolean;
    theme: string;
    viewDensity: string;
    liveArr: boolean;
  };
}

export interface TorrentAutomationStatus {
  trackerIssues?: {
    clientInstanceId: string;
    torrentHash: string;
    torrentName: string;
    reason: string;
  }[];
  protection: NonNullable<MaintenanceStatus["protection"]>;
  recovery: NonNullable<MaintenanceStatus["recovery"]>;
  inspections: NonNullable<MaintenanceStatus["inspections"]>;
}

export interface OperationalIssue {
  id: number;
  code: string;
  category: "automation" | "system";
  arrInstance: string;
  clientInstance: string;
  subjectType: string;
  subjectId: string;
  severity: "warning" | "error";
  title: string;
  impact: string;
  remediation: string;
  target: string;
  firstObserved: string;
  lastObserved: string;
  resolvedAt: string | null;
  resolution?: string | null;
  occurrences: number;
}
export interface OperationalIssueGroup extends OperationalIssue {
  affected: number;
  examples: OperationalIssue[];
}
export interface PendingOperationalIssue {
  report: Omit<
    OperationalIssue,
    "id" | "firstObserved" | "lastObserved" | "resolvedAt" | "occurrences"
  >;
  resolved: boolean;
  observedAt: string;
}
export interface IssuesResponse {
  available: boolean;
  total: number | null;
  items: OperationalIssueGroup[];
  page: number;
  pageSize: number;
  pending: PendingOperationalIssue[];
}
export interface IssuesSummary {
  instances?: string[];
  available: boolean;
  activeGroups: number | null;
  counts: {
    category: string;
    severity: string;
    arrInstance: string;
    clientInstance: string;
    code: string;
    affected: number;
  }[];
  pending: PendingOperationalIssue[];
}
