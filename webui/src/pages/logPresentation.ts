const ANSI = new RegExp(`${String.fromCharCode(27)}\\[[0-9;]*m`, "g");
const ALIASES: Record<string, string> = {
  INF: "INFO",
  WRN: "WARNING",
  WARN: "WARNING",
  ERR: "ERROR",
  DBG: "DEBUG",
  VRB: "TRACE",
  FTL: "CRITICAL",
  FATAL: "CRITICAL",
  VERBOSE: "TRACE",
};
export function logLevel(line: string): string | undefined {
  const match = line
    .replace(ANSI, "")
    .match(
      /\[(?:[^\]]*?\s)?(TRACE|DEBUG|INFO|NOTICE|WARNING|WARN|ERROR|CRITICAL|FATAL|SUCCESS|INF|WRN|ERR|DBG|VRB|FTL)\s*\]|\b(TRACE|DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL|SUCCESS)\s*:/i,
    );
  const level = (match?.[1] ?? match?.[2])?.toUpperCase();
  return level ? ALIASES[level] ?? level : undefined;
}
// @melloware/react-logviewer recognises only SGR foreground 30-37 and 90.
// Bright foreground codes 91-97 are silently ignored by its ansiparse parser.
// Keep all display-only colours within the supported range.
// Tokenise messages once, prioritising full quoted values, addresses and paths
// over individual words to avoid fragmented ANSI-coloured output.
const MESSAGE_TOKENS =
  /"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|https?:\/\/[^\s"'<>]+|(?:\/[\w.%{}@+~?=&#:-]+){2,}|\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b|\b(?:connection refused|access denied|not found|timed out|error|failed|failure|denied|rejected|invalid|exception|unauthenticated|timeout|disconnected|disconnecting|stopped|unavailable|missing|aborted|cancelled|canceled|warning|authenticated|connected|accepted|started|success|succeeded|completed|allowed|healthy|enabled|matched|online|active|passed|ready|running|finished|imported|removed|downloaded|updated|saved)\b|\b[a-z_][\w.-]*(?=\s*[=:])|\b\d+(?:\.\d+)?(?:\s?(?:ms|KiB|MiB|GiB|KB|MB|GB|TB|s|m|h))?%?/gi;
const FAILURE =
  /^(?:connection refused|access denied|not found|timed out|error|failed|failure|denied|rejected|invalid|exception|unauthenticated|timeout|disconnected|disconnecting|stopped|unavailable|missing|aborted|cancelled|canceled|warning)$/i;
const SUCCESS =
  /^(?:authenticated|connected|accepted|started|success|succeeded|completed|allowed|healthy|enabled|matched|online|active|passed|ready|running|finished|imported|removed|downloaded|updated|saved)$/i;

function colourMessage(message: string, baseColour: string): string {
  const highlighted = message.replace(MESSAGE_TOKENS, (token) => {
    let colour: string;
    if (token.startsWith('"') || token.startsWith("'")) colour = "33";
    else if (FAILURE.test(token)) colour = "31";
    else if (SUCCESS.test(token)) colour = "32";
    else if (/^\d/.test(token) || /^[0-9a-f]{8}-/i.test(token)) colour = "36";
    else colour = "34";
    return "\x1b[" + colour + "m" + token + "\x1b[" + baseColour + "m";
  });
  return "\x1b[" + baseColour + "m" + highlighted + "\x1b[0m";
}

export function colourLog(text: string, dark: boolean): string {
  let previousLevel: string | undefined;
  const esc = "\x1b[";
  const paint = (value: string, code: string) =>
    esc + code + "m" + value + esc + "0m";
  const severity = (level: string | undefined): string => {
    switch (level) {
      case "ERROR":
        return "31";
      case "CRITICAL":
        return "1;31";
      case "WARNING":
        return "33";
      case "NOTICE":
        return "36";
      case "DEBUG":
        return "35";
      case "TRACE":
        return "90";
      case "SUCCESS":
        return "32";
      default:
        return "32";
    }
  };
  return text
    .split("\n")
    .map((raw) => {
      // Keep colours already emitted by the logger intact.
      if (raw.includes("\x1b")) return raw;
      const line = raw;
      const level = logLevel(line);
      if (level) previousLevel = level;
      const colour = severity(level ?? previousLevel);
      const neutral = dark ? "37" : "30";
      const sourceColour = "34";
      const timestampColour = "32";
      // Host logs: padded level and logger names are display formatting only.
      const host = line.match(
        /^(\[[^\]]+\])(\[([^\]]+)\])(\[([^\]]+)\])([\s\S]*)$/,
      );
      if (host && logLevel(host[2])) {
        const message = host[6].trimStart();
        return (
          paint(host[1], timestampColour) +
          paint("[" + host[3].trim() + "]", colour) +
          paint("[" + host[5].trim() + "]", sourceColour) +
          (message ? " " + colourMessage(message, neutral) : "")
        );
      }
      // Worker logs: retain date and message while highlighting the level.
      const worker = line.match(
        /^(\d{4}-\d\d-\d\d[ T][^[]+)(\[([^\]]+)\])(\s*)([\s\S]*)$/,
      );
      if (worker && logLevel(worker[2])) {
        return (
          paint(worker[1], timestampColour) +
          paint("[" + worker[3].trim() + "]", colour) +
          worker[4] +
          colourMessage(worker[5], neutral)
        );
      }
      return colourMessage(line, neutral);
    })
    .join("\n");
}
export function newestAggregate(files: string[]): string | undefined {
  return files
    .filter((f) => /^all-\d{8}\.log$/.test(f))
    .sort()
    .at(-1);
}
