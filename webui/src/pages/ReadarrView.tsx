import {
  Fragment,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type JSX,
} from "react";
import {
  getArrList,
  getReadarrAuthorDetail,
  getReadarrAuthors,
  restartArr,
} from "../api/client";
import type {
  ArrInfo,
  ReadarrAuthorDetailResponse,
  ReadarrAuthorEntry,
} from "../api/types";
import { useToast } from "../context/ToastContext";
import { useSearch } from "../context/SearchContext";
import { useWebUI } from "../context/WebUIContext";
import { useInterval } from "../hooks/useInterval";
import { IconImage } from "../components/IconImage";
import RefreshIcon from "../icons/refresh-arrow.svg";

const PAGE_SIZE = 50;

interface ReadarrViewProps {
  active: boolean;
}

export function ReadarrView({ active }: ReadarrViewProps): JSX.Element {
  const { push } = useToast();
  const { value: searchValue } = useSearch();
  const { liveArr } = useWebUI();
  const [instances, setInstances] = useState<ArrInfo[]>([]);
  const [selected, setSelected] = useState<string>("");
  const [authors, setAuthors] = useState<ReadarrAuthorEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(0);
  const [loading, setLoading] = useState(true);
  const [counts, setCounts] = useState({
    available: 0,
    monitored: 0,
    missing: 0,
  });
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [detail, setDetail] = useState<ReadarrAuthorDetailResponse | null>(
    null,
  );
  const expandedIdRef = useRef(expandedId);
  expandedIdRef.current = expandedId;
  const detailRequestGen = useRef(0);
  const detailAbortRef = useRef<AbortController | null>(null);

  const readarrInstances = useMemo(
    () => instances.filter((i) => i.type === "readarr"),
    [instances],
  );

  const loadArr = useCallback(async () => {
    try {
      const list = await getArrList();
      setInstances(list.arr ?? []);
    } catch (err) {
      push(
        `Failed to load Readarr instances: ${err instanceof Error ? err.message : String(err)}`,
        "error",
      );
    }
  }, [push]);

  const loadAuthors = useCallback(async () => {
    if (!selected) {
      setAuthors([]);
      setTotal(0);
      setLoading(false);
      return;
    }
    setLoading(true);
    try {
      const res = await getReadarrAuthors(
        selected,
        page,
        PAGE_SIZE,
        searchValue || undefined,
      );
      setAuthors(res.authors ?? []);
      setTotal(res.total ?? 0);
      setCounts({
        available: res.counts?.available ?? 0,
        monitored: res.counts?.monitored ?? 0,
        missing: res.counts?.missing ?? 0,
      });
    } catch (err) {
      push(
        `Failed to load Readarr authors: ${err instanceof Error ? err.message : String(err)}`,
        "error",
      );
    } finally {
      setLoading(false);
    }
  }, [page, push, searchValue, selected]);

  useEffect(() => {
    if (!active) return;
    void loadArr();
  }, [active, loadArr, liveArr]);

  useEffect(() => {
    if (readarrInstances.length > 0 && !selected) {
      setSelected(readarrInstances[0].category);
    }
  }, [readarrInstances, selected]);

  useEffect(() => {
    if (!active) return;
    void loadAuthors();
  }, [active, loadAuthors]);

  useInterval(() => {
    if (active) void loadAuthors();
  }, 30000);

  const openAuthor = async (id: number) => {
    detailAbortRef.current?.abort();
    detailAbortRef.current = null;
    if (expandedIdRef.current === id) {
      detailRequestGen.current += 1;
      expandedIdRef.current = null;
      setExpandedId(null);
      setDetail(null);
      return;
    }
    const requestGen = ++detailRequestGen.current;
    const ac = new AbortController();
    detailAbortRef.current = ac;
    expandedIdRef.current = id;
    setExpandedId(id);
    setDetail(null);
    try {
      const res = await getReadarrAuthorDetail(selected, id, ac.signal);
      if (
        ac.signal.aborted ||
        detailRequestGen.current !== requestGen ||
        expandedIdRef.current !== id
      ) {
        return;
      }
      setDetail(res);
    } catch (err) {
      if (
        ac.signal.aborted ||
        (err instanceof DOMException && err.name === "AbortError")
      ) {
        return;
      }
      if (
        detailRequestGen.current === requestGen &&
        expandedIdRef.current === id
      ) {
        setDetail(null);
      }
      push(
        `Failed to load author: ${err instanceof Error ? err.message : String(err)}`,
        "error",
      );
    }
  };

  const openInReadarr = (authorId: number) => {
    window.open(
      `/web/arr/${encodeURIComponent(selected)}/open/author/${authorId}`,
      "_blank",
    );
  };

  if (readarrInstances.length === 0) {
    return (
      <section className="card">
        <header className="card-header">
          <h2>Readarr</h2>
        </header>
        <p className="empty-state">No authors found.</p>
      </section>
    );
  }

  return (
    <section className="card">
      <header className="card-header">
        <h2>Readarr</h2>
        <div className="card-actions">
          <span className="muted">
            {counts.available}/{counts.monitored} available · {counts.missing}{" "}
            missing
          </span>
          <button
            type="button"
            className="icon-button"
            onClick={() => void loadAuthors()}
            title="Refresh"
          >
            <IconImage src={RefreshIcon} alt="" />
          </button>
          <button
            type="button"
            onClick={() => void restartArr(selected)}
            title="Restart worker"
          >
            Restart
          </button>
        </div>
      </header>

      {readarrInstances.length > 1 && (
        <div className="instance-sidebar">
          {readarrInstances.map((inst) => (
            <button
              key={inst.category}
              type="button"
              className={inst.category === selected ? "active" : ""}
              onClick={() => {
                detailAbortRef.current?.abort();
                detailAbortRef.current = null;
                setSelected(inst.category);
                setPage(0);
                detailRequestGen.current += 1;
                expandedIdRef.current = null;
                setExpandedId(null);
                setDetail(null);
              }}
            >
              {inst.name}
            </button>
          ))}
        </div>
      )}

      {loading && authors.length === 0 ? (
        <p className="muted">Loading authors…</p>
      ) : authors.length === 0 ? (
        <p className="empty-state">No books found.</p>
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Author</th>
              <th>Monitored</th>
              <th>Books</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {authors.map((entry) => {
              const author = entry.author;
              const expanded = expandedId === author.id;
              return (
                <Fragment key={author.id}>
                  <tr>
                    <td>
                      <button
                        type="button"
                        className="link-button"
                        onClick={() => void openAuthor(author.id)}
                      >
                        {author.name ?? "Unknown"}
                      </button>
                    </td>
                    <td>
                      <span
                        className={`track-status ${author.monitored ? "available" : "missing"}`}
                      >
                        {author.monitored ? "✓" : "✗"}
                      </span>
                    </td>
                    <td>
                      {author.booksAvailable ?? 0}/
                      {author.booksMonitored ?? author.bookCount ?? 0}
                    </td>
                    <td>
                      <button
                        type="button"
                        onClick={() => openInReadarr(author.id)}
                      >
                        Open in Readarr
                      </button>
                    </td>
                  </tr>
                  {expanded && (
                    <tr>
                      <td colSpan={4}>
                        {!detail ? (
                          <p className="muted">Loading books…</p>
                        ) : (detail.books ?? []).length === 0 ? (
                          <p className="empty-state">No books found.</p>
                        ) : (
                          <table className="data-table nested">
                            <thead>
                              <tr>
                                <th>Title</th>
                                <th>Year</th>
                                <th>Has File</th>
                                <th>Reason</th>
                              </tr>
                            </thead>
                            <tbody>
                              {detail.books.map((row) => (
                                <tr key={row.book.id}>
                                  <td>{row.book.title}</td>
                                  <td>
                                    {row.book.releaseDate
                                      ? new Date(
                                          row.book.releaseDate,
                                        ).getFullYear()
                                      : ""}
                                  </td>
                                  <td>
                                    <span
                                      className={`track-status ${row.book.hasFile ? "available" : "missing"}`}
                                    >
                                      {row.book.hasFile ? "✓" : "✗"}
                                    </span>
                                  </td>
                                  <td>{row.book.reason ?? ""}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        )}
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      )}

      {total > PAGE_SIZE && (
        <div className="pagination">
          <button
            type="button"
            disabled={page === 0}
            onClick={() => setPage((p) => Math.max(0, p - 1))}
          >
            Previous
          </button>
          <span>
            Page {page + 1} of {Math.max(1, Math.ceil(total / PAGE_SIZE))}
          </span>
          <button
            type="button"
            disabled={(page + 1) * PAGE_SIZE >= total}
            onClick={() => setPage((p) => p + 1)}
          >
            Next
          </button>
        </div>
      )}
    </section>
  );
}
