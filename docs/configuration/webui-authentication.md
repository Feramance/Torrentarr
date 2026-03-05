# WebUI Authentication

This page describes Torrentarr's WebUI authentication options: token-only, local username/password, and OpenID Connect (OIDC). For a quick overview and links, see [WebUI Configuration](webui.md). For a step-by-step OIDC example with Authentik, see [OIDC with Authentik](webui-oidc-authentik.md).

---

## Overview

- **AuthDisabled = true (default):** No login screen. The WebUI and API are protected only by the [Token](webui.md#token) (or are public until Torrentarr has run once and auto-generated a token). Use this when you rely on the API token or a reverse proxy for access control.

- **AuthDisabled = false:** Browser users must log in or present the Bearer token. At least one of **LocalAuthEnabled** or **OIDCEnabled** should be true so the login page offers a sign-in method. The API token still works for `/api/*` (Bearer or `?token=` on GET). After a successful login (local or OIDC), a session cookie grants access to the WebUI and `/web/token` returns the API token for the frontend.

---

## Settings reference

### AuthDisabled

```toml
AuthDisabled = true
```

**Type:** Boolean
**Default:** `true`

When `true`, authentication is disabled for browser access: no login screen, and protection is via Token only (or public if Token is empty before first run). When `false`, unauthenticated browser requests to `/ui` and other protected paths are redirected to the login page unless the request includes a valid Bearer token.

---

### LocalAuthEnabled

```toml
LocalAuthEnabled = false
```

**Type:** Boolean
**Default:** `false`

When `true` (and **AuthDisabled** is false), users can log in with a username and password via the login page or `POST /web/login`. Requires **Username** and **PasswordHash** to be set. The password is never stored in config in plain form; it is set via the login page "Set password" or `POST /web/auth/set-password`.

---

### OIDCEnabled

```toml
OIDCEnabled = false
```

**Type:** Boolean
**Default:** `false`

When `true` (and **AuthDisabled** is false), the login page offers "Sign in with OIDC." Requires a **[WebUI.OIDC]** block with at least **Authority** and **ClientId** (and **ClientSecret** for confidential clients).

---

### Username

```toml
Username = ""
```

**Type:** String
**Default:** `""`

Single admin username for local auth. Used with **PasswordHash** when **LocalAuthEnabled** is true. Set via config or when calling the set-password flow.

---

### PasswordHash

```toml
PasswordHash = ""
```

**Type:** String
**Default:** `""`

BCrypt hash of the local-auth password. Never store a plain password in config.

**Setting the password:**

- **First-time:** Leave **PasswordHash** empty. Open the login page and use "Set password," or send username and password to `POST /web/auth/set-password`. Torrentarr hashes the password and writes it to config; if **AuthDisabled** was true, it will also set **AuthDisabled** = false and **LocalAuthEnabled** = true.
- **Reset:** Set the environment variable **TORRENTARR_SETUP_TOKEN** to a secret value. Call `POST /web/auth/set-password` with the same value in the `setupToken` field along with the new username and password. Torrentarr will update the hash in config.

---

### Legacy AuthMode

Older configs may use a single **AuthMode** key instead of **AuthDisabled**, **LocalAuthEnabled**, and **OIDCEnabled**. Torrentarr maps them as follows:

| AuthMode value | AuthDisabled | LocalAuthEnabled | OIDCEnabled |
|----------------|--------------|------------------|-------------|
| `Disabled` or `TokenOnly` | true | false | false |
| `Local` | false | true | false |
| `OIDC` | false | false | true |
| Other | true | false | false |

New configs should use the boolean keys. If your config already has **AuthMode**, you can leave it; the loader will derive the three booleans. When saving config, Torrentarr writes the boolean keys.

---

## [WebUI.OIDC]

When **OIDCEnabled** is true, add a **\[WebUI.OIDC]** table with your identity provider’s settings.

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| **Authority** | string | `""` | IdP issuer URL (no trailing slash). Example: `https://auth.example.com/application/o/torrentarr`. |
| **ClientId** | string | `""` | OAuth2 client id. |
| **ClientSecret** | string | `""` | OAuth2 client secret (required for confidential clients). |
| **Scopes** | string | `"openid profile"` | Space-separated scopes. |
| **CallbackPath** | string | `"/signin-oidc"` | Path Torrentarr uses for the OIDC callback. Must match the **redirect URI** configured at the IdP. |
| **RequireHttpsMetadata** | boolean | `true` | Whether to require HTTPS when fetching IdP metadata. Set `false` only for local HTTP IdPs (e.g. dev). |

When Torrentarr is behind a reverse proxy, the redirect URI the IdP must allow is the **public** URL, e.g. `https://torrentarr.example.com/signin-oidc` (same scheme, host, and path as users use to reach Torrentarr).

For a step-by-step example with one IdP, see [OIDC with Authentik](webui-oidc-authentik.md). For Authentik’s OAuth2/OIDC provider details, see the [Authentik OAuth 2.0 provider](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/) documentation.

---

## Config examples

### Auth disabled (default)

```toml
[WebUI]
AuthDisabled = true
LocalAuthEnabled = false
OIDCEnabled = false
Token = ""  # Optional; auto-generated at startup if empty
# ... Host, Port, LiveArr, etc.
```

### Local auth only

```toml
[WebUI]
AuthDisabled = false
LocalAuthEnabled = true
OIDCEnabled = false
Username = "admin"
# PasswordHash set via login page or POST /web/auth/set-password
Token = ""
# ...
```

### OIDC only

```toml
[WebUI]
AuthDisabled = false
LocalAuthEnabled = false
OIDCEnabled = true
Token = ""
# ...

[WebUI.OIDC]
Authority = "https://auth.example.com/application/o/myapp"
ClientId = "your-client-id"
ClientSecret = "your-client-secret"
Scopes = "openid profile"
CallbackPath = "/signin-oidc"
RequireHttpsMetadata = true
```

### Local + OIDC

```toml
[WebUI]
AuthDisabled = false
LocalAuthEnabled = true
OIDCEnabled = true
Username = "admin"
# PasswordHash set via set-password flow
Token = ""
# ...

[WebUI.OIDC]
Authority = "https://auth.example.com/application/o/myapp"
ClientId = "your-client-id"
ClientSecret = "your-client-secret"
Scopes = "openid profile"
CallbackPath = "/signin-oidc"
RequireHttpsMetadata = true
```

---

## Troubleshooting

### 401 Unauthorized

- **API (`/api/*`):** Ensure the **Token** is set (or was auto-generated at startup). Send it as `Authorization: Bearer <token>` or, for GET only, `?token=<token>`.
- **Browser:** If **AuthDisabled** is false, either log in (local or OIDC) or send the Bearer token. For local auth, ensure **PasswordHash** has been set (via the login page or set-password).
- Clear browser cache and cookies and check logs for auth-related errors.

### OIDC redirect or callback errors

- **Redirect URI:** The URI registered at the IdP must match exactly: same scheme, host, and path as Torrentarr’s public URL (e.g. `https://torrentarr.example.com/signin-oidc`). No trailing slash on the path unless **CallbackPath** includes it.
- **Authority:** Use the IdP issuer URL **without** a trailing slash (Torrentarr trims it).
- **HTTPS:** If **RequireHttpsMetadata** is true, the IdP’s metadata URL must be HTTPS. For local or HTTP-only IdPs, set **RequireHttpsMetadata** = false (dev only).

---

## See also

- [WebUI Configuration](webui.md) — Host, Port, Token, theme, and other WebUI settings
- [OIDC with Authentik](webui-oidc-authentik.md) — Step-by-step Authentik OIDC setup
