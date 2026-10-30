import { beforeEach, describe, expect, it, vi } from "vitest";
import { handleChunkPreloadError } from "../../utils/chunkRecovery";

describe("lazy-loaded chunk recovery", () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  it("prevents the preload error and reloads once after a deployment", () => {
    const reload = vi.fn();
    const event = new Event("vite:preloadError", { cancelable: true });
    handleChunkPreloadError(event, sessionStorage, reload, 100_000);
    expect(event.defaultPrevented).toBe(true);
    expect(reload).toHaveBeenCalledTimes(1);
    handleChunkPreloadError(
      new Event("vite:preloadError", { cancelable: true }),
      sessionStorage,
      reload,
      100_005,
    );
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("allows another refresh after the guard period", () => {
    const reload = vi.fn();
    handleChunkPreloadError(
      new Event("vite:preloadError", { cancelable: true }),
      sessionStorage,
      reload,
      100_000,
    );
    handleChunkPreloadError(
      new Event("vite:preloadError", { cancelable: true }),
      sessionStorage,
      reload,
      160_100,
    );
    expect(reload).toHaveBeenCalledTimes(2);
  });

  it("does not reload when session storage is unavailable", () => {
    const reload = vi.fn();
    const storage = {
      getItem: () => {
        throw new Error("not available");
      },
    } as unknown as Storage;
    handleChunkPreloadError(
      new Event("vite:preloadError", { cancelable: true }),
      storage,
      reload,
      100_000,
    );
    expect(reload).not.toHaveBeenCalled();
  });
});
