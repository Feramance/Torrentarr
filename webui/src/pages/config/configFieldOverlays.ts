import type { FieldDefinition } from "./configTypes";
type FieldOverlay = Partial<FieldDefinition>;

export const WEBUI_FIELD_OVERLAYS: Record<string, FieldOverlay> = {
  "WebUI.Host": {
    validate: (value) => {
      if (!String(value ?? "").trim()) {
        return "WebUI Host is required.";
      }
      return undefined;
    },
  },
  "WebUI.Port": {
    validate: (value) => {
      const port = typeof value === "number" ? value : Number(value);
      if (!Number.isInteger(port) || port < 1 || port > 65535) {
        return "WebUI Port must be between 1 and 65535.";
      }
      return undefined;
    },
  },
  "WebUI.Token": {
    fullWidth: true,
  },
  "WebUI.UrlBase": {
    validate: (value) => {
      const raw = String(value ?? "").trim();
      if (!raw) {
        return undefined;
      }
      if (!raw.startsWith("/")) {
        return "UrlBase must start with / (e.g. /torrentarr).";
      }
      if (raw.endsWith("/")) {
        return "UrlBase must not end with a trailing slash.";
      }
      if (raw.includes("//")) {
        return "UrlBase is invalid.";
      }
      return undefined;
    },
  },
  "WebUI.OIDC.Authority": {
    placeholder: "https://auth.example.com/application/o/qbitrr",
    fullWidth: true,
    description: "OIDC issuer/authority URL",
  },
  "WebUI.OIDC.Scopes": {
    placeholder: "openid profile",
    description: "Space-separated OIDC scopes",
  },
  "WebUI.OIDC.CallbackPath": {
    placeholder: "/signin-oidc",
    description: "OIDC callback path (must match IdP redirect URI)",
  },
  "WebUI.OIDC.RequireHttpsMetadata": {
    label: "Require HTTPS Metadata",
    description:
      "Require HTTPS for IdP metadata (set false only for local dev OIDC)",
  },
};
