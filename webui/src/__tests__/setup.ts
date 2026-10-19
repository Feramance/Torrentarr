import "@testing-library/jest-dom";
import { afterEach, beforeEach, vi } from "vitest";
import { cleanup, configure } from "@testing-library/react";

// Allow lazy production-sized routes to settle on constrained CI/build hosts.
configure({ asyncUtilTimeout: 3000 });
window.scrollTo = vi.fn();

// Clean up after each test to avoid state leakage between tests
afterEach(() => {
  cleanup();
});

beforeEach(async () => {
  const actual =
    await vi.importActual<typeof import("../api/client")>("../api/client");
  actual.invalidateGetCache(["/web/"]);
});
if (!globalThis.ResizeObserver)
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as typeof ResizeObserver;
if (!globalThis.IntersectionObserver)
  globalThis.IntersectionObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof IntersectionObserver;
