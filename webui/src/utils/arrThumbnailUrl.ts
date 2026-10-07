/**
 * Thumbnail image URLs.
 *
 * Same-origin ``<img>`` requests authenticate via the session cookie; do not embed
 * ``?token=`` (that splits browser cache keys and leaks into history/proxy logs).
 * Use Bearer / ``?token=`` only for XHR API helpers when needed.
 */
import { webPath } from "../api/urlBase";

function authQuery(): string {
  try {
    const token =
      localStorage.getItem("token") ||
      localStorage.getItem("webui-token") ||
      sessionStorage.getItem("token") ||
      sessionStorage.getItem("webui-token") ||
      sessionStorage.getItem("webui_token");
    return token ? `?token=${encodeURIComponent(token)}` : "";
  } catch {
    return "";
  }
}

export function radarrMovieThumbnailUrl(
  category: string,
  entryId: number,
): string {
  const c = encodeURIComponent(category);
  return webPath(`/web/radarr/${c}/movie/${entryId}/thumbnail${authQuery()}`);
}

export function sonarrSeriesThumbnailUrl(
  category: string,
  entryId: number,
): string {
  const c = encodeURIComponent(category);
  return webPath(`/web/sonarr/${c}/series/${entryId}/thumbnail${authQuery()}`);
}

export function lidarrArtistThumbnailUrl(
  category: string,
  artistId: number,
): string {
  const c = encodeURIComponent(category);
  return webPath(`/web/lidarr/${c}/artist/${artistId}/thumbnail${authQuery()}`);
}
