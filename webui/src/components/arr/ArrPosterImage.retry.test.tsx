import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ArrPosterImage } from "./ArrPosterImage";

const enqueueMock = vi.fn();

vi.mock("../../utils/posterLoadQueue", () => ({
  enqueuePosterReveal: (subscriber: (release: () => void) => void) => {
    enqueueMock(subscriber);
    const release = vi.fn();
    subscriber(release);
    return () => {
      /* cancel waiting */
    };
  },
}));

vi.mock("../../utils/sharedIntersectionObserver", () => ({
  observePosterVisibility: (_el: Element, onVisible: () => void) => {
    onVisible();
    return () => {
      /* unobserve */
    };
  },
}));

beforeEach(() => {
  enqueueMock.mockClear();
  Object.defineProperty(window.HTMLImageElement.prototype, "decode", {
    configurable: true,
    value: () => Promise.resolve(),
  });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  localStorage.removeItem("token");
});

function fireImgError(container: HTMLElement): void {
  const img = container.querySelector("img");
  expect(img).toBeTruthy();
  act(() => {
    img!.dispatchEvent(new Event("error"));
  });
}

function fireImgLoad(container: HTMLElement): void {
  const img = container.querySelector("img");
  expect(img).toBeTruthy();
  act(() => {
    img!.dispatchEvent(new Event("load"));
  });
}

describe("ArrPosterImage retries", () => {
  it("succeeds on the second attempt after one error", async () => {
    vi.useFakeTimers();
    const { container } = render(
      <ArrPosterImage src="/web/radarr/c/movie/1/thumbnail" alt="Movie" />,
    );

    expect(container.querySelector("img")?.getAttribute("src")).toBe(
      "/web/radarr/c/movie/1/thumbnail",
    );

    fireImgError(container);
    expect(container.querySelector("img")).toBeNull();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(150);
    });

    expect(container.querySelector("img")?.getAttribute("src")).toBe(
      "/web/radarr/c/movie/1/thumbnail?_retry=1",
    );

    fireImgLoad(container);
    await act(async () => {
      await Promise.resolve();
    });

    expect(
      container.querySelector(".arr-poster-image-wrap--ready"),
    ).toBeTruthy();
    expect(screen.queryByRole("img", { hidden: true })).toBeTruthy();
  });

  it("shows fallback after three retries (four failures)", async () => {
    vi.useFakeTimers();
    const { container } = render(
      <ArrPosterImage src="/web/radarr/c/movie/2/thumbnail" alt="Movie" />,
    );

    expect(container.querySelector("img")).toBeTruthy();

    fireImgError(container);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(150);
    });
    expect(container.querySelector("img")?.getAttribute("src")).toContain(
      "_retry=1",
    );

    fireImgError(container);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });
    expect(container.querySelector("img")?.getAttribute("src")).toContain(
      "_retry=2",
    );

    fireImgError(container);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(600);
    });
    expect(container.querySelector("img")?.getAttribute("src")).toContain(
      "_retry=3",
    );

    fireImgError(container);
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector(".arr-poster-fallback")).toBeTruthy();
  });

  it("cancels a pending retry on unmount", async () => {
    vi.useFakeTimers();
    const { container, unmount } = render(
      <ArrPosterImage src="/web/radarr/c/movie/3/thumbnail" alt="Movie" />,
    );

    expect(container.querySelector("img")).toBeTruthy();
    const enqueuesBefore = enqueueMock.mock.calls.length;

    fireImgError(container);
    unmount();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });

    expect(enqueueMock.mock.calls.length).toBe(enqueuesBefore);
  });
  it("refetches authenticated posters after an image error and revokes the bad blob", async () => {
    vi.useFakeTimers();
    localStorage.setItem("token", "test-token");
    const fetchMock = vi
      .fn()
      .mockResolvedValue({ ok: true, blob: async () => new Blob(["poster"]) });
    const revoke = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    vi.stubGlobal(
      "URL",
      class extends URL {
        static createObjectURL = vi
          .fn()
          .mockReturnValueOnce("blob:bad")
          .mockReturnValue("blob:good");
        static revokeObjectURL = revoke;
      },
    );
    const { container } = render(<ArrPosterImage src="/poster" alt="Movie" />);
    await act(async () => {
      await Promise.resolve();
    });
    expect(container.querySelector("img")?.getAttribute("src")).toBe(
      "blob:bad",
    );
    fireImgError(container);
    expect(revoke).toHaveBeenCalledWith("blob:bad");
    expect(container.querySelector("img")).toBeNull();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(150);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[1][0]).toBe("/poster?_retry=1");
    expect(fetchMock.mock.calls[1][1].headers.Authorization).toBe(
      "Bearer test-token",
    );
    expect(container.querySelector("img")?.getAttribute("src")).toBe(
      "blob:good",
    );
    fireImgLoad(container);
    await act(async () => {
      await Promise.resolve();
    });
    expect(
      container.querySelector(".arr-poster-image-wrap--ready"),
    ).toBeTruthy();
  });

  it("ignores a stale decode after the failed image has begun retrying", async () => {
    vi.useFakeTimers();
    let finishDecode!: () => void;
    Object.defineProperty(window.HTMLImageElement.prototype, "decode", {
      configurable: true,
      value: () =>
        new Promise<void>((resolve) => {
          finishDecode = resolve;
        }),
    });
    const { container } = render(<ArrPosterImage src="/poster" alt="Movie" />);
    fireImgLoad(container);
    fireImgError(container);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(150);
      finishDecode();
    });
    expect(container.querySelector(".arr-poster-image-wrap--ready")).toBeNull();
  });
});
