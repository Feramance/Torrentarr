import { describe, it, expect, beforeEach, vi } from "vitest";

function stubLocation(pathname: string) {
  vi.stubGlobal("location", { pathname } as Location);
}

async function loadUrlBase() {
  vi.resetModules();
  return import("../../api/urlBase");
}

describe("urlBase", () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
  });

  it("pathnameUrlBase extracts prefix from static index path", async () => {
    stubLocation("/qbitrr/static/index.html");
    const { pathnameUrlBase } = await loadUrlBase();
    expect(pathnameUrlBase()).toBe("/qbitrr");
  });

  it("pathnameUrlBase returns empty string for root paths", async () => {
    stubLocation("/ui");
    const { pathnameUrlBase } = await loadUrlBase();
    expect(pathnameUrlBase()).toBe("");
  });

  it("pathnameUrlBase extracts prefix from /ui and /login under UrlBase", async () => {
    stubLocation("/torrentarr/ui");
    const { pathnameUrlBase } = await loadUrlBase();
    expect(pathnameUrlBase()).toBe("/torrentarr");
  });

  it("pathnameUrlBase extracts prefix from nested login path", async () => {
    stubLocation("/torrentarr/login/");
    const { pathnameUrlBase } = await loadUrlBase();
    expect(pathnameUrlBase()).toBe("/torrentarr");
  });

  it("isLoginPathname matches login routes with UrlBase", async () => {
    stubLocation("/torrentarr/login");
    const { isLoginPathname } = await loadUrlBase();
    expect(isLoginPathname("/torrentarr/login")).toBe(true);
    expect(isLoginPathname("/torrentarr/ui")).toBe(false);
    expect(isLoginPathname("/login")).toBe(true);
  });

  it("getUrlBase prefers meta cache over pathname", async () => {
    stubLocation("/qbitrr/static/index.html");
    const { getUrlBase, setUrlBaseFromMeta } = await loadUrlBase();
    setUrlBaseFromMeta("/torrentarr");
    expect(getUrlBase()).toBe("/torrentarr");
  });

  it("webPath prefixes active UrlBase", async () => {
    stubLocation("/ui");
    const { webPath, setUrlBaseFromMeta } = await loadUrlBase();
    setUrlBaseFromMeta("/torrentarr");
    expect(webPath("/web/status")).toBe("/torrentarr/web/status");
  });

  it("webPath returns path unchanged when UrlBase is empty", async () => {
    stubLocation("/ui");
    const { webPath } = await loadUrlBase();
    expect(webPath("/web/meta")).toBe("/web/meta");
  });

  it("webPath throws when path does not start with /", async () => {
    stubLocation("/ui");
    const { webPath } = await loadUrlBase();
    expect(() => webPath("web/meta")).toThrow(
      "webPath expects a path starting with /",
    );
  });
});
