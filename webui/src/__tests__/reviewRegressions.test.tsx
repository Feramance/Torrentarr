import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { fireEvent, render, screen, act } from "@testing-library/react";
import { pathnameUrlBase, resetUrlBaseCacheForTests } from "../api/urlBase";
import { sectionHasCategoryChanges } from "../pages/config/configValidation";
import * as api from "../api/client";
import { SecureField } from "../pages/config/configFieldComponents";
import {
  ToastProvider,
  useToast,
  resetToastDedupeForTests,
} from "../context/ToastContext";
import { useArrBrowseMode } from "../hooks/useArrBrowseMode";

beforeEach(() => {
  localStorage.clear();
  resetToastDedupeForTests();
});
afterEach(() => {
  document.querySelector("base")?.remove();
  window.history.replaceState({}, "", "/");
  resetUrlBaseCacheForTests();
  vi.restoreAllMocks();
});

it.each(["/index.html", "/prefix", "/prefix/", "/nested/prefix/ui"])(
  "resolves deployment entry %s",
  (path) => {
    window.history.replaceState({}, "", path);
    expect(pathnameUrlBase()).toBe(
      path === "/index.html"
        ? ""
        : path.startsWith("/nested")
          ? "/nested/prefix"
          : "/prefix",
    );
  },
);
it("uses the injected base for nested bare deployment entries", () => {
  window.history.replaceState({}, "", "/nested/prefix/");
  const base = document.createElement("base");
  base.href = "/nested/prefix/";
  document.head.append(base);
  expect(pathnameUrlBase()).toBe("/nested/prefix");
});
it.each([
  ["Settings", { Settings: { FailedCategory: "new" } }],
  ["qBit", { qBit: { ManagedCategories: ["new"] } }],
  [
    "TorrentClient.remote",
    { TorrentClient: { remote: { ManagedCategories: ["new"] } } },
  ],
  ["Radarr", { Radarr: { Managed: false } }],
])("detects category-affecting edits in %s", (section, edited) => {
  expect(sectionHasCategoryChanges(section, edited, {})).toBe(true);
});
it("generates a 128-bit hex credential without a weak random fallback", () => {
  const random = vi
    .spyOn(crypto, "getRandomValues")
    .mockImplementation((bytes) => {
      (bytes as Uint8Array).fill(171);
      return bytes;
    });
  const change = vi.fn();
  render(
    <SecureField label="Token" value="" onChange={change} isWebApiToken />,
  );
  fireEvent.click(screen.getByRole("button", { name: /generate|refresh/i }));
  expect(random).toHaveBeenCalled();
  expect(change).toHaveBeenCalledWith("ab".repeat(16));
});
it("loads the saved Web API token only on reveal and clears it on hide", async () => {
  const getToken = vi
    .spyOn(api, "getToken")
    .mockResolvedValue({ token: "saved-token" });
  const change = vi.fn();
  render(
    <SecureField
      label="Token"
      value="[redacted]"
      onChange={change}
      isWebApiToken
    />,
  );
  const input = screen.getByLabelText("Token");
  expect(input).toHaveValue("");
  expect(getToken).not.toHaveBeenCalled();

  fireEvent.click(screen.getByRole("button", { name: "Show Token" }));
  const hide = await screen.findByRole("button", { name: "Hide Token" });
  expect(input).toHaveValue("saved-token");
  expect(input).toHaveAttribute("type", "text");
  fireEvent.click(hide);
  expect(input).toHaveValue("");
  expect(input).toHaveAttribute("type", "password");
  expect(change).not.toHaveBeenCalled();
});

it("keeps the saved token masked on reveal failure and discards a stale reveal after editing", async () => {
  let resolveReveal!: (value: { token: string }) => void;
  vi.spyOn(api, "getToken")
    .mockRejectedValueOnce(new Error("Unavailable"))
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveReveal = resolve;
        }),
    );
  const change = vi.fn();
  const field = (value: string) => (
    <SecureField label="Token" value={value} onChange={change} isWebApiToken />
  );
  const { rerender } = render(field("[redacted]"));
  const input = screen.getByLabelText("Token");
  const show = screen.getByRole("button", { name: "Show Token" });

  fireEvent.click(show);
  expect(await screen.findByRole("alert")).toHaveTextContent("Could not load");
  expect(input).toHaveAttribute("type", "password");

  fireEvent.click(show);
  fireEvent.change(input, { target: { value: "draft-token" } });
  rerender(field("draft-token"));
  await act(async () => resolveReveal({ token: "old-token" }));
  expect(input).toHaveAttribute("type", "password");
  fireEvent.click(show);
  expect(input).toHaveValue("draft-token");
  expect(input).toHaveAttribute("type", "text");
  expect(change).toHaveBeenCalledOnce();
});

it("shows a repeated toast immediately after dismissal", () => {
  let actions!: ReturnType<typeof useToast>;
  function Harness() {
    actions = useToast();
    return (
      <>
        {actions.toasts.map((t) => (
          <p key={t.id}>{t.message}</p>
        ))}
      </>
    );
  }
  render(
    <ToastProvider>
      <Harness />
    </ToastProvider>,
  );
  act(() => actions.push("retry", "error"));
  act(() => actions.dismiss(actions.toasts[0].id));
  act(() => actions.push("retry", "error"));
  expect(screen.getByText("retry")).toBeInTheDocument();
});
it("retains the legacy catalog browse preference", () => {
  localStorage.setItem("torrentarr.arrBrowseMode.radarr", "list");
  function Harness() {
    return <p>{useArrBrowseMode("radarr").mode}</p>;
  }
  render(<Harness />);
  expect(screen.getByText("list")).toBeInTheDocument();
});

it("loads and applies backend settings when both browser stores are unavailable", async () => {
  const api = await import("../api/client");
  const { WebUIProvider, useWebUI } = await import("../context/WebUIContext");
  vi.spyOn(api, "getConfig").mockResolvedValue({
    WebUI: { Theme: "Light", ViewDensity: "Compact" },
  });
  vi.spyOn(api, "watchConfiguration").mockImplementation(
    (_receive, signal) =>
      new Promise((resolve) =>
        signal.addEventListener("abort", () => resolve(), { once: true }),
      ),
  );
  vi.spyOn(api, "updateConfig").mockResolvedValue({
    configReloaded: true,
    reloadType: "live",
  });
  for (const store of ["localStorage", "sessionStorage"] as const)
    vi.spyOn(window, store, "get").mockImplementation(() => {
      throw new Error("blocked");
    });
  function Harness() {
    const ui = useWebUI();
    return (
      <button onClick={() => ui.setTheme("dark")}>
        {ui.theme} {ui.viewDensity}
      </button>
    );
  }
  render(
    <ToastProvider>
      <WebUIProvider>
        <Harness />
      </WebUIProvider>
    </ToastProvider>,
  );
  fireEvent.click(await screen.findByRole("button", { name: "light compact" }));
  expect(document.documentElement).toHaveAttribute("data-theme", "dark");
  expect(api.updateConfig).toHaveBeenCalledWith({
    changes: { "WebUI.Theme": "Dark" },
  });
});
