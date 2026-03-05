# OIDC with Authentik

This guide walks through configuring [Authentik](https://goauthentik.io/) as the OpenID Connect (OIDC) provider for Torrentarr. For general OAuth2/OIDC concepts and Authentik's endpoints, see the [Authentik OAuth 2.0 provider](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/) documentation. For Torrentarr's OIDC config options, see [WebUI Authentication](webui-authentication.md).

---

## Prerequisites

- Authentik installed and reachable (e.g. `https://auth.example.com`).
- Torrentarr reachable over HTTPS at a stable public URL (e.g. `https://torrentarr.example.com`), or plan to use that URL once the reverse proxy is in place.

---

## Step 1: Create provider and application in Authentik

1. Log in to the Authentik admin interface.
2. Go to **Applications** → **Applications**.
3. Click **Create with provider** so that the application and provider are created together (recommended). See [Create an OAuth2 provider](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/create-oauth2-provider/).
4. Select **OAuth2/OIDC** as the provider type and proceed.

---

## Step 2: Configure the OAuth2/OpenID provider

On the **Configure OAuth2/OpenId Provider** page:

- **Redirect URIs:** Add your Torrentarr callback URL exactly as users will reach it, e.g. `https://torrentarr.example.com/signin-oidc`. Use **strict** matching. This must match Torrentarr's public URL and the default `CallbackPath` (`/signin-oidc`).
- **Client type:** **Confidential** (Torrentarr sends the client secret when exchanging the authorization code for tokens).
- **Scopes:** Include at least `openid` and `profile` (or match what you will set in Torrentarr's **Scopes**).
- **Provider slug:** Choose a slug that is **not** a reserved name (e.g. avoid `authorize`, `token`, `userinfo`) to prevent URL clashes with Authentik's endpoints. For example use `torrentarr` or `torrentarr-webui`. See [Authentik issue #7419](https://github.com/goauthentik/authentik/issues/7419) for details.

Optional: If you need refresh tokens, configure the `offline_access` scope mapping in Authentik. Torrentarr's authorization code flow works with the access token only, so this is optional.

---

## Step 3: Create the application and note credentials

Complete the wizard to create the application and link it to the provider. From the provider (or application) details, note:

- **Client ID**
- **Client Secret**

You will use these in Torrentarr's `[WebUI.OIDC]` block.

---

## Step 4: Authority URL for Torrentarr

Authentik's issuer (Authority) URL is:

```text
https://<authentik-host>/application/o/<provider-slug>
```

Use **no trailing slash**. Examples:

- Authentik at `https://auth.example.com`, provider slug `torrentarr` → `https://auth.example.com/application/o/torrentarr`
- Authentik at `https://idp.home.local`, provider slug `torrentarr-webui` → `https://idp.home.local/application/o/torrentarr-webui`

The OIDC discovery document is at `https://<authentik-host>/application/o/<provider-slug>/.well-known/openid-configuration`; Torrentarr will use the Authority to discover endpoints.

---

## Step 5: Torrentarr config.toml

Add or update the WebUI and OIDC settings. Use the same Authority (no trailing slash), Client ID, Client Secret, and callback path as in Authentik.

```toml
[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = ""  # Optional; recommended for API access
AuthDisabled = false
LocalAuthEnabled = false
OIDCEnabled = true
LiveArr = true
GroupSonarr = true
GroupLidarr = true
Theme = "Dark"
ViewDensity = "Comfortable"

[WebUI.OIDC]
Authority = "https://auth.example.com/application/o/torrentarr"
ClientId = "your-client-id-from-authentik"
ClientSecret = "your-client-secret-from-authentik"
Scopes = "openid profile"
CallbackPath = "/signin-oidc"
RequireHttpsMetadata = true
```

Replace:

- `https://auth.example.com/application/o/torrentarr` with your Authentik host and provider slug.
- `your-client-id-from-authentik` and `your-client-secret-from-authentik` with the values from Step 3.

If Torrentarr is only reachable via HTTPS at `https://torrentarr.example.com`, ensure the redirect URI in Authentik is exactly `https://torrentarr.example.com/signin-oidc`.

---

## Step 6: Reverse proxy (if used)

If Torrentarr is behind a reverse proxy:

- Serve Torrentarr over HTTPS at the **same** public URL you configured as the redirect URI in Authentik (e.g. `https://torrentarr.example.com`).
- Ensure the path `/signin-oidc` is proxied to Torrentarr so the OIDC callback reaches the app.
- Preserve the request path and host/forwarded headers so Torrentarr and the IdP see the same callback URL.

---

## External links

- [Authentik OAuth 2.0 provider](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/)
- [Create an OAuth2 provider](https://docs.goauthentik.io/add-secure-apps/providers/oauth2/create-oauth2-provider/)

---

## See also

- [WebUI Authentication](webui-authentication.md) — All `[WebUI.OIDC]` fields and general OIDC configuration
