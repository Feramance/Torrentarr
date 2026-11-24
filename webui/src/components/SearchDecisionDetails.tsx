import { useState } from "react";
import {
  getLidarrOpenArtistUrl,
  getRadarrOpenMovieUrl,
  getReadarrOpenAuthorUrl,
  getSearchDecisions,
  getSonarrOpenSeriesUrl,
} from "../api/client";
import type { SearchDecisionItem, SearchDecisionResponse } from "../api/types";
import { CopyButton } from "./CopyButton";
import { requestNavigateTab } from "../utils/navigateTab";

function SearchItem({
  item,
  category,
}: {
  item: SearchDecisionItem;
  category: string;
}) {
  const type = item.type.toLowerCase();
  const url =
    type === "movie" && item.id > 0
      ? getRadarrOpenMovieUrl(category, item.id)
      : item.seriesId
        ? getSonarrOpenSeriesUrl(category, item.seriesId)
        : item.artistId
          ? getLidarrOpenArtistUrl(category, item.artistId)
          : item.authorId
            ? getReadarrOpenAuthorUrl(category, item.authorId)
            : null;
  const label = `${item.type} ${item.id}`;
  return (
    <>
      {url ? (
        <a href={url} target="_blank" rel="noreferrer">
          {label}
        </a>
      ) : (
        label
      )}
      {item.reason
        ? ` · ${item.reason === "Retry" ? "Replacement" : item.reason}`
        : ""}
      {item.scope !== item.type ? ` · ${item.scope}` : ""}
    </>
  );
}

export function SearchDecisionDetails({
  instance,
  category,
  label = "Search activity",
}: {
  instance: string;
  category: string;
  label?: string;
}) {
  const [result, setResult] = useState<SearchDecisionResponse>();
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);
  async function load() {
    setLoading(true);
    setFailed(false);
    try {
      setResult(await getSearchDecisions(instance));
    } catch {
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }
  return (
    <details
      onToggle={(event) => {
        if (event.currentTarget.open && !loading) void load();
      }}
    >
      <summary>{label}</summary>
      {loading && <p role="status">Loading search activity…</p>}
      {failed && <p role="alert">Search activity is unavailable.</p>}
      <button
        className="btn ghost small"
        disabled={loading}
        type="button"
        onClick={() => void load()}
      >
        Refresh
      </button>
      {result && (
        <>
          <h4>Candidate preview</h4>
          {result.preview ? (
            <>
              <p className="hint">
                Last catalog sync{" "}
                {new Date(result.preview.observedAt).toLocaleString()} (
                {Math.floor(result.preview.ageSeconds / 60)} min old when
                loaded). Eligibility may change before execution.
              </p>
              {result.preview.candidateCount > 50 && (
                <p className="hint">
                  Preview shows the first 50 of {result.preview.candidateCount}{" "}
                  planned candidates.
                </p>
              )}
              {result.preview.items.length === 0 ? (
                <p>No eligible candidates in this preview.</p>
              ) : (
                <ol>
                  {result.preview.items.map((item, index) => (
                    <li key={index}>
                      <SearchItem item={item} category={category} />
                    </li>
                  ))}
                </ol>
              )}
              {result.preview.deferred.length > 0 && (
                <>
                  <h4>Deferred candidates</h4>
                  <ul>
                    {result.preview.deferred.map(({ item, reason }, index) => (
                      <li key={index}>
                        <SearchItem item={item} category={category} /> ·{" "}
                        {reason}
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </>
          ) : (
            <p>No usable worker preview is available.</p>
          )}
          <h4>Recent decisions</h4>
          {result.history.length === 0 ? (
            <p>No retained search decisions.</p>
          ) : (
            <ul>
              {result.history.map((decision) => (
                <li key={decision.cycleId}>
                  <time dateTime={decision.observedAt}>
                    {new Date(decision.observedAt).toLocaleString()}
                  </time>
                  {" · "}
                  <SearchItem item={decision.item} category={category} />
                  {" · "}
                  {decision.outcome}
                  {decision.occurrences > 1
                    ? ` (${decision.occurrences} observations)`
                    : ""}
                  {decision.completedAt && (
                    <p>
                      Completion confirmed{" "}
                      {new Date(decision.completedAt).toLocaleString()}
                    </p>
                  )}
                  {decision.noGrabAt && (
                    <p>
                      No grab observed through{" "}
                      {new Date(decision.noGrabAt).toLocaleString()}
                    </p>
                  )}
                  {decision.grabbedAt && (
                    <p>
                      Grab observed for this item{" "}
                      {new Date(decision.grabbedAt).toLocaleString()}
                    </p>
                  )}
                  <div className="actions">
                    {/^[0-9a-f]{32}$/.test(decision.cycleId) && (
                      <button
                        className="btn ghost small"
                        type="button"
                        onClick={() =>
                          requestNavigateTab({
                            tab: "logs",
                            relatedLogs: {
                              stream: "all",
                              operation: "SearchSubmission",
                              runId: decision.cycleId,
                              startedAt: decision.observedAt,
                            },
                          })
                        }
                      >
                        View related logs
                      </button>
                    )}
                    <CopyButton
                      label="Copy details"
                      text={`${decision.observedAt} · ${decision.item.type} ${decision.item.id} · ${decision.item.reason} · ${decision.outcome}\n${decision.cycleId}`}
                    />
                  </div>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </details>
  );
}
