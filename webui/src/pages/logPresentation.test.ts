import { describe, expect, it } from "vitest";
import { colourLog, logLevel, newestAggregate } from "./logPresentation";

const ANSI_STRIP = new RegExp(String.fromCharCode(27) + "\\[[0-9;]*m", "g");

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
      expect(rendered).toContain(`\x1b[${"31"}m`);
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
      expect(rendered.replace(ANSI_STRIP, "")).toBe(
        '[2026-10-30 17:06:16][INFO][Torrentarr] Connection id "@HNPMISEGMV5K" accepted. 2 candidate(s) found at /web/logs/all-20261030.log',
      );
      expect(rendered).toContain(
        "\x1b[" + ("33") + 'm"@HNPMISEGMV5K"',
      );
      expect(rendered).toContain("\x1b[" + ("32") + "maccepted");
      expect(rendered).toContain("\x1b[" + ("36") + "m2");
      expect(rendered).toContain(
        "\x1b[" + ("34") + "m/web/logs/all-20261030.log",
      );
    },
  );

  it("colours worker log messages without altering their spacing", () => {
    const rendered = colourLog(
      '2026-10-30 17:06:16 +00:00 [WRN] Download failed for "Movie 42"',
      true,
    );
    expect(rendered.replace(ANSI_STRIP, "")).toBe(
      '2026-10-30 17:06:16 +00:00 [WRN] Download failed for "Movie 42"',
    );
    expect(rendered).toContain("\x1b[31mfailed");
    expect(rendered).toContain('\x1b[33m"Movie 42"');
  });
  it.each([true, false])(
    "emits only foreground SGR codes supported by react-logviewer (dark=%s)",
    (dark) => {
      const levels = [
        "ERROR",
        "CRITICAL",
        "WARNING",
        "NOTICE",
        "INFO",
        "DEBUG",
        "TRACE",
        "SUCCESS",
      ];
      const text = levels
        .map((level) =>
          '[2026-10-30 18:43:07][' + level.padEnd(8) +
          '][Torrentarr          ]Failed "quoted" 42 at /web/logs/file.log',
        )
        .join("\n");
      const rendered = colourLog(text, dark);
      const sgrCodes = Array.from(
        rendered.matchAll(
          new RegExp(String.fromCharCode(27) + "\\[([0-9;]+)m", "g"),
        ),
        (match) => match[1].split(";"),
      ).flat();
      const supported = new Set([
        "0", "1", "30", "31", "32", "33", "34",
        "35", "36", "37", "90",
      ]);
      expect(sgrCodes.length).toBeGreaterThan(levels.length);
      for (const code of sgrCodes) {
        expect(supported.has(code)).toBe(true);
      }
      expect(rendered).toContain("\x1b[31m[ERROR]");
      expect(rendered).toContain("\x1b[1;31m[CRITICAL]");
      expect(rendered).toContain("\x1b[33m[WARNING]");
      expect(rendered).toContain("\x1b[35m[DEBUG]");
      expect(rendered).toContain("\x1b[90m[TRACE]");
      expect(rendered.replace(ANSI_STRIP, "")).toContain(
        "[Torrentarr] Failed",
      );
    },
  );

});
