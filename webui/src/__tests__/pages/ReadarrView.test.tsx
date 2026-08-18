import React from "react";
import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import type { ReactNode } from "react";
import { ReadarrView } from "../../pages/ReadarrView";
import { ToastProvider } from "../../context/ToastContext";
import { WebUIProvider } from "../../context/WebUIContext";
import { SearchProvider } from "../../context/SearchContext";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(async () => {
  server.resetHandlers();
  server.use(http.all("*", () => new HttpResponse(null, { status: 500 })));
  await new Promise<void>((r) => setTimeout(r, 50));
  server.resetHandlers();
});
afterAll(() => server.close());

const minimalConfig = {
  Settings: {},
  WebUI: { LiveArr: false },
};

const emptyArrList = { arr: [], ready: true };

const readarrArrList = {
  arr: [{ category: "readarr-books", name: "Readarr Books", type: "readarr" }],
  ready: true,
};

const emptyAuthorsResponse = {
  category: "readarr-books",
  authors: [],
  total: 0,
  page: 0,
  page_size: 50,
  counts: { available: 0, monitored: 0, missing: 0 },
};

function Wrapper({ children }: { children: ReactNode }) {
  return (
    <ToastProvider>
      <WebUIProvider>
        <SearchProvider>{children}</SearchProvider>
      </WebUIProvider>
    </ToastProvider>
  );
}

function renderView(active = true) {
  return render(<ReadarrView active={active} />, { wrapper: Wrapper });
}

describe("ReadarrView – card header", () => {
  it("renders the Readarr card header", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("Readarr");
  });
});

describe("ReadarrView – empty state", () => {
  it("shows 'No authors found.' when no instances are configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(emptyArrList)),
    );

    renderView();

    await screen.findByText("No authors found.");
  });

  it("shows 'No books found.' when instance returns empty author list", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(readarrArrList)),
      http.get("/web/readarr/readarr-books/authors", () =>
        HttpResponse.json(emptyAuthorsResponse),
      ),
    );

    renderView();

    await screen.findByText("No books found.", {}, { timeout: 10000 });
  }, 12000);
});

