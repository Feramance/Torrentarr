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
      ).toBe(
        "[2026-10-30 12:00:00][ERROR][Torrentarr] failed\nSystem.IO.IOException: denied",
      );
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
  it.each([true, false])(
    "highlights semantic message tokens while keeping compact source names (dark=%s)",
    (dark) => {
      const plain =
        '[2026-10-30 17:06:16][INFO   ][Torrentarr          ]Connection id "@HNPMISEGMV5K" accepted. 2 candidate(s) found at /web/logs/all-20261030.log';
      const rendered = colourLog(plain, dark);
      expect(rendered.replace(/\x1b\[[0-9;]*m/g, "")).toBe(
        '[2026-10-30 17:06:16][INFO][Torrentarr] Connection id "@HNPMISEGMV5K" accepted. 2 candidate(s) found at /web/logs/all-20261030.log',
      );
      expect(rendered).toContain(
        "\x1b[" + (dark ? "93" : "33") + 'm"@HNPMISEGMV5K"',
      );
      expect(rendered).toContain(
        "\x1b[" + (dark ? "92" : "32") + "maccepted",
      );
      expect(rendered).toContain("\x1b[" + (dark ? "96" : "36") + "m2");
      expect(rendered).toContain(
        "\x1b[" + (dark ? "94" : "34") + "m/web/logs/all-20261030.log",
      );
    },
  );

  it("colours worker log messages without altering their spacing", () => {
    const rendered = colourLog(
      '2026-10-30 17:06:16 +00:00 [WRN] Download failed for "Movie 42"',
      true,
    );
    expect(rendered.replace(/\x1b\[[0-9;]*m/g, "")).toBe(
      '2026-10-30 17:06:16 +00:00 [WRN] Download failed for "Movie 42"',
    );
    expect(rendered).toContain("\x1b[91mfailed");
    expect(rendered).toContain('\x1b[93m"Movie 42"');
  });

});
