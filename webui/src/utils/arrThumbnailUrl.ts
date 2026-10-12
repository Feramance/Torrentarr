/**
 * Thumbnail image URLs.
 *
 * Same-origin ``<img>`` requests authenticate via the session cookie. Token-authenticated
 * poster loads are fetched as blobs by <ArrPosterImage>, never placed in URLs.
 */
import { webPath } from "../api/urlBase";

export function radarrMovieThumbnailUrl(
  category: string,
  entryId: number,
): string {
  const c = encodeURIComponent(category);
  return webPath(`/web/radarr/${c}/movie/${entryId}/thumbnail`);
}

export function sonarrSeriesThumbnailUrl(
  category: string,
  entryId: number,
): string {
  const c = encodeURIComponent(category);
  return webPath(`/web/sonarr/${c}/series/${entryId}/thumbnail`);
}

export function lidarrArtistThumbnailUrl(
  category: string,
  artistId: number,
): string {
  const c = encodeURIComponent(category);
  return webPath(`/web/lidarr/${c}/artist/${artistId}/thumbnail`);
}
