/** Public URL path prefix when the WebUI is served under a subpath (e.g. /qbitrr). */
let cachedUrlBaseFromMeta: string | null = null;

/** Derive UrlBase from the current page pathname before /web/meta is loaded. */
export function pathnameUrlBase(): string {
  const path = window.location.pathname.replace(/\/$/, "") || "/";

  const staticMatch = path.match(/^(.*)\/static\/index\.html$/);
  if (staticMatch) return staticMatch[1];

  const uiOrLoginMatch = path.match(/^(.*)\/(?:ui|login)$/);
  if (uiOrLoginMatch) return uiOrLoginMatch[1];

  const webMatch = path.match(/^(.*)\/web(?:\/|$)/);
  if (webMatch) return webMatch[1];

  return "";
}

/** Return the active UrlBase prefix (pathname first, then meta after load). */
export function getUrlBase(): string {
  if (cachedUrlBaseFromMeta !== null) {
    return cachedUrlBaseFromMeta;
  }
  return pathnameUrlBase();
}

/** Store UrlBase from /web/meta so API calls match the configured prefix. */
export function setUrlBaseFromMeta(base: string | undefined): void {
  if (base !== undefined) {
    cachedUrlBaseFromMeta = base;
  }
}

/** Clear cached UrlBase (for Vitest isolation). */
export function resetUrlBaseCacheForTests(): void {
  cachedUrlBaseFromMeta = null;
}

/** True when the current pathname is the login route (with or without UrlBase). */
export function isLoginPathname(pathname: string): boolean {
  const normalized = pathname.replace(/\/$/, "") || "/";
  if (normalized === "/login") return true;
  const base = getUrlBase();
  return base !== "" && normalized === `${base}/login`;
}

/** Prefix an app-relative path (must start with /) with the active UrlBase. */
export function webPath(path: string): string {
  if (!path.startsWith("/")) {
    throw new Error("webPath expects a path starting with /");
  }
  const base = getUrlBase();
  return base ? `${base}${path}` : path;
}
