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
export function colourLog(text: string, dark: boolean): string {
  let previousLevel: string | undefined;
  const esc = "\x1b[";
  const paint = (value: string, code: string) => esc + code + "m" + value + esc + "0m";
  const severity = (level: string | undefined): string => {
    switch (level) {
      case "ERROR": return dark ? "91" : "31";
      case "CRITICAL": return dark ? "1;91" : "1;31";
      case "WARNING": return dark ? "93" : "33";
      case "NOTICE": return dark ? "96" : "36";
      case "DEBUG": return dark ? "95" : "35";
      case "TRACE": return "90";
      case "SUCCESS": return dark ? "92" : "32";
      default: return dark ? "92" : "32";
    }
  };
  return text.split("\n").map((raw) => {
    // Existing ANSI colours must not prevent consistent browser highlighting.
    const line = raw.replace(ANSI, "");
    const level = logLevel(line);
    if (level) previousLevel = level;
    const colour = severity(level ?? previousLevel);
    const neutral = dark ? "37" : "30";
    const sourceColour = dark ? "94" : "34";
    const timestampColour = dark ? "92" : "32";
    // Host logs: padded level and logger names are display formatting only.
    const host = line.match(/^(\[[^\]]+\])(\[([^\]]+)\])(\[([^\]]+)\])([\s\S]*)$/);
    if (host && logLevel(host[2])) {
      const message = host[6].trimStart();
      return paint(host[1], timestampColour) +
        paint("[" + host[3].trim() + "]", colour) +
        paint("[" + host[5].trim() + "]", sourceColour) +
        (message ? " " + paint(message, neutral) : "");
    }
    // Worker logs: retain date and message while highlighting the level.
    const worker = line.match(/^(\d{4}-\d\d-\d\d[ T][^\[]+)(\[([^\]]+)\])(\s*)([\s\S]*)$/);
    if (worker && logLevel(worker[2])) {
      return paint(worker[1], timestampColour) +
        paint("[" + worker[3].trim() + "]", colour) +
        worker[4] + paint(worker[5], neutral);
    }
    return paint(line, neutral);
  }).join("\n");
}
export function newestAggregate(files: string[]): string | undefined {
  return files
    .filter((f) => /^all-\d{8}\.log$/.test(f))
    .sort()
    .at(-1);
}
