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
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response()));
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
  const view = render(
    <ToastProvider>
      <WebUIProvider>
        <p>settings</p>
      </WebUIProvider>
    </ToastProvider>,
  );
  await waitFor(() => expect(watchConfiguration).toHaveBeenCalled());
  act(() => receive(initial));
  expect(replace).not.toHaveBeenCalled();
  return view;
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
  await waitFor(() =>
    expect(replace).toHaveBeenCalledExactlyOnceWith(
      "http://127.0.0.1:6970/new/ui?filter=active#logs",
    ),
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

it("keeps the current document until the replacement listener responds", async () => {
  await mount();
  vi.mocked(fetch).mockRejectedValueOnce(new TypeError("connection refused"));
  let ready!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        ready = resolve;
      }),
  );
  act(() => receive({ ...initial, webui: { ...initial.webui, port: 6970 } }));
  expect(replace).not.toHaveBeenCalled();
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
  expect(replace).not.toHaveBeenCalled();
  expect(fetch).toHaveBeenLastCalledWith(
    new URL("http://localhost:6970/old/health"),
    expect.objectContaining({
      mode: "no-cors",
      credentials: "omit",
      cache: "no-store",
    }),
  );
  await act(async () => ready(new Response()));
  expect(replace).toHaveBeenCalledExactlyOnceWith(
    "http://localhost:6970/old/ui?filter=active#logs",
  );
});

it("discards superseded listener probes and follows the latest prefix", async () => {
  await mount();
  const probes: Array<{
    signal: AbortSignal;
    ready: (response: Response) => void;
  }> = [];
  vi.mocked(fetch).mockImplementation(
    (_url, options) =>
      new Promise((resolve) => {
        probes.push({ signal: options!.signal!, ready: resolve });
      }),
  );
  act(() => receive({ ...initial, webui: { ...initial.webui, port: 6970 } }));
  act(() =>
    receive({
      ...initial,
      webui: { ...initial.webui, port: 6970, urlBase: "/new" },
    }),
  );
  expect(probes[0].signal.aborted).toBe(true);
  await act(async () => probes[0].ready(new Response()));
  expect(replace).not.toHaveBeenCalled();
  await act(async () => probes[1].ready(new Response()));
  expect(replace).toHaveBeenCalledExactlyOnceWith(
    "http://localhost:6970/new/ui?filter=active#logs",
  );
});

it("cancels navigation when the provider unmounts", async () => {
  const view = await mount();
  let ready!: (response: Response) => void;
  vi.mocked(fetch).mockImplementationOnce(
    () =>
      new Promise((resolve) => {
        ready = resolve;
      }),
  );
  act(() => receive({ ...initial, webui: { ...initial.webui, port: 6970 } }));
  const signal = vi.mocked(fetch).mock.calls[0][1]!.signal!;
  view.unmount();
  expect(signal.aborted).toBe(true);
  await act(async () => ready(new Response()));
  expect(replace).not.toHaveBeenCalled();
});

it("probes and navigates to an IPv6 listener", async () => {
  await mount();
  act(() =>
    receive({
      ...initial,
      webui: { ...initial.webui, host: "::1", port: 6970 },
    }),
  );
  await waitFor(() =>
    expect(replace).toHaveBeenCalledExactlyOnceWith(
      "http://[::1]:6970/old/ui?filter=active#logs",
    ),
  );
  expect(fetch).toHaveBeenCalledWith(
    new URL("http://[::1]:6970/old/health"),
    expect.anything(),
  );
});
