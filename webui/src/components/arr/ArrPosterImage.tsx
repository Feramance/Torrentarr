import {
  useEffect,
  useRef,
  useState,
  type JSX,
  type SyntheticEvent,
} from "react";
import { enqueuePosterReveal } from "../../utils/posterLoadQueue";
import {
  POSTER_MAX_RETRIES,
  posterRetryBackoffMs,
  withPosterRetryParam,
} from "../../utils/posterRetry";
import { observePosterVisibility } from "../../utils/sharedIntersectionObserver";

interface ArrPosterImageProps {
  src: string;
  alt: string;
  className?: string;
}

function getToken(): string | null {
  try {
    return (
      localStorage.getItem("token") ||
      localStorage.getItem("webui-token") ||
      sessionStorage.getItem("token") ||
      sessionStorage.getItem("webui-token") ||
      sessionStorage.getItem("webui_token")
    );
  } catch {
    return null;
  }
}

async function finalizePosterDisplay(img: HTMLImageElement): Promise<void> {
  try {
    await img.decode();
  } catch {
    // Still show the bitmap if decode rejects (unsupported or huge image).
  }
}

/**
 * Poster for icon browse: shared intersection gate + bounded global queue limits
 * parallel thumbnail loads; shows fallback until the image is ready to paint.
 *
 * Failed loads retry up to {@link POSTER_MAX_RETRIES} times (backoff + cache-bust) via the
 * poster queue. The queue slot is released when the network load settles (``onLoad`` /
 * ``onError``), before awaiting ``decode()``, so decode work does not starve the queue.
 */
