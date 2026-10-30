import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterAll, afterEach, beforeAll, describe, expect, it } from "vitest";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import { IssuesView } from "../../pages/IssuesView";

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
    { id: 1, subjectType: "profile", subjectId: "42", resolvedAt: null },
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
          ? [{ ...group, category: "system", title: "Worker timed out" }]
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
    await screen.findByText("Search skipped");
    expect(screen.getByText(/2 affected/)).toBeInTheDocument();
    await user.click(screen.getByText("Search skipped"));
    expect(screen.getByText("Check Arr profile mappings")).toBeVisible();
    expect(screen.getByRole("button", { name: "Open config" })).toBeVisible();
    await user.click(screen.getByRole("tab", { name: "System & services" }));
    await screen.findByText("Worker timed out");
    expect(
      screen.getByRole("tab", { name: "System & services" }),
    ).toHaveAttribute("aria-selected", "true");
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
  await screen.findByText("Search skipped");
  expect(
    screen.getByRole("tab", { name: "Automation & safety" }),
  ).toHaveFocus();
  expect(screen.getByRole("combobox", { name: "Instance" })).toHaveValue(
    "Sonarr-TV",
  );
});

it("keeps loaded subject history visible when the grouped list refreshes", async () => {
  let loads = 0;
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
            title: ++loads > 1 ? "Search skipped (refreshed)" : group.title,
            id: loads > 1 ? 2 : 1,
          },
        ],
      }),
    ),
    http.get(/\/web\/issues\/\d+$/, () =>
      HttpResponse.json({
        available: true,
        total: 2,
        page: 0,
        pageSize: 50,
        items: [
          ...group.examples,
          {
            id: 2,
            subjectType: "profile",
            subjectId: "43",
            resolvedAt: "2026-10-29T10:02:00Z",
            resolution: "Verified recovery",
          },
        ],
      }),
    ),
  );
  const user = userEvent.setup();
  render(<IssuesView />);
  await user.click(await screen.findByText("Search skipped"));
  await user.click(
    screen.getByRole("button", { name: "View affected subjects and history" }),
  );
  await screen.findByText(/43.*Verified recovery/);
  await user.click(
    screen.getByRole("button", { name: "Refresh", exact: true }),
  );
  await screen.findByText("Search skipped (refreshed)");
  expect(screen.getByText(/43.*Verified recovery/)).toBeVisible();
});

it("refreshes loaded subjects after recovery without collapsing the row", async () => {
  let recovered = false;
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
            occurrences: recovered ? 4 : 3,
          },
        ],
      }),
    ),
    http.get(/\/web\/issues\/\d+$/, () =>
      HttpResponse.json({
        available: true,
        total: 2,
        page: 0,
        pageSize: 50,
        items: [
          group.examples[0],
          {
            id: 2,
            subjectType: "profile",
            subjectId: "43",
            resolvedAt: recovered ? "2026-10-29T11:00:00Z" : null,
            resolution: recovered ? "Verified recovery" : null,
          },
        ],
      }),
    ),
  );
  const user = userEvent.setup();
  render(<IssuesView />);
  await user.click(await screen.findByText("Search skipped"));
  await user.click(
    screen.getByRole("button", { name: "View affected subjects and history" }),
  );
  expect(await screen.findByText(/43.*Active/)).toBeVisible();
  recovered = true;
  await user.click(
    screen.getByRole("button", { name: "Refresh", exact: true }),
  );
  expect(await screen.findByText(/43.*Verified recovery/)).toBeVisible();
  expect(screen.getByText(/1 affected/)).toBeVisible();
});

it("shows stale detail errors and retries them on the next group refresh", async () => {
  let fail = false;
  let revision = 3;
  server.use(
    http.get("/web/issues", () =>
      HttpResponse.json({
        available: true,
        total: 1,
        page: 0,
        pageSize: 50,
        pending: [],
        items: [{ ...group, occurrences: revision }],
      }),
    ),
    http.get(/\/web\/issues\/\d+$/, () =>
      fail
        ? new HttpResponse(null, { status: 503 })
        : HttpResponse.json({
            available: true,
            total: 1,
            page: 0,
            pageSize: 50,
            items: group.examples,
          }),
    ),
  );
  const user = userEvent.setup();
  render(<IssuesView />);
  await user.click(await screen.findByText("Search skipped"));
  await user.click(
    screen.getByRole("button", { name: "View affected subjects and history" }),
  );
  await screen.findByText(/42.*Active/);
  fail = true;
  revision++;
  await user.click(
    screen.getByRole("button", { name: "Refresh", exact: true }),
  );
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Affected subjects could not be loaded",
  );
  expect(screen.getByText(/42.*Active/)).toBeVisible();
  fail = false;
  revision++;
  await user.click(
    screen.getByRole("button", { name: "Refresh", exact: true }),
  );
  await screen.findByText(/42.*Active/);
});
