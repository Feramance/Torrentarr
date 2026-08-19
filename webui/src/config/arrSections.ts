export const ARR_TYPES = ["radarr", "sonarr", "lidarr", "readarr"] as const;
export type KnownArrType = (typeof ARR_TYPES)[number];

export function arrTypeFromSectionName(
  sectionName: string | undefined | null,
): KnownArrType | null {
  if (!sectionName) return null;
  const lower = sectionName.trim().toLowerCase();
  for (const type of ARR_TYPES) {
    if (lower === type || lower.startsWith(`${type}-`)) return type;
  }
  return null;
}

export function isArrSection(sectionName: string | undefined | null): boolean {
  return arrTypeFromSectionName(sectionName) !== null;
}

export function supportsRequestIntegration(
  arrType: string | undefined | null,
): boolean {
  return arrType === "radarr" || arrType === "sonarr";
}

export function supportsSearchByYear(
  arrType: string | undefined | null,
): boolean {
  return arrType !== "lidarr";
}

export const READARR_ALLOWLIST = [
  ".epub",
  ".kepub",
  ".mobi",
  ".azw",
  ".azw3",
  ".pdf",
  ".cbz",
  ".cbr",
  ".flac",
  ".ape",
  ".wavpack",
  ".wav",
  ".alac",
  ".mp2",
  ".mp3",
  ".wma",
  ".m4a",
  ".m4p",
  ".m4b",
  ".aac",
  ".mp4a",
  ".ogg",
  ".oga",
  ".vorbis",
  ".!qB",
  ".parts",
];

export const LIDARR_ALLOWLIST = [
  ".mp3",
  ".flac",
  ".m4a",
  ".aac",
  ".ogg",
  ".opus",
  ".wav",
  ".ape",
  ".wma",
  ".!qB",
  ".parts",
  ".log",
  ".cue",
];

export const VIDEO_ALLOWLIST = [
  ".mp4",
  ".mkv",
  ".sub",
  ".ass",
  ".srt",
  ".!qB",
  ".parts",
];

export function defaultFileExtensionAllowlist(
  arrType: string | undefined | null,
): string[] {
  if (arrType === "lidarr") return [...LIDARR_ALLOWLIST];
  if (arrType === "readarr") return [...READARR_ALLOWLIST];
  return [...VIDEO_ALLOWLIST];
}

export const ARR_SECTION_PREFIX: Record<KnownArrType, string> = {
  radarr: "Radarr",
  sonarr: "Sonarr",
  lidarr: "Lidarr",
  readarr: "Readarr",
};