export function ArrPosterImage({
  src,
  alt,
  className,
}: ArrPosterImageProps): JSX.Element {
  const [failed, setFailed] = useState(false);
  const [released, setReleased] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const [blobSrc, setBlobSrc] = useState<string | null>(null);
  const rootRef = useRef<HTMLDivElement>(null);
  const loadIdRef = useRef(0);
  const attemptRef = useRef(0);
  const retryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const retryEnqueueCancelRef = useRef<(() => void) | null>(null);
  const blobUrlRef = useRef<string | null>(null);
  const cancelledRef = useRef(false);
  // Held while the slot is checked out; called when the image network settles (load/error/
  // unmount) so we never pin the queue past the lifetime of this poster.
  const releaseSlotRef = useRef<(() => void) | null>(null);

  const clearRetryTimer = () => {
    if (retryTimerRef.current !== null) {
      clearTimeout(retryTimerRef.current);
      retryTimerRef.current = null;
    }
  };

  const releaseSlot = () => {
    if (releaseSlotRef.current) {
      releaseSlotRef.current();
      releaseSlotRef.current = null;
    }
  };

  const enqueueLoad = () => {
    return enqueuePosterReveal((release) => {
      releaseSlotRef.current = release;
      setReleased(true);
    });
  };

  const displaySrc = withPosterRetryParam(src, attempt);
  const token = getToken();

  useEffect(() => {
    cancelledRef.current = false;
    loadIdRef.current += 1;
    attemptRef.current = 0;
    clearRetryTimer();
    retryEnqueueCancelRef.current?.();
    retryEnqueueCancelRef.current = null;
    // Reset load state when the poster URL changes (new row / retry base src).
    // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional reset on src identity change
    setLoaded(false);
    setFailed(false);
    setBlobSrc(null);
    setReleased(false);
    setAttempt(0);
    if (releaseSlotRef.current) {
      releaseSlotRef.current();
      releaseSlotRef.current = null;
    }
    if (blobUrlRef.current) {
      URL.revokeObjectURL(blobUrlRef.current);
      blobUrlRef.current = null;
    }
    return () => {
      cancelledRef.current = true;
      clearRetryTimer();
      retryEnqueueCancelRef.current?.();
      retryEnqueueCancelRef.current = null;
    };
  }, [src]);

  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    let cancelEnqueue: (() => void) | null = null;
    const unobserve = observePosterVisibility(el, () => {
      cancelEnqueue = enqueueLoad();
    });
    return () => {
      unobserve();
      if (cancelEnqueue) cancelEnqueue();
    };
    // Only re-bind visibility when the base src changes; retries re-enqueue directly.
  }, [src]);

  useEffect(() => {
    return () => {
      cancelledRef.current = true;
      clearRetryTimer();
      retryEnqueueCancelRef.current?.();
      retryEnqueueCancelRef.current = null;
      if (releaseSlotRef.current) {
        releaseSlotRef.current();
        releaseSlotRef.current = null;
      }
      if (blobUrlRef.current) URL.revokeObjectURL(blobUrlRef.current);
    };
  }, []);

  const fallbackCls = ["arr-poster-fallback"];
  if (className) {
    fallbackCls.push(className);
  }

  const scheduleRetry = () => {
    const current = attemptRef.current;
    if (current >= POSTER_MAX_RETRIES) {
      setFailed(true);
      releaseSlot();
      return;
    }
    releaseSlot();
    setReleased(false);
    setLoaded(false);
    const delay = posterRetryBackoffMs(current);
    clearRetryTimer();
    retryTimerRef.current = setTimeout(() => {
      retryTimerRef.current = null;
      if (cancelledRef.current) return;
      const next = current + 1;
      attemptRef.current = next;
      setAttempt(next);
      retryEnqueueCancelRef.current = enqueueLoad();
    }, delay);
  };

  useEffect(() => {
    if (!released || !token) return;
    const controller = new AbortController();
    void fetch(displaySrc, {
      credentials: "include",
      headers: { Authorization: `Bearer ${token}` },
      signal: controller.signal,
    })
      .then((response) => {
        if (!response.ok)
          throw new Error(`Thumbnail request failed: ${response.status}`);
        return response.blob();
      })
      .then((blob) => {
        if (controller.signal.aborted) return;
        const url = URL.createObjectURL(blob);
        if (blobUrlRef.current) URL.revokeObjectURL(blobUrlRef.current);
        blobUrlRef.current = url;
        setBlobSrc(url);
      })
      .catch(() => {
        if (!controller.signal.aborted) scheduleRetry();
      });
    return () => controller.abort();
    // scheduleRetry intentionally uses the current render's retry state.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [displaySrc, released, token]);

  const onImgLoad = (ev: SyntheticEvent<HTMLImageElement>) => {
    const token = loadIdRef.current;
    const img = ev.currentTarget;
    releaseSlot();
    void finalizePosterDisplay(img).then(() => {
      if (token === loadIdRef.current) {
        setLoaded(true);
      }
    });
  };

  const onImgError = () => {
    if (attemptRef.current >= POSTER_MAX_RETRIES) {
      setFailed(true);
      releaseSlot();
      return;
    }
    scheduleRetry();
  };

  if (failed) {
    return (
      <div
        className={
          className ? `arr-poster-fallback ${className}` : "arr-poster-fallback"
        }
        aria-hidden
      />
    );
  }

  return (
    <div
      ref={rootRef}
      className={
        loaded
          ? "arr-poster-image-wrap arr-poster-image-wrap--ready"
          : "arr-poster-image-wrap"
      }
    >
      {!released || (token && !blobSrc) ? (
        <div className={fallbackCls.join(" ")} aria-hidden />
      ) : (
        <>
          <img
            key={`${src}-${attempt}`}
            src={blobSrc ?? displaySrc}
            alt={alt}
            className={[className, "arr-poster-layer"]
              .filter(Boolean)
              .join(" ")}
            decoding="async"
            onLoad={onImgLoad}
            onError={onImgError}
          />
          {!loaded && (
            <div
              className={[...fallbackCls, "arr-poster-fallback--overlay"].join(
                " ",
              )}
              aria-hidden
            />
          )}
        </>
      )}
    </div>
  );
}
