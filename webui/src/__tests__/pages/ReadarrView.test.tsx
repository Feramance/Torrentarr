import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ReadarrView } from "../../pages/ReadarrView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import { SearchProvider } from "../../context/SearchContext";
import * as api from "../../api/client";

vi.mock("../../components/arr/ArrPosterImage", () => ({
  ArrPosterImage: () => <img alt="" />,
}));
vi.mock("../../api/client", () => ({
  getConfig: vi.fn(),
  updateConfig: vi.fn(),
  getArrList: vi.fn(),
  getReadarrAuthors: vi.fn(),
  getReadarrAuthorDetail: vi.fn(),
  getReadarrOpenAuthorUrl: () => "/open",
}));
function view() {
  return render(
    <ToastProvider>
      <WebUIProvider>
        <SearchProvider>
          <ReadarrView active />
        </SearchProvider>
      </WebUIProvider>
    </ToastProvider>,
  );
}
beforeEach(() => {
  vi.resetAllMocks();
  vi.stubGlobal(
    "ResizeObserver",
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
  vi.mocked(api.getConfig).mockResolvedValue({ WebUI: { LiveArr: false } });
  vi.mocked(api.getArrList).mockResolvedValue({
    arr: [{ name: "Readarr-Books", category: "books", type: "readarr" }],
    ready: true,
  });
  vi.mocked(api.getReadarrAuthors).mockResolvedValue({
    category: "books",
    counts: { available: 1, monitored: 2, missing: 1 },
    book_total: 2,
    total: 1,
    page: 0,
    page_size: 50,
    authors: [
      {
        author: {
          id: 10,
          name: "Frank Herbert",
          monitored: true,
          booksAvailable: 1,
          booksMissing: 1,
        },
      },
    ],
  });
  vi.mocked(api.getReadarrAuthorDetail).mockResolvedValue({
    category: "books",
    counts: { available: 1, monitored: 2 },
    author: { id: 10, name: "Frank Herbert", bookCount: 2 },
    books: [
      { book: { id: 20, title: "Dune", hasFile: true, monitored: true } },
    ],
  });
});
afterEach(() => vi.unstubAllGlobals());
describe("Readarr shared catalog", () => {
  it("offers the same filters and browse layouts as the other Arrs", async () => {
    view();
    expect(
      await screen.findByRole(
        "button",
        { name: /Frank Herbert/ },
        { timeout: 5000 },
      ),
    ).toBeInTheDocument();
    expect(screen.getByLabelText("Status")).toBeInTheDocument();
    expect(screen.getByLabelText("Search Reason")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "List" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Icon" })).toBeInTheDocument();
  });
  it("opens author and book metadata in a detail dialog", async () => {
    view();
    fireEvent.click(
      await screen.findByRole(
        "button",
        { name: /Frank Herbert/ },
        { timeout: 5000 },
      ),
    );
    expect((await screen.findAllByText("Dune")).length).toBeGreaterThan(0);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(screen.queryByText("Tracks")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
  it("sends missing-only filters through the author API", async () => {
    view();
    await screen.findByRole(
      "button",
      { name: /Frank Herbert/ },
      { timeout: 5000 },
    );
    fireEvent.change(screen.getByLabelText("Status"), {
      target: { value: "missing" },
    });
    await waitFor(() =>
      expect(api.getReadarrAuthors).toHaveBeenCalledWith(
        expect.any(String),
        expect.any(Number),
        expect.any(Number),
        expect.any(String),
        expect.objectContaining({ missing: true }),
      ),
    );
  });
  it("explains an empty catalog when no instances are configured", async () => {
    vi.mocked(api.getArrList).mockResolvedValue({ arr: [], ready: true });
    view();
    expect(
      await screen.findByText(/No Readarr instances configured/),
    ).toBeInTheDocument();
  });
});
