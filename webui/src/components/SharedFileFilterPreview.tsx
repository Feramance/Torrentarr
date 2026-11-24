import { useState } from "react";
import { getFileFilterPreview, type FileFilterPreview } from "../api/client";

export function SharedFileFilterPreview({ instance }: { instance: string }) {
  const [path, setPath] = useState("");
  const [result, setResult] = useState<FileFilterPreview | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  return (
    <details>
      <summary>Preview saved file filters</summary>
      <label>
        Example torrent path
        <input
          value={path}
          maxLength={4096}
          onChange={(event) => {
            setPath(event.target.value);
            setResult(null);
          }}
          placeholder="Movie/video.mkv"
        />
      </label>
      <button
        type="button"
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError("");
          setResult(null);
          try {
            setResult(await getFileFilterPreview(instance, path));
          } catch {
            setError("File filter preview could not be loaded.");
          } finally {
            setBusy(false);
          }
        }}
      >
        {busy ? "Checking…" : "Check saved filters"}
      </button>
      {error && <p role="alert">{error}</p>}
      {result && (
        <div role="status">
          <p>
            {result.available
              ? result.reason ?? "Path is allowed by saved file filters."
              : "Processing is waiting for a usable shared list."}
          </p>
          <ul>
            {result.lists.map((list) => (
              <li key={list.name}>
                {list.name} · {list.source ?? "Source unavailable"} ·{" "}
                {list.loadedAt
                  ? `Loaded ${new Date(list.loadedAt).toLocaleString()}`
                  : "No usable version"}
                {list.checkedAt &&
                  ` · Checked ${new Date(list.checkedAt).toLocaleString()}`}
                {list.error && ` · ${list.error}`}
              </li>
            ))}
          </ul>
        </div>
      )}
    </details>
  );
}