describe("ReadarrView – instance sidebar", () => {
  it("shows refresh and restart actions when one readarr instance is configured", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(readarrArrList)),
      http.get("/web/readarr/readarr-books/authors", () =>
        HttpResponse.json(emptyAuthorsResponse),
      ),
    );

    renderView();

    await screen.findByText("No books found.", {}, { timeout: 10000 });
    expect(screen.getByTitle("Restart worker")).toBeInTheDocument();
  }, 12000);

  it("renders author rows without a tracks table", async () => {
    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(readarrArrList)),
      http.get("/web/readarr/readarr-books/authors", () =>
        HttpResponse.json({
          category: "readarr-books",
          authors: [
            {
              author: {
                id: 1,
                name: "Frank Herbert",
                monitored: true,
                bookCount: 2,
                booksAvailable: 1,
                booksMonitored: 2,
              },
            },
          ],
          total: 1,
          page: 0,
          page_size: 50,
          counts: { available: 1, monitored: 2, missing: 1 },
        }),
      ),
    );

    renderView();

    expect(await screen.findByText("Frank Herbert")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: /open in readarr/i }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/tracks/i)).not.toBeInTheDocument();
  });

  it("ignores stale author-detail responses", async () => {
    let resolveFirst: ((value: unknown) => void) | undefined;
    const firstGate = new Promise((resolve) => {
      resolveFirst = resolve;
    });

    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(readarrArrList)),
      http.get("/web/readarr/readarr-books/authors", () =>
        HttpResponse.json({
          category: "readarr-books",
          authors: [
            {
              author: {
                id: 1,
                name: "Frank Herbert",
                monitored: true,
                bookCount: 1,
                booksAvailable: 1,
                booksMonitored: 1,
              },
            },
            {
              author: {
                id: 2,
                name: "Ursula K. Le Guin",
                monitored: true,
                bookCount: 1,
                booksAvailable: 1,
                booksMonitored: 1,
              },
            },
          ],
          total: 2,
          page: 0,
          page_size: 50,
          counts: { available: 2, monitored: 2, missing: 0 },
        }),
      ),
      http.get("/web/readarr/readarr-books/author/:id", async ({ params }) => {
        const id = String(params.id);
        if (id === "1") {
          await firstGate;
          return HttpResponse.json({
            category: "readarr-books",
            author: { id: 1, name: "Frank Herbert" },
            books: [{ book: { id: 10, title: "Dune", hasFile: true } }],
          });
        }
        return HttpResponse.json({
          category: "readarr-books",
          author: { id: 2, name: "Ursula K. Le Guin" },
          books: [
            { book: { id: 20, title: "A Wizard of Earthsea", hasFile: true } },
          ],
        });
      }),
    );

    renderView();
    expect(await screen.findByText("Frank Herbert")).toBeInTheDocument();

    // Same-tick clicks: B can resolve before React re-renders from A.
    fireEvent.click(screen.getByRole("button", { name: "Frank Herbert" }));
    fireEvent.click(screen.getByRole("button", { name: "Ursula K. Le Guin" }));

    expect(await screen.findByText("A Wizard of Earthsea")).toBeInTheDocument();
    resolveFirst?.(undefined);
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText("Dune")).not.toBeInTheDocument();
  });

  it("ignores a stale first expand of the same author after A then B then A", async () => {
    let author1Hits = 0;

    server.use(
      http.get("/web/config", () => HttpResponse.json(minimalConfig)),
      http.post("/web/config", () => HttpResponse.json({})),
      http.get("/web/arr", () => HttpResponse.json(readarrArrList)),
      http.get("/web/readarr/readarr-books/authors", () =>
        HttpResponse.json({
          category: "readarr-books",
          authors: [
            {
              author: {
                id: 1,
                name: "Frank Herbert",
                monitored: true,
                bookCount: 1,
                booksAvailable: 1,
                booksMonitored: 1,
              },
            },
            {
              author: {
                id: 2,
                name: "Ursula K. Le Guin",
                monitored: true,
                bookCount: 1,
                booksAvailable: 1,
                booksMonitored: 1,
              },
            },
          ],
          total: 2,
          page: 0,
          page_size: 50,
          counts: { available: 2, monitored: 2, missing: 0 },
        }),
      ),
      http.get("/web/readarr/readarr-books/author/:id", async ({ params }) => {
        const id = String(params.id);
        if (id === "1") {
          author1Hits += 1;
          if (author1Hits === 1) {
            await new Promise((r) => setTimeout(r, 150));
            return HttpResponse.json({
              category: "readarr-books",
              author: { id: 1, name: "Frank Herbert" },
              books: [{ book: { id: 10, title: "Dune", hasFile: true } }],
            });
          }
          return HttpResponse.json({
            category: "readarr-books",
            author: { id: 1, name: "Frank Herbert" },
            books: [{ book: { id: 11, title: "Dune Messiah", hasFile: true } }],
          });
        }
        return HttpResponse.json({
          category: "readarr-books",
          author: { id: 2, name: "Ursula K. Le Guin" },
          books: [
            { book: { id: 20, title: "A Wizard of Earthsea", hasFile: true } },
          ],
        });
      }),
    );

    renderView();
    expect(await screen.findByText("Frank Herbert")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Frank Herbert" }));
    fireEvent.click(screen.getByRole("button", { name: "Ursula K. Le Guin" }));
    expect(await screen.findByText("A Wizard of Earthsea")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Frank Herbert" }));
    expect(await screen.findByText("Dune Messiah")).toBeInTheDocument();
    await new Promise((r) => setTimeout(r, 200));
    expect(screen.queryByText("Dune")).not.toBeInTheDocument();
    expect(screen.getByText("Dune Messiah")).toBeInTheDocument();
  });
});
