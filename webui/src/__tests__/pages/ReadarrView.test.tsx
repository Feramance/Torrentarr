import React from "react";
import { describe, it, expect, beforeAll, afterAll, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
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
  WebUI: { LiveArr: false, GroupSonarr: false, GroupLidarr: false },
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
});
