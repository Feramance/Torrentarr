export function configOverviewUrl(value: unknown): URL | undefined {
  if (typeof value !== "string") return;
  const address = value.trim();
  if (!/^[a-z][a-z\d+.-]*:\/\/[^/\\?#]/i.test(address)) return;
  try {
    const url = new URL(address);
    if (url.hostname) return url;
  } catch {
    // Leave invalid addresses in the editor.
  }
}

export function clientOverviewAddress(host: unknown, port: unknown): string {
  if (typeof host !== "string" || !host.trim() || host.trim() === "CHANGE_ME")
    return "Not configured";
  const url = configOverviewUrl(
    host.includes("://") ? host : `http://${host.trim()}`,
  );
  if (!url) return "Not configured";
  const portNumber = Number(port);
  const displayPort =
    portNumber > 0 && portNumber <= 65535 && Number.isInteger(portNumber)
      ? portNumber
      : url.port;
  return displayPort ? `${url.hostname}:${displayPort}` : url.hostname;
}
