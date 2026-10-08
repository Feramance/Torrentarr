import type { JSX, MouseEvent } from "react";

interface ArrExternalLinkProps {
  readonly href: string | null;
  readonly arrName: "Radarr" | "Sonarr" | "Lidarr";
}

/** Shared "Open in *Arr" action link for detail modals. */
export function ArrExternalLink({
  href,
  arrName,
}: ArrExternalLinkProps): JSX.Element | null {
  if (!href) {
    return null;
  }
  const openAuthenticated = async (event: MouseEvent<HTMLAnchorElement>) => {
    const token = localStorage.getItem("token");
    if (!token) return;
    event.preventDefault();
    const response = await fetch(href, {
      credentials: "include",
      headers: {
        Authorization: `Bearer ${token}`,
        "X-Requested-With": "XMLHttpRequest",
      },
    });
    if (response.ok) {
      const result = (await response.json()) as { url?: string };
      if (result.url) window.open(result.url, "_blank", "noopener,noreferrer");
    }
  };
  return (
    <div className="arr-detail-actions">
      <a
        className="btn small outline"
        href={href}
        target="_blank"
        rel="noreferrer"
        onClick={openAuthenticated}
      >
        Open in {arrName}
      </a>
    </div>
  );
}

interface ArrInstanceHintProps {
  readonly instanceLabel?: string | null;
  readonly qualityProfileName?: string | null;
}

/** Instance + quality profile hint line (Sonarr series group, Lidarr artist). */
export function ArrInstanceHint({
  instanceLabel,
  qualityProfileName,
}: ArrInstanceHintProps): JSX.Element | null {
  const hintLabel =
    instanceLabel != null && String(instanceLabel).trim() !== ""
      ? String(instanceLabel).trim()
      : null;
  const profileName = qualityProfileName?.trim()
    ? qualityProfileName.trim()
    : null;
  if (hintLabel == null && !profileName) {
    return null;
  }
  return (
    <p className="hint" style={{ margin: 0 }}>
      {hintLabel != null ? (
        <>
          <strong>Instance:</strong> {hintLabel}
        </>
      ) : null}
      {hintLabel != null && profileName ? <> • </> : null}
      {profileName ? (
        <>
          <strong>Profile:</strong> {profileName}
        </>
      ) : null}
    </p>
  );
}
