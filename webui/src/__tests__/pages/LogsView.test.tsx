import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LogsView } from "../../pages/LogsView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import * as api from "../../api/client";

vi.mock("@melloware/react-logviewer", () => ({
  LazyLog: ({ text }: { text: string }) => (
    <div data-testid="lazy-log">{text}</div>
  ),
}));
vi.mock("../../api/client", () => ({
  getConfig: vi.fn(),
  updateConfig: vi.fn(),
  getLogs: vi.fn(),
  getLogTailJson: vi.fn(),
  getLogDelta: vi.fn(),
  searchLogs: vi.fn(),
  getLogDownloadUrl: () => "/download",
  getLogStreamUrl: () => "/stream",
}));
class Stream {
  static instances: Stream[] = [];
  listeners = new Map<string, (event: MessageEvent) => void>();
  close = vi.fn();
  onerror: (() => void) | null = null;
  constructor() {
    Stream.instances.push(this);
  }
  addEventListener(type: string, listener: (event: MessageEvent) => void) {
    this.listeners.set(type, listener);
  }
  send(type: string, content: string) {
    this.listeners.get(type)?.({
      data: JSON.stringify({
        ...payload,
        content,
        rotated: type === "rotated",
      }),
    } as MessageEvent);
  }
}
const payload = {
  start_bytes: 50,
  start_inode: 2,
  content: "[now] INFO : ready\n[now] ERROR : failed\n",
  next_bytes: 100,
  size: 100,
  inode: 1,
  rotated: false,
  truncated: false,
};
function view(active = true) {
  return render(
    <ToastProvider>
      <WebUIProvider>
        <LogsView active={active} />
      </WebUIProvider>
    </ToastProvider>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  Stream.instances = [];
  vi.stubGlobal("EventSource", Stream);
  vi.mocked(api.getConfig).mockResolvedValue({
    WebUI: { Theme: "Dark", LiveArr: false },
  });
  vi.mocked(api.getLogs).mockResolvedValue({
    files: [{ name: "All.log", size: 100, modified: "2026-10-18" }],
  });
  vi.mocked(api.getLogTailJson).mockResolvedValue(payload);
  vi.mocked(api.getLogDelta).mockResolvedValue({ ...payload, content: "" });
});
afterEach(() => {
  // Unmount first so deferred live-stream effects cannot use EventSource after it is unstubbed.
  cleanup();
  vi.unstubAllGlobals();
});
describe("log viewer parity", () => {
  it("falls back to delta polling after repeated stream failures", async () => {
    view();
    await waitFor(() => expect(Stream.instances.length).toBeGreaterThan(0));
    act(() => Stream.instances.at(-1)!.onerror?.());
    await waitFor(() => expect(Stream.instances.length).toBe(2), {
      timeout: 2000,
    });
    act(() => Stream.instances.at(-1)!.onerror?.());
    await waitFor(() => expect(api.getLogDelta).toHaveBeenCalled(), {
      timeout: 3000,
    });
  });
  it("reopens the live stream after refreshing the tail and ignores the old stream", async () => {
    view();
    await waitFor(() => expect(Stream.instances.length).toBe(1));
    const old = Stream.instances[0];
    fireEvent.click(screen.getByRole("button", { name: "More" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Refresh" }));
    await waitFor(() => expect(Stream.instances.length).toBeGreaterThan(1));
    expect(old.close).toHaveBeenCalled();
    act(() => old.send("append", "obsolete append\n"));
    act(() => Stream.instances.at(-1)!.send("append", "fresh append\n"));
    expect(screen.getByTestId("lazy-log")).toHaveTextContent("fresh append");
    expect(screen.getByTestId("lazy-log")).not.toHaveTextContent(
      "obsolete append",
    );
  });
  it("filters loaded lines by level", async () => {
    view();
    await screen.findByTestId("lazy-log");
    fireEvent.click(screen.getByRole("button", { name: "ERROR" }));
    expect(screen.getByTestId("lazy-log")).toHaveTextContent("failed");
    expect(screen.getByTestId("lazy-log")).not.toHaveTextContent("ready");
  });
  it("keeps Follow independent from Live and closes streams when inactive", async () => {
    const { rerender } = view();
    await waitFor(() => expect(Stream.instances.length).toBeGreaterThan(0));
    const stream = Stream.instances.at(-1)!;
    fireEvent.click(screen.getByLabelText("Follow"));
    expect(stream.close).not.toHaveBeenCalled();
    rerender(
      <ToastProvider>
        <WebUIProvider>
          <LogsView active={false} />
        </WebUIProvider>
      </ToastProvider>,
    );
    await waitFor(() => expect(stream.close).toHaveBeenCalled());
  });
  it("appends SSE events and replaces the window on rotation", async () => {
    view();
    await waitFor(() => expect(Stream.instances.length).toBeGreaterThan(0));
    act(() =>
      Stream.instances.at(-1)!.send("append", "[now] INFO : appended\n"),
    );
    expect(screen.getByTestId("lazy-log")).toHaveTextContent("appended");
    act(() =>
      Stream.instances.at(-1)!.send("rotated", "[now] INFO : replacement\n"),
    );
    expect(screen.getByTestId("lazy-log")).toHaveTextContent("replacement");
    expect(screen.getByTestId("lazy-log")).not.toHaveTextContent("appended");
  });
  it("loads older windows and searches rotated files", async () => {
    view();
    await screen.findByTestId("lazy-log");
    fireEvent.click(screen.getByRole("button", { name: /Load older logs/ }));
    await waitFor(() =>
      expect(api.getLogTailJson).toHaveBeenCalledWith(
        "All.log",
        expect.objectContaining({ beforeBytes: 50, sinceBytes: 100, inode: 1 }),
      ),
    );
    vi.mocked(api.searchLogs).mockResolvedValue({
      query: "failure",
      truncated: true,
      files_searched: ["All.log"],
      matches: [{ file: "All.log", line: 50, text: "failure" }],
    });
    fireEvent.change(
      screen.getByPlaceholderText("Search log file on server…"),
      { target: { value: "failure" } },
    );
    fireEvent.click(screen.getByRole("button", { name: "Search file" }));
    await waitFor(() =>
      expect(api.searchLogs).toHaveBeenCalledWith(
        "All.log",
        "failure",
        expect.objectContaining({ includeRotated: true }),
      ),
    );
    expect(await screen.findByText("failure")).toBeInTheDocument();
  });
});

it("filters native Host and Serilog worker levels", async () => {
  vi.mocked(api.getLogTailJson).mockResolvedValue({
    ...payload,
    content:
      "[2026-10-20][ERROR   ][Torrentarr]host failed\n2026-10-20 12:00:00 +00:00 [ERR] worker failed\n[now] INFO : fine\n",
  });
  view();
  await screen.findByTestId("lazy-log");
  fireEvent.click(screen.getByRole("button", { name: "ERROR" }));
  expect(screen.getByTestId("lazy-log")).toHaveTextContent("host failed");
  expect(screen.getByTestId("lazy-log")).toHaveTextContent("worker failed");
  expect(screen.getByTestId("lazy-log")).not.toHaveTextContent("fine");
});

it("serializes slow polls and discards their results after switching files", async () => {
  vi.mocked(api.getLogs).mockResolvedValue({ files: ["All.log", "Other.log"] });
  vi.mocked(api.getLogTailJson).mockImplementation(async (name) => ({
    ...payload,
    content: name,
  }));
  let complete!: (value: typeof payload) => void;
  vi.mocked(api.getLogDelta).mockImplementation(
    () =>
      new Promise((resolve) => {
        complete = resolve;
      }),
  );
  view();
  await waitFor(() => expect(Stream.instances.length).toBeGreaterThan(0));
  vi.useFakeTimers();
  try {
    act(() => Stream.instances.at(-1)!.onerror?.());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });
    act(() => Stream.instances.at(-1)!.onerror?.());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1800);
    });
    expect(api.getLogDelta).toHaveBeenCalledTimes(1);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3600);
    });
    expect(api.getLogDelta).toHaveBeenCalledTimes(1);
    fireEvent.keyDown(screen.getByLabelText("Log File"), { key: "ArrowDown" });
    fireEvent.click(screen.getByText("Other.log"));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    await act(async () => complete({ ...payload, content: "obsolete lines" }));
    expect(screen.getByTestId("lazy-log")).toHaveTextContent("Other.log");
    expect(screen.getByTestId("lazy-log")).not.toHaveTextContent(
      "obsolete lines",
    );
  } finally {
    vi.useRealTimers();
  }
});

