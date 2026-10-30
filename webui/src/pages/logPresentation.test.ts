import { describe, expect, it } from "vitest";
import { colourLog, logLevel, newestAggregate } from "./logPresentation";

describe("log presentation", () => {
  it("prefers the newest aggregate over stale qBitrr and service logs", () => {
    expect(
      newestAggregate([
        "All.log",
        "worker-Sonarr-20261031.log",
        "all-20261030.log",
        "all-20261029.log",
      ]),
    ).toBe("all-20261030.log");
  });
  it.each([true, false])(
    "colours plain logs and exception continuations (dark=%s)",
    (dark) => {
      const plain =
        "[2026-10-30 12:00:00][ERROR   ][Torrentarr          ]failed\nSystem.IO.IOException: denied";
      const rendered = colourLog(plain, dark);
      expect(rendered).toContain(`\x1b[${dark ? "91" : "31"}m`);
      expect(
        rendered.replace(
          new RegExp(`${String.fromCharCode(27)}\\[[0-9;]*m`, "g"),
          "",
        ),
      ).toBe(plain);
      expect(logLevel(rendered.split("\n")[0])).toBe("ERROR");
    },
  );
  it("preserves ANSI lines and recognizes both service formats", () => {
    const old = "\x1b[31mWARNING : qBitrr: blocked\x1b[0m";
    expect(colourLog(old, true)).toBe(old);
    expect(logLevel(old)).toBe("WARNING");
    expect(logLevel("2026-10-30 12:00 [WRN] failed")).toBe("WARNING");
    expect(logLevel("[now][FATAL   ][Torrentarr] failed")).toBe("CRITICAL");
  });
});
