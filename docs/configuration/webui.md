# WebUI Configuration

Configure Torrentarr's modern React-based web interface for monitoring and managing your Torrentarr instance.

---

## Overview

The Torrentarr WebUI provides:

- **Real-time monitoring** - Live process status and logs
- **Media browsing** - View movies, shows, and albums from Arr instances
- **Configuration management** - Edit config.toml from the web
- **System information** - Version, uptime, and health metrics
- **Responsive design** - Works on desktop, tablet, and mobile

**Access:** `http://localhost:6969/ui` (default)

---

## Configuration Section

WebUI settings are configured in the `[WebUI]` section:

```toml
[WebUI]
# Listen address
Host = "0.0.0.0"

# Listen port
Port = 6969

# Optional authentication token (API and optional browser auth)
Token = ""

# Authentication mode (see Authentication section below)
AuthDisabled = true
LocalAuthEnabled = false
OIDCEnabled = false
# For local auth: Username = ""
# PasswordHash is set via login page or POST /web/auth/set-password (never store plain password)
# Optional OIDC: [WebUI.OIDC] with Authority, ClientId, ClientSecret, Scopes, CallbackPath, RequireHttpsMetadata

# Live updates
LiveArr = true

# Group settings
GroupSonarr = true
GroupLidarr = true

# Default theme
Theme = "Dark"
```

---

## Host

```toml
Host = "0.0.0.0"
```

**Type:** String (IP address)
**Default:** `"0.0.0.0"`

IP address the WebUI server listens on.

**Options:**

