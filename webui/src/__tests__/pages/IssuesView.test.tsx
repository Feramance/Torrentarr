import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { IssuesView } from "../../pages/IssuesView";
import { NAVIGATE_TAB_EVENT } from "../../utils/navigateTab";

const group = {
  id: 1,
  code: "profile.prepare",
  category: "automation",
  arrInstance: "Sonarr-TV",
  clientInstance: "",
  severity: "error",
  title: "Search skipped",
  impact: "Original profile preserved",
  remediation: "Check Arr profile mappings",
  target: "config",
  firstObserved: "2026-10-29T10:00:00Z",
  lastObserved: "2026-10-29T10:01:00Z",
  resolvedAt: null,
  affected: 2,
  occurrences: 3,
  examples: [
    {
      id: 1,
      code: "profile.prepare",
      firstObserved: "2026-10-29T10:00:00Z",
      lastObserved: "2026-10-29T10:01:00Z",
      subjectType: "profile",
      subjectId: "42",
      resolvedAt: null,
      title: "Profile 42 failed",
      impact: "Observed profile 7; expected profile 9",
      remediation: "Restore profile 9 before searching",
    },
  ],
};
const server = setupServer(
  http.get("/web/issues", ({ request }) =>
    HttpResponse.json({
      available: true,
      total: 1,
      page: 0,
      pageSize: 50,
      pending: [],
      items:
        new URL(request.url).searchParams.get("category") === "system"
          ? [
              {
                ...group,
                code: "worker.legacy",
                category: "system",
                title: "Worker timed out",
              },
            ]
          : [group],
    }),
  ),
  http.get("/web/issues/summary", () =>
    HttpResponse.json({
      available: true,
      activeGroups: 1,
      counts: [{ arrInstance: "Sonarr-TV", clientInstance: "" }],
      pending: [],
    }),
  ),
);
beforeAll(() => server.listen({ onUnhandledRequest: "error" }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

describe("Issues view", () => {
  it("groups issues, expands actionable details and switches inner tabs", async () => {
    const user = userEvent.setup();
    render(<IssuesView />);
    await screen.findByText("Temporary quality profile could not be prepared");
    expect(screen.getByText(/2 items · Active/)).toBeInTheDocument();
    await user.click(
      screen.getByText("Temporary quality profile could not be prepared"),
    );
    expect(
      screen.getByText(/Verify the configured original and temporary profiles/),
    ).toBeVisible();
    expect(
      screen.getByText(/Observed profile 7; expected profile 9/),
    ).not.toBeVisible();
    await user.click(screen.getByText("Technical details"));
    expect(
      screen.getByText(/Observed profile 7; expected profile 9/),
    ).toBeVisible();
    expect(
      screen.getByRole("button", { name: "Check quality profile mappings" }),
    ).toBeVisible();
    await user.click(screen.getByRole("tab", { name: "System & services" }));
    await screen.findByText("Worker timed out");
    expect(
      screen.getByRole("tab", { name: "System & services" }),
    ).toHaveAttribute("aria-selected", "true");
  });
  it("shows torrent names and categories with hash fallback for unavailable metadata", async () => {
    server.use(
      http.get("/web/issues", () =>
        HttpResponse.json({
          available: true,
          total: 1,
          page: 0,
          pageSize: 50,
          pending: [],
          items: [
            {
              ...group,
              code: "import.ownership",
              examples: [
                {
                  ...group.examples[0],
                  subjectType: "torrent",
                  subjectId: "deadbeef",
                  subjectName: "Anime S02E04",
                  subjectCategory: "sonarr-anime",
                },
                {
                  ...group.examples[0],
                  id: 2,
                  subjectType: "torrent",
                  subjectId: "missinghash",
                },
                {
                  ...group.examples[0],
                  id: 3,
                  subjectType: "torrent",
                  subjectId: "uncategorizedhash",
                  subjectName: "Movie",
                  subjectCategory: "",
                },
              ],
            },
          ],
        }),
      ),
    );
    render(<IssuesView />);
    await userEvent.click(
      await screen.findByText("Import owner could not be determined"),
    );
    expect(screen.getByText("Anime S02E04")).toBeVisible();
    expect(
      screen.getByText("missinghash", { selector: "strong" }),
    ).toBeVisible();
    expect(screen.getByText("Movie")).toBeVisible();
    for (const id of screen.getAllByText(/deadbeef|uncategorizedhash/))
      expect(id).not.toBeVisible();
  });
  it("does not claim healthy when history is unavailable", async () => {
    server.use(
      http.get("/web/issues", () =>
        HttpResponse.json({
          available: false,
          items: [],
          total: null,
          page: 0,
          pageSize: 50,
          pending: [],
        }),
      ),
    );
    render(<IssuesView />);
    await screen.findByRole("alert");
    expect(screen.getByText(/Issue history unavailable/)).toBeInTheDocument();
    expect(
      screen.queryByText("No active issues match these filters."),
    ).not.toBeInTheDocument();
  });
  it("does not fetch while inactive", () => {
    render(<IssuesView active={false} />);
    expect(screen.getByRole("status")).toHaveTextContent("Loading issues");
  });
});

it("opens scoped system issues and supports arrow-key tabs", async () => {
  const user = userEvent.setup();
  render(
    <IssuesView selection={{ instance: "Sonarr-TV", category: "system" }} />,
  );
  await screen.findByText("Worker timed out");
  expect(screen.getByRole("combobox", { name: "Instance" })).toHaveValue(
    "Sonarr-TV",
  );
  await user.click(screen.getByRole("tab", { name: "System & services" }));
  await user.keyboard("{ArrowLeft}");
  await screen.findByText("Temporary quality profile could not be prepared");
  expect(
    screen.getByRole("tab", { name: "Automation & safety" }),
  ).toHaveFocus();
  expect(screen.getByRole("combobox", { name: "Instance" })).toHaveValue(
    "Sonarr-TV",
  );
});

it("opens recovered items only when available and keeps the card open when an item recovers", async () => {
  let recovered = false;
  const second = { ...group.examples[0], id: 2, subjectId: "43" };
  server.use(
    http.get("/web/issues", () =>
      HttpResponse.json({
        available: true,
        total: 1,
        page: 0,
        pageSize: 50,
        pending: [],
        items: [
          {
            ...group,
            affected: recovered ? 1 : 2,
            hasResolvedItems: recovered,
          },
        ],
      }),
    ),
    http.get(/\/web\/issues\/\d+$/, ({ request }) => {
      const state = new URL(request.url).searchParams.get("state");
      const items =
        state === "resolved"
          ? [
              {
                ...second,
                resolvedAt: "2026-10-29T11:00:00Z",
                resolution: "Verified recovery",
              },
            ]
          : recovered
            ? group.examples
            : [...group.examples, second];
      return HttpResponse.json({
        available: true,
        total: items.length,
        page: 0,
        pageSize: 50,
        items,
      });
    }),
  );
  const user = userEvent.setup();
  render(<IssuesView />);
  await user.click(
    await screen.findByText("Temporary quality profile could not be prepared"),
  );
  expect(
    screen.queryByRole("button", { name: "View resolved items" }),
  ).toBeNull();
  await user.click(
    screen.getByRole("button", { name: "Show all affected items" }),
  );
  expect(await screen.findByText("43")).toBeVisible();
  recovered = true;
  await user.click(
    screen.getByRole("button", { name: "Refresh", exact: true }),
  );
  await user.click(
    await screen.findByRole("button", { name: "View resolved items" }),
  );
  expect(await screen.findByText(/Verified recovery/)).toBeVisible();
  await user.click(
    screen.getByRole("button", { name: "Back to affected items" }),
  );
  expect(screen.queryByText("43")).toBeNull();
  expect(screen.getByText("42")).toBeVisible();
});

it("retains the current item page on failure and retries a page that shrank", async () => {
  let fail = true;
  let shrank = false;
  const requests: number[] = [];
  server.use(
    http.get(/\/web\/issues\/\d+$/, ({ request }) => {
      const page = Number(new URL(request.url).searchParams.get("page"));
      requests.push(page);
      if (page === 1 && fail) return new HttpResponse(null, { status: 503 });
      return HttpResponse.json({
        available: true,
        total: shrank ? 1 : 2,
        page,
        pageSize: 1,
        items: page === 1 ? [] : group.examples,
      });
    }),
  );
  const user = userEvent.setup();
  render(<IssuesView />);
  await user.click(
    await screen.findByText("Temporary quality profile could not be prepared"),
  );
  await user.click(
    screen.getByRole("button", { name: "Show all affected items" }),
  );
  await user.click(await screen.findByRole("button", { name: "Next page" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Issue items could not be loaded",
  );
  expect(screen.getByText("42")).toBeVisible();
  fail = false;
  shrank = true;
  await user.click(screen.getByRole("button", { name: "Retry" }));
  await screen.findByText("42");
  expect(screen.queryByRole("button", { name: "Previous page" })).toBeNull();
  await waitFor(() => expect(requests).toEqual([0, 1, 1, 0]));
});

it("shows a missing-file warning with grouped examples and inspectable paths", async () => {
  const preview = {
    ...group,
    code: "maintenance.preview.missing-file",
    arrInstance: "",
    clientInstance: "qBit-General",
    target: "maintenance",
    severity: "warning",
    affected: 1,
  };
  server.use(
    http.get("/web/issues", () =>
      HttpResponse.json({
        available: true,
        total: 1,
        page: 0,
        pageSize: 50,
        pending: [],
        items: [
          {
            ...preview,
            examples: [
              {
                ...preview,
                subjectType: "preview",
                subjectId: "OrphanScan",
                evidence: {
                  affectedFiles: 7,
                  affectedTorrents: 1,
                  operations: ["OrphanScan"],
                  files: ["episode-1.mkv", "episode-2.mkv"].map((name) => ({
                    torrentHash: "123",
                    torrentName: "Season one",
                    clientPath: `/client/${name}`,
                    localPath: `/local/${name}`,
                  })),
                },
              },
            ],
          },
        ],
      }),
    ),
  );
  const user = userEvent.setup();
  render(<IssuesView />);
  await user.click(
    await screen.findByText(
      "Torrentarr couldn’t find files at their mapped paths",
    ),
  );
  expect(screen.getByText("7 files · 1 torrent")).toBeVisible();
  expect(screen.getByText("Orphan Scan")).toBeVisible();
  expect(screen.getByText("Season one")).toBeVisible();
  expect(screen.getByText(/selected and fully downloaded/)).toBeVisible();
  expect(
    screen.queryByRole("button", { name: "Show all affected items" }),
  ).toBeNull();
  expect(
    screen.queryByRole("button", { name: "View resolved items" }),
  ).toBeNull();
  await user.click(screen.getByText("episode-1.mkv"));
  expect(screen.getByText("/client/episode-1.mkv")).toBeVisible();
  expect(screen.getByText("/local/episode-1.mkv")).toBeVisible();
});

it("removes a recovered group on automatic refresh", async () => {
  let recovered = false;
  server.use(
    http.get("/web/issues", () =>
      HttpResponse.json({
        available: true,
        total: recovered ? 0 : 1,
        page: 0,
        pageSize: 50,
        pending: [],
        items: recovered ? [] : [group],
      }),
    ),
  );
  render(<IssuesView />);
  await screen.findByText("Temporary quality profile could not be prepared");
  recovered = true;
  await screen.findByText("No active issues match these filters.", undefined, {
    timeout: 8000,
  });
  expect(
    screen.queryByText("Temporary quality profile could not be prepared"),
  ).not.toBeInTheDocument();
}, 10000);

it("links grouped storage issues to each affected monitor", async () => {
  const reserve = {
    ...group,
    code: "storage.reserve",
    arrInstance: "",
    clientInstance: "qBit-General",
    title: "Download storage reserve needs attention",
  };
  server.use(
    http.get("/web/issues", () =>
      HttpResponse.json({
        available: true,
        total: 1,
        page: 0,
        pageSize: 50,
        pending: [],
        items: [
          {
            ...reserve,
            examples: ["/first", "/second"].map((subjectId, index) => ({
              ...reserve,
              id: index + 1,
              subjectType: "storage",
              subjectId,
            })),
          },
        ],
      }),
    ),
  );
  const user = userEvent.setup();
  const destinations: unknown[] = [];
  const capture = (event: Event) =>
    destinations.push((event as CustomEvent).detail);
  window.addEventListener(NAVIGATE_TAB_EVENT, capture);
  try {
    render(<IssuesView />);
    await user.click(
      await screen.findByText(reserve.title, { selector: "summary strong" }),
    );
    await user.click(
      screen.getAllByRole("button", { name: "Edit free-space monitor" })[1]!,
    );
    expect(destinations).toEqual([
      {
        tab: "config",
        configDestination: {
          arr: undefined,
          client: "qBit-General",
          field: "FreeSpace",
          monitorPath: "/second",
        },
        preserveConfigDraft: true,
      },
    ]);
  } finally {
    window.removeEventListener(NAVIGATE_TAB_EVENT, capture);
  }
});

it.each([false, true])(
  "lists torrent metadata and blockers for a removal report (structured: %s)",
  async (structured) => {
    const blockers = [
      {
        code: "torrent.protected-tag",
        reason: "Blocking tag 'free_space_paused' is present",
        remediation: "Resolve the free-space policy.",
      },
      {
        code: "stalled.grace",
        reason: "StalledDelay=1440min; idle=696s; required=86400s",
        remediation:
          "Wait for the stalled grace; a negative delay disables removal.",
      },
    ];
    const issue = {
      ...group.examples[0],
      code: "torrent.removal.SeedingLimit",
      subjectType: "torrent",
      subjectId: "deadbeef",
      subjectName: "Season pack",
      clientInstance: "qBit-General",
      impact:
        "Torrent=Season pack; hash=deadbeef; client=qBit-General; category=sonarranime; intent=SeedingLimit. " +
        blockers
          .map(
            ({ code, reason, remediation }) =>
              `${code}: ${reason} Next: ${remediation}`,
          )
          .join("; "),
      evidence: structured
        ? {
            removal: {
              name: "Season pack",
              category: "sonarranime",
              intent: "SeedingLimit",
              blockers,
            },
          }
        : undefined,
    };
    server.use(
      http.get("/web/issues", () =>
        HttpResponse.json({
          available: true,
          total: 1,
          page: 0,
          pageSize: 50,
          pending: [],
          items: [
            { ...group, code: issue.code, affected: 1, examples: [issue] },
          ],
        }),
      ),
    );
    const user = userEvent.setup();
    render(<IssuesView />);
    await user.click(await screen.findByText("Torrent removal was blocked"));
    await user.click(screen.getByText("Technical details"));
    expect(screen.getByText("Removal rule").closest("li")).toHaveTextContent(
      "Seeding Limit",
    );
    expect(screen.getByText("Hash").closest("li")).toHaveTextContent(
      "deadbeef",
    );
    for (const blocker of blockers) {
      const bullet = screen
        .getByText(blocker.code, { selector: "li strong" })
        .closest("li");
      expect(bullet).toHaveTextContent(blocker.reason);
      expect(bullet).toHaveTextContent(blocker.remediation);
    }
  },
);
