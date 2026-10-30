// A deployed release can replace hashed lazy-load chunks while an existing tab
// still references the old filenames. Reload at most once per minute so broken
// deployments cannot cause an infinite refresh loop.
const reloadKey = "torrentarr:stale-chunk-reload";
const retryIntervalMs = 60_000;

export function handleChunkPreloadError(
  event: Event,
  storage: Storage,
  reload: () => void,
  now: number,
): void {
  event.preventDefault();
  try {
    const previous = Number(storage.getItem(reloadKey));
    if (previous > 0 && now - previous >= 0 && now - previous < retryIntervalMs) {
      return;
    }
    storage.setItem(reloadKey, String(now));
  } catch {
    // Without storage, reloading can trap users in a refresh loop.
    return;
  }
  reload();
}