- `"0.0.0.0"` - **(Default)** Listen on all network interfaces
- `"127.0.0.1"` - Localhost only (secure, but can't access remotely)
- `"192.168.1.100"` - Specific network interface

**Use cases:**

| Host | Use Case | Security | Remote Access |
|------|----------|----------|---------------|
| `0.0.0.0` | Docker, network access | Medium | ✅ Yes |
| `127.0.0.1` | Localhost only | High | ❌ No |
| Specific IP | Bind to one interface | Medium | ✅ Limited |

**Recommendations:**

```toml
# Docker (with reverse proxy)
Host = "0.0.0.0"

# Native (with reverse proxy)
Host = "127.0.0.1"

# Native (direct access)
Host = "0.0.0.0"  # Use with Token for security
```

---

## Port

```toml
Port = 6969
```

**Type:** Integer
**Default:** `6969`

TCP port the WebUI listens on.

**Access URL:** `http://<host>:<port>/ui`

**Common ports:**

```toml
Port = 6969   # Default
Port = 8080   # Alternative
Port = 443    # HTTPS (with reverse proxy)
```

**Port conflicts:**

If port 6969 is in use:

```bash
# Check what's using the port
sudo lsof -i :6969
sudo netstat -tulpn | grep 6969

# Change to alternative
Port = 6970
```

---

## Token

```toml
Token = ""
```

**Type:** String
**Default:** `""` (empty on first run; then auto-generated and persisted if left empty)

Bearer token for API authentication. All `/api/*` requests require this token (via `Authorization: Bearer` header or `?token=` on GET). When empty at startup, Torrentarr generates a secure token and saves it to config so the API is never unprotected.

When **AuthDisabled** is false (browser login enabled), users can still authenticate via this token (Bearer in the browser) or by logging in with local username/password or OIDC; after login, the session cookie grants access and `/web/token` returns the API token for the frontend.

For full authentication options (local login, OIDC, legacy AuthMode), see [WebUI Authentication](webui-authentication.md).

**Setting up authentication:**

```toml
[WebUI]
Token = "my-secure-token-12345"
```

**Generating secure tokens:**

```bash
# Linux/macOS
openssl rand -hex 32

# Alternative (any environment with Python)
python3 -c "import secrets; print(secrets.token_hex(32))"

# Output: a1b2c3d4e5f6...
```

**Using authenticated API:**

```bash
curl -H "Authorization: Bearer my-secure-token-12345" \
  http://localhost:6969/api/processes
```

!!! warning "Security Recommendation"
    **Always set a token if:**

    - Torrentarr is accessible from the internet
    - You're not using a reverse proxy with authentication
    - Multiple users have network access

    **Token can be omitted if:**

    - Behind reverse proxy with its own authentication
    - Only accessible from localhost
    - Running in a trusted private network

---

## Authentication

When **AuthDisabled** = `true` (default for existing configs), there is no login screen; the WebUI and API are protected only by the Token (or are public if Token was empty and has not yet been auto-generated). When **AuthDisabled** = `false`, browser users must either log in (local username/password and/or OIDC) or present the Bearer token. At least one of **LocalAuthEnabled** or **OIDCEnabled** should be true so the login page can offer a sign-in method.

**New installs:** If Torrentarr creates the config file on first run (it did not exist before), the generated config has **AuthDisabled = false** and **LocalAuthEnabled = true**. Users see a welcome screen to set an admin username and password before accessing the rest of the WebUI. Existing configs are unchanged unless you edit auth settings.

Local auth uses a single **Username** and a stored **PasswordHash** (set via the login page or `POST /web/auth/set-password`; never store a plain password in config). OIDC uses an external identity provider (e.g. Authentik, Keycloak) and a `[WebUI.OIDC]` block with Authority, ClientId, ClientSecret, and related settings.

**Full reference:** [WebUI Authentication](webui-authentication.md). **Example with Authentik:** [OIDC with Authentik](webui-oidc-authentik.md).

---

## LiveArr

```toml
LiveArr = true
```

**Type:** Boolean
**Default:** `true`

Enable live updates for Arr instance views (Radarr/Sonarr/Lidarr tabs).

**When true:**
- Real-time status updates
- Progress bars update automatically
- No manual page refresh needed
- Uses polling every few seconds

**When false:**
- Static snapshots
- Must manually refresh page
- Lower resource usage
- Reduced API calls to Arr instances

**Recommendation:** `true` for best user experience.

**Performance consideration:**

```toml
# High-resource system
LiveArr = true  # Enable real-time updates

# Low-resource system (Raspberry Pi, etc.)
LiveArr = false  # Reduce load
```

---

## GroupSonarr

```toml
GroupSonarr = true
```

**Type:** Boolean
**Default:** `true`

Group Sonarr episodes by series in the WebUI.

**When true (grouped):**

```
└─ Breaking Bad
   ├─ S01E01 - Pilot
   ├─ S01E02 - Cat's in the Bag
   └─ S01E03 - ...and the Bag's in the River
```

**When false (flat list):**

```
├─ Breaking Bad S01E01 - Pilot
├─ Breaking Bad S01E02 - Cat's in the Bag
└─ Breaking Bad S01E03 - ...and the Bag's in the River
```

**Recommendation:** `true` for better organization.

---

## GroupLidarr

```toml
GroupLidarr = true
```

**Type:** Boolean
**Default:** `true`

Group Lidarr albums by artist in the WebUI.

**When true (grouped):**

```
└─ Pink Floyd
   ├─ The Dark Side of the Moon
   ├─ The Wall
   └─ Wish You Were Here
```

**When false (flat list):**

```
├─ Pink Floyd - The Dark Side of the Moon
├─ Pink Floyd - The Wall
└─ Pink Floyd - Wish You Were Here
```

**Recommendation:** `true` for better navigation.

---

## ViewDensity

```toml
ViewDensity = "Comfortable"
```

**Type:** String
**Default:** `"Comfortable"`
**Options:** `"Comfortable"`, `"Compact"`

UI density setting for tables and lists.

- `"Comfortable"` - More spacing, easier to read
- `"Compact"` - Denser layout, shows more data per screen

**Note:** Users can toggle this in the WebUI settings. This sets the initial default.

---

## Theme

```toml
Theme = "Dark"
```

**Type:** String
**Default:** `"Dark"`
**Options:** `"Dark"`, `"Light"`

Default color theme for the WebUI.

- `"Dark"` - Dark mode (easier on eyes, lower power consumption)
- `"Light"` - Light mode (better in bright environments)

**Note:** Users can toggle theme in the WebUI itself. This sets the initial default.

---

## Complete Configuration Examples

### Example 1: Default (Public Access)

```toml
[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = ""  # Auto-generated at startup if empty
AuthDisabled = true
LiveArr = true
GroupSonarr = true
GroupLidarr = true
Theme = "Dark"
ViewDensity = "Comfortable"
```

**Access:** `http://localhost:6969/ui`

**Use case:** Local network, trusted environment.

---

### Example 2: Secured with Token

```toml
[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = "a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6"
AuthDisabled = true
LiveArr = true
GroupSonarr = true
GroupLidarr = true
Theme = "Dark"
ViewDensity = "Comfortable"
```

**Access:** `http://localhost:6969/ui` (token handled automatically by WebUI)

**Use case:** Exposed to internet or untrusted network.

---

### Example 3: Local auth (username/password)

```toml
[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = ""  # Optional; set for API or leave to auto-generate
AuthDisabled = false
LocalAuthEnabled = true
OIDCEnabled = false
Username = "admin"
# PasswordHash set via login page "Set password" or POST /web/auth/set-password
LiveArr = true
GroupSonarr = true
GroupLidarr = true
Theme = "Dark"
ViewDensity = "Comfortable"
```

See [WebUI Authentication](webui-authentication.md) for details (set-password flow, TORRENTARR_SETUP_TOKEN, etc.).

---

### Example 4: OIDC (e.g. Authentik)

```toml
[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = ""
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
ClientId = "your-client-id"
ClientSecret = "your-client-secret"
Scopes = "openid profile"
CallbackPath = "/signin-oidc"
RequireHttpsMetadata = true
```

See [OIDC with Authentik](webui-oidc-authentik.md) for step-by-step setup.

---

### Example 5: Localhost Only (with Reverse Proxy)

```toml
[WebUI]
Host = "127.0.0.1"
Port = 6969
Token = ""  # Reverse proxy handles auth
LiveArr = true
GroupSonarr = true
GroupLidarr = true
Theme = "Dark"
ViewDensity = "Comfortable"
```

**Nginx reverse proxy:**

```nginx
location /torrentarr/ {
    proxy_pass http://127.0.0.1:6969/;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
}
```

**Access:** `https://yourdomain.com/torrentarr/ui`

---

### Example 6: Low Resource System

```toml
[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = ""
LiveArr = false  # Disable auto-refresh
GroupSonarr = false  # Flat lists
GroupLidarr = false  # Flat lists
Theme = "Dark"
```

**Use case:** Raspberry Pi, low-power devices.

---

## Reverse Proxy Configuration

### Nginx

```nginx
server {
    listen 80;
    server_name torrentarr.example.com;

    location / {
        proxy_pass http://localhost:6969;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection 'upgrade';
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
    }
}
```

**Torrentarr config:**

```toml
[WebUI]
Host = "127.0.0.1"  # Only listen on localhost
Port = 6969
```

---

### Apache

```apache
<VirtualHost *:80>
    ServerName torrentarr.example.com

    ProxyPreserveHost On
    ProxyPass / http://localhost:6969/
    ProxyPassReverse / http://localhost:6969/

    <Location />
        Require all granted
    </Location>
</VirtualHost>
```

**Enable required modules:**

```bash
sudo a2enmod proxy
sudo a2enmod proxy_http
sudo systemctl restart apache2
```

---

### Traefik (Docker)

```yaml
services:
  torrentarr:
    image: feramance/torrentarr:latest
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.torrentarr.rule=Host(`torrentarr.example.com`)"
      - "traefik.http.services.torrentarr.loadbalancer.server.port=6969"
      - "traefik.http.routers.torrentarr.entrypoints=websecure"
      - "traefik.http.routers.torrentarr.tls.certresolver=letsencrypt"
```

---

### Caddy

```caddyfile
torrentarr.example.com {
    reverse_proxy localhost:6969
}
```

---

## Docker Port Mapping

**Docker Run:**

```bash
docker run -d \
  --name torrentarr \
  -p 6969:6969 \
  -v /path/to/config:/config \
  feramance/torrentarr:latest
```

**Docker Compose:**

```yaml
version: '3'
services:
  torrentarr:
    image: feramance/torrentarr:latest
    container_name: torrentarr
    ports:
      - "6969:6969"  # External:Internal
    volumes:
      - /path/to/config:/config
```

**Alternative port mapping:**

```yaml
ports:
  - "8080:6969"  # Access on port 8080 externally
```

**Access:** `http://localhost:8080/ui`

---

## Config file only

WebUI settings (Host, Port, Token) are read from `config.toml` only. Torrentarr does not support environment variable overrides for individual settings. Use the [Config Editor](../webui/config-editor.md) or edit `config.toml` directly. To point Torrentarr at a different config file (e.g. in Docker), set `TORRENTARR_CONFIG=/config/config.toml`.

---

## Troubleshooting

### WebUI Not Loading

**Symptom:** Cannot access `http://localhost:6969/ui`

**Solutions:**

1. **Check Torrentarr is running:**
   ```bash
   # Docker
   docker ps | grep torrentarr

   # Systemd
   systemctl status torrentarr

   # Process
   ps aux | grep torrentarr
   ```

2. **Verify port:**
   ```bash
   # Check if port is listening
   sudo netstat -tulpn | grep 6969
   sudo lsof -i :6969
   ```

3. **Check logs:**
   ```bash
   # Docker
   docker logs torrentarr | grep -i webui

   # Native
   tail -f ~/logs/WebUI.log
   ```

4. **Verify configuration:**
   ```toml
   [WebUI]
   Host = "0.0.0.0"
   Port = 6969
   ```

---

### 401 Unauthorized

**Symptom:** API requests return 401 errors

**Solutions:**

1. **Check token is set:** When Token is empty, Torrentarr auto-generates one at startup; ensure the app has run at least once and config was saved.
2. **Include token in requests:**
   ```bash
   curl -H "Authorization: Bearer your-token" \
     http://localhost:6969/api/processes
   ```
3. **When browser login is enabled (AuthDisabled = false):** For API calls use the Bearer token. For the WebUI, ensure you are logged in (local or OIDC) or provide the token; if using local auth, ensure a password has been set (PasswordHash in config or via the login page "Set password").
4. **Clear browser cache and cookies**
5. **Check WebUI logs:**
   ```bash
   tail -f ~/logs/WebUI.log | grep -i "401\|auth"
   ```

For more authentication troubleshooting, see [WebUI Authentication](webui-authentication.md).

---

### Connection Refused

**Symptom:** Browser shows "Connection refused"

**Solutions:**

1. **Check Host binding:**
   ```toml
   # If accessing remotely, must not be 127.0.0.1
   Host = "0.0.0.0"
   ```

2. **Check firewall:**
   ```bash
   # UFW
   sudo ufw allow 6969

   # Firewalld
   sudo firewall-cmd --add-port=6969/tcp --permanent
   sudo firewall-cmd --reload
   ```

3. **Docker: Check port mapping:**
   ```bash
   docker port torrentarr
   ```

---

### Slow Performance

**Symptom:** WebUI is slow or unresponsive

**Solutions:**

1. **Disable live updates:**
   ```toml
   LiveArr = false
   ```

2. **Disable grouping:**
   ```toml
   GroupSonarr = false
   GroupLidarr = false
   ```

3. **Check resource usage:**
   ```bash
   docker stats torrentarr
   htop
   ```

4. **Clear browser cache**

5. **Reduce log retention:**
   - Fewer logs = faster log view
   - Consider log rotation

---

### CORS Errors

**Symptom:** Browser console shows CORS errors

**Solutions:**

1. **Access via correct URL:**
   - Use `http://localhost:6969/ui`
   - Not `http://127.0.0.1:6969/ui` (different origin)

2. **Configure reverse proxy correctly:**
   - Set proper headers
   - See reverse proxy examples above

---

## Security Best Practices

### 1. Use a Strong Token

```bash
# Generate secure token
openssl rand -hex 32
```

```toml
[WebUI]
Token = "a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6q7r8s9t0u1v2w3x4y5z6"
```

---

### 2. Bind to Localhost with Reverse Proxy

```toml
[WebUI]
Host = "127.0.0.1"  # Only localhost
```

Use Nginx/Apache/Caddy for external access with HTTPS.

---

### 3. Use HTTPS

Never expose WebUI over HTTP on the internet.

**Options:**

- Reverse proxy with Let's Encrypt
- Cloudflare Tunnel
- VPN (Tailscale, WireGuard)

---

### 4. Restrict Network Access

**Docker:**

```yaml
services:
  torrentarr:
    networks:
      - internal  # Private network only

networks:
  internal:
    internal: true  # No external access
```

**Firewall:**

```bash
# Only allow from specific IP
sudo ufw allow from 192.168.1.0/24 to any port 6969
```

---

### 5. Regular Updates

Keep Torrentarr updated for security patches:

```bash
# Docker
docker pull feramance/torrentarr:latest
docker restart torrentarr

# Binary: WebUI Install update, or download latest from GitHub Releases and replace executable
```

---

## Performance Tuning

### For Large Libraries

```toml
[WebUI]
LiveArr = false  # Disable auto-refresh
GroupSonarr = false  # Faster rendering
GroupLidarr = false  # Faster rendering
```

**In WebUI:**
- Use search/filters to reduce displayed items
- Limit log entries shown

---

### For Low-Resource Systems

```toml
[WebUI]
Host = "127.0.0.1"
Port = 6969
Token = ""
LiveArr = false
GroupSonarr = false
GroupLidarr = false
Theme = "Dark"  # Lower power on OLED
```

---

## See Also

- [WebUI Usage Guide](../webui/index.md) - Using the WebUI
- [WebUI Authentication](webui-authentication.md) - Full auth reference (local and OIDC)
- [OIDC with Authentik](webui-oidc-authentik.md) - Step-by-step Authentik setup
- [Config File Reference](config-file.md) - All configuration options
- [Getting Started](../getting-started/index.md) - Initial setup
- [Troubleshooting](../troubleshooting/index.md) - Common issues