it("selects the current aggregate instead of the legacy All.log", async () => {
  vi.mocked(api.getLogs).mockResolvedValue({
    files: ["All.log", "all-20261029.log", "all-20261030.log"],
  });
  view();
  await waitFor(() =>
    expect(api.getLogTailJson).toHaveBeenCalledWith(
      "all-20261030.log",
      expect.anything(),
    ),
  );
});

it("keeps exception continuation lines when filtering errors", async () => {
  vi.mocked(api.getLogTailJson).mockResolvedValue({
    ...payload,
    content:
      "[now] ERROR : failed\nIOException: denied\n   at Pause()\n[now] INFO : ready\n",
  });
  view();
  await screen.findByTestId("lazy-log");
  fireEvent.click(screen.getByRole("button", { name: "ERROR" }));
  expect(screen.getByTestId("lazy-log")).toHaveTextContent(
    "IOException: denied",
  );
  expect(screen.getByTestId("lazy-log")).toHaveTextContent("at Pause()");
  expect(screen.getByTestId("lazy-log")).not.toHaveTextContent("ready");
});

it("follows aggregate rollover while live", async () => {
  vi.useFakeTimers();
  try {
    vi.mocked(api.getLogs).mockResolvedValue({ files: ["all-20261030.log"] });
    view();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(api.getLogTailJson).toHaveBeenCalledWith(
      "all-20261030.log",
      expect.anything(),
    );
    vi.mocked(api.getLogs).mockResolvedValue({
      files: ["all-20261030.log", "all-20261031.log"],
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15050);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(api.getLogTailJson).toHaveBeenCalledWith(
      "all-20261031.log",
      expect.anything(),
    );
  } finally {
    vi.useRealTimers();
  }
});

it("preserves an explicitly selected historical log during rollover", async () => {
  vi.mocked(api.getLogs).mockResolvedValue({
    files: ["all-20261030.log", "all-20261029.log"],
  });
  view();
  await screen.findByTestId("lazy-log");
  fireEvent.keyDown(screen.getByRole("combobox"), { key: "ArrowDown" });
  fireEvent.click(screen.getByText("all-20261029.log"));
  await waitFor(() =>
    expect(api.getLogTailJson).toHaveBeenCalledWith(
      "all-20261029.log",
      expect.anything(),
    ),
  );
  vi.mocked(api.getLogTailJson).mockClear();
  vi.mocked(api.getLogs).mockResolvedValue({
    files: ["all-20261031.log", "all-20261029.log"],
  });
  fireEvent.click(screen.getByRole("button", { name: "More" }));
  fireEvent.click(screen.getByRole("menuitem", { name: "Reload List" }));
  await waitFor(() => expect(api.getLogs).toHaveBeenCalledTimes(2));
  expect(api.getLogTailJson).not.toHaveBeenCalledWith(
    "all-20261031.log",
    expect.anything(),
  );
});

it("keeps background rollover discovery failures quiet", async () => {
  vi.useFakeTimers();
  try {
    vi.mocked(api.getLogs).mockResolvedValue({ files: ["all-20261030.log"] });
    view();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    vi.mocked(api.getLogs).mockRejectedValue(
      new Error("temporarily unavailable"),
    );
    await act(async () => {
      await vi.advanceTimersByTimeAsync(15050);
    });
    expect(api.getLogs).toHaveBeenCalledTimes(2);
    expect(
      screen.queryByText(/Failed to refresh log list/),
    ).not.toBeInTheDocument();
    expect(screen.getByTestId("lazy-log")).toHaveTextContent("ready");
  } finally {
    vi.useRealTimers();
  }
});
