import { afterEach, describe, expect, it, vi } from "vitest";
import {
  applyTheme,
  isDarkTheme,
  resolveTheme,
  themeFromValue,
} from "../utils/theme";

afterEach(() => {
  vi.unstubAllGlobals();
  delete document.documentElement.dataset.palette;
});

describe("theme model", () => {
  it("parses persisted values and defaults unknown values to system", () => {
    expect(themeFromValue("SYSTEM")).toBe("system");
    expect(themeFromValue("Light")).toBe("light");
    expect(themeFromValue("dark")).toBe("dark");
    expect(themeFromValue("Midnight")).toBe("midnight");
    expect(themeFromValue(null)).toBe("system");
    expect(themeFromValue("sepia")).toBe("system");
  });

  it("resolves the system theme from the media query", () => {
    vi.stubGlobal(
      "matchMedia",
      vi.fn(() => ({
        matches: false,
        media: "(prefers-color-scheme: dark)",
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    );

    expect(resolveTheme("system")).toBe("light");
    expect(isDarkTheme("system")).toBe(false);
    expect(isDarkTheme("midnight")).toBe(true);
  });

  it("applies midnight as dark with the midnight palette", () => {
    applyTheme("midnight");
    expect(document.documentElement).toHaveAttribute("data-theme", "dark");
    expect(document.documentElement.dataset.palette).toBe("midnight");

    applyTheme("light");
    expect(document.documentElement).toHaveAttribute("data-theme", "light");
    expect(document.documentElement.dataset.palette).toBeUndefined();
  });
});
