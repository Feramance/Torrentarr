# Security policy

## Supported versions

Security fixes are applied only to the **latest released version** of Torrentarr. Older releases are not maintained with backported security patches. If you report an issue affecting an older version, we may ask you to upgrade to the current release and confirm whether the problem still exists.

## Reporting a vulnerability

Please report security vulnerabilities responsibly so we can address them before public disclosure.

**Preferred:** Use [GitHub Security Advisories](https://github.com/Feramance/Torrentarr/security/advisories) for this repository (Report a vulnerability).

**Alternative:** Open a private discussion with maintainers if GitHub Advisories is not available, or contact the maintainers through the channels listed in the [README](README.md).

Include:

- A description of the issue and its impact
- Steps to reproduce or proof-of-concept, if safe to share
- Affected component (WebUI, Host, config, etc.) and version

We aim to acknowledge reports in a timely manner. Please avoid testing against production systems you do not own.

## Scope

This policy applies to the Torrentarr application and its official distribution artifacts (for example, release binaries published on GitHub). Third-party services you configure (qBittorrent, Radarr, Sonarr, Lidarr, reverse proxies, identity providers) follow their own security practices and are outside this project’s control.

## Deployment reminders

- When **`AuthDisabled`** is true, `/web/*` is not behind the login screen—restrict network access (firewall, bind address, or reverse proxy with authentication) if needed. **`/api/*`** still requires `WebUI.Token` (Bearer). Prefer keeping authentication enabled or using network controls when exposing Torrentarr beyond a trusted LAN.
- Prefer HTTPS in production (e.g. terminate TLS at a reverse proxy).
- See the project documentation for [WebUI authentication](docs/configuration/webui-authentication.md) and [API usage](docs/webui/api.md).
