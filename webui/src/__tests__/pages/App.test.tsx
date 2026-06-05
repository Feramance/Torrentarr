import React from "react";
import {
  describe,
  it,
  expect,
  beforeAll,
  afterAll,
  afterEach,
  beforeEach,
  vi,
} from "vitest";
import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import { setupServer } from "msw/node";
import App from "../../App";
import { resetUrlBaseCacheForTests } from "../../api/urlBase";

const metaWithSetup = {
  current_version: "6.12.2",
  auth_required: true,
  local_auth_enabled: true,
  oidc_enabled: false,
  setup_required: true,
};

vi.mock("../../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../../api/client")>();
  return {
    ...actual,
    getMeta: vi.fn(async () => metaWithSetup),
    login: vi.fn(),
    setPassword: vi.fn(async () => ({ success: true })),
  };
});

import { AuthError, getMeta, login, setPassword } from "../../api/client";

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: "bypass" }));
afterEach(() => {
  server.resetHandlers();
  resetUrlBaseCacheForTests();
  vi.mocked(getMeta).mockResolvedValue(metaWithSetup);
  vi.mocked(login).mockReset();
  vi.mocked(setPassword).mockResolvedValue({ success: true });
});
afterAll(() => server.close());

beforeEach(() => {
  resetUrlBaseCacheForTests();
  vi.stubGlobal("location", {
    pathname: "/login",
    replace: vi.fn(),
    reload: vi.fn(),
  } as unknown as Location);
});

describe("App login setup flow", () => {
  it("shows setup token field when setup_required is true", async () => {
    render(<App />);

    expect(await screen.findByLabelText(/setup token/i)).toBeInTheDocument();
    expect(screen.getByText(/welcome to torrentarr/i)).toBeInTheDocument();
  });

  it("blocks submit when setup token is empty", async () => {
    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText(/setup token/i);
    await user.type(screen.getByLabelText(/username/i), "admin");
    await user.type(screen.getByLabelText(/new password/i), "password123");
    await user.type(screen.getByLabelText(/confirm password/i), "password123");
    const form = screen
      .getByRole("button", { name: /set password/i })
      .closest("form")!;
    fireEvent.submit(form);

    expect(await screen.findByText("Setup token is required.")).toBeInTheDocument();
    expect(setPassword).not.toHaveBeenCalled();
  });

  it("captures setupToken in set-password POST on success", async () => {
    vi.mocked(login).mockResolvedValue({ success: true });

    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText(/setup token/i);
    await user.type(screen.getByLabelText(/username/i), "admin");
    await user.type(screen.getByLabelText(/new password/i), "password123");
    await user.type(screen.getByLabelText(/confirm password/i), "password123");
    await user.type(screen.getByLabelText(/setup token/i), "my-setup-token");
    await user.click(screen.getByRole("button", { name: /set password/i }));

    await waitFor(() => {
      expect(setPassword).toHaveBeenCalledWith({
        username: "admin",
        password: "password123",
        setupToken: "my-setup-token",
      });
    });
  });

  it("switches to setup form when login returns SETUP_REQUIRED", async () => {
    vi.mocked(getMeta).mockResolvedValue({
      ...metaWithSetup,
      setup_required: false,
    });
    vi.mocked(login).mockRejectedValue(
      new AuthError("Password not set", "SETUP_REQUIRED"),
    );

    const user = userEvent.setup();
    render(<App />);

    await screen.findByLabelText(/username/i);
    await user.type(screen.getByLabelText(/username/i), "admin");
    await user.type(screen.getByLabelText(/password/i), "anything");
    await user.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByLabelText(/setup token/i)).toBeInTheDocument();
  });
});
