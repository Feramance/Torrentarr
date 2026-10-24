import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { describe, it, expect, vi } from "vitest";

const source = readFileSync("public/sw.js", "utf8");
function worker(offline = false) {
  const listeners = new Map<string, (event: unknown) => void>();
  const caches = {
    match: vi.fn(),
    open: vi.fn(),
    keys: vi.fn(async () => ["torrentarr-old", "another-app-cache"]),
    delete: vi.fn(async () => true),
  };
  const fetch = offline
    ? vi.fn(async () => {
        throw new Error("offline");
      })
    : vi.fn(async () => new Response("{}"));
  runInNewContext(source, {
    URL,
    caches,
    fetch,
    console,
    self: {
      location: { origin: "http://localhost" },
      addEventListener: (name: string, callback: (event: unknown) => void) =>
        listeners.set(name, callback),
      clients: { claim: vi.fn() },
      skipWaiting: vi.fn(),
    },
  });
  return { listeners, caches, fetch };
}
describe("service-worker isolation", () => {
  it.each([
    "/web/config",
    "/api/torrents",
    "/prefix/web/config",
    "/prefix/api/torrents",
  ])("never caches private API responses at %s", async (path) => {
    const { listeners, caches, fetch } = worker();
    let response: Promise<Response> | undefined;
    listeners.get("fetch")!({
      request: new Request(`http://localhost${path}`),
      respondWith: (value: Promise<Response>) => {
        response = value;
      },
    });
    await response;
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(caches.open).not.toHaveBeenCalled();
    expect(caches.match).not.toHaveBeenCalled();
  });
  it("does not return another session's cached response when the API is offline", async () => {
    const { listeners, caches } = worker(true);
    let response: Promise<Response> | undefined;
    listeners.get("fetch")!({
      request: new Request("http://localhost/prefix/web/config"),
      respondWith: (value: Promise<Response>) => {
        response = value;
      },
    });
    await expect(response).rejects.toThrow("offline");
    expect(caches.match).not.toHaveBeenCalled();
  });
  it("only removes Torrentarr caches during activation", async () => {
    const { listeners, caches } = worker();
    let completion: Promise<unknown> | undefined;
    listeners.get("activate")!({
      waitUntil: (value: Promise<unknown>) => {
        completion = value;
      },
    });
    await completion;
    expect(caches.delete).toHaveBeenCalledWith("torrentarr-old");
    expect(caches.delete).not.toHaveBeenCalledWith("another-app-cache");
  });
});
