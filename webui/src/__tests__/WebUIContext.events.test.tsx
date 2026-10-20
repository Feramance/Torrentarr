import { act, render, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { WebUIProvider } from "../context/WebUIContext";
import { ToastProvider } from "../context/ToastContext";
import { watchConfiguration } from "../api/client";
import { resetUrlBaseCacheForTests } from "../api/urlBase";
import type { LiveConfigurationEvent } from "../api/types";

vi.mock("../api/client", () => ({
  getConfig: vi.fn(async () => ({ WebUI: {} })),
  updateConfig: vi.fn(),
  watchConfiguration: vi.fn(),
}));
let receive: (event: LiveConfigurationEvent) => void;
const replace = vi.fn();
const initial: LiveConfigurationEvent = {
  application: { acceptedRevision: 1, state: "applied", components: {} },
  webui: {
    host: "0.0.0.0",
    port: 6969,
    urlBase: "/old",
    behindHttpsProxy: false,
    theme: "Dark",
    viewDensity: "Comfortable",
    liveArr: false,
  },
};
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  vi.stubGlobal("location", {
    href: "http://localhost:6969/old/ui?filter=active#logs",
    pathname: "/old/ui",
    origin: "http://localhost:6969",
    replace,
  });
  vi.mocked(watchConfiguration).mockImplementation((listener, signal) => {
    receive = listener;
    return new Promise((resolve) =>
      signal.addEventListener("abort", () => resolve(), { once: true }),
    );
  });
});
afterEach(() => {
  vi.unstubAllGlobals();
  resetUrlBaseCacheForTests();
});
async function mount() {
  render(
    <ToastProvider>
      <WebUIProvider>
        <p>settings</p>
      </WebUIProvider>
    </ToastProvider>,
  );
  await waitFor(() => expect(watchConfiguration).toHaveBeenCalled());
  act(() => receive(initial));
  expect(replace).not.toHaveBeenCalled();
}
it.each([
  ["/new", false, "http://localhost:6969/new/ui?filter=active#logs"],
  ["/new", true, "http://localhost:6969/new/ui?filter=active#logs"],
  ["", false, "http://localhost:6969/ui?filter=active#logs"],
])(
  "reloads the document after rebasing to %s (proxy=%s)",
  async (urlBase, behindHttpsProxy, expected) => {
    await mount();
    act(() =>
      receive({
        ...initial,
        webui: { ...initial.webui, urlBase, behindHttpsProxy },
      }),
    );
    expect(replace).toHaveBeenCalledExactlyOnceWith(expected);
  },
);
it("combines listener and prefix changes into one document navigation", async () => {
  await mount();
  act(() =>
    receive({
      ...initial,
      webui: {
        ...initial.webui,
        host: "127.0.0.1",
        port: 6970,
        urlBase: "/new",
      },
    }),
  );
  expect(replace).toHaveBeenCalledExactlyOnceWith(
    "http://127.0.0.1:6970/new/ui?filter=active#logs",
  );
});
it("applies theme changes without navigating", async () => {
  await mount();
  act(() =>
    receive({ ...initial, webui: { ...initial.webui, theme: "Light" } }),
  );
  expect(document.documentElement).toHaveAttribute("data-theme", "light");
  expect(replace).not.toHaveBeenCalled();
});
