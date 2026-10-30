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
  let level: string | undefined;
  return text
    .split("\n")
    .map((line) => {
      level = logLevel(line) ?? level;
      if (line.includes(String.fromCharCode(27))) return line;
      let colour = dark ? "37" : "30";
      if (level === "ERROR" || level === "CRITICAL")
        colour = dark ? "91" : "31";
      else if (level === "WARNING") colour = dark ? "93" : "33";
      else if (level === "DEBUG" || level === "TRACE")
        colour = dark ? "95" : "35";
      else if (level === "SUCCESS") colour = "32";
      const prefix = line.match(/^(\[[^\]]+\])(\[[^\]]+\])(\[[^\]]+\])(.*)$/);
      return prefix
        ? `\x1b[32m${prefix[1]}\x1b[0m\x1b[${colour}m${prefix[2]}\x1b[0m\x1b[${dark ? "94" : "34"}m${prefix[3]}\x1b[0m\x1b[${colour}m${prefix[4]}\x1b[0m`
        : `\x1b[${colour}m${line}\x1b[0m`;
    })
    .join("\n");
}
export function newestAggregate(files: string[]): string | undefined {
  return files
    .filter((f) => /^all-\d{8}\.log$/.test(f))
    .sort()
    .at(-1);
}
