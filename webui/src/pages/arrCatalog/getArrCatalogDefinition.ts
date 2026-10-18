import type { AnyArrCatalogDefinition } from "./definition";
import { getLidarrCatalogDefinition } from "./lidarrDefinition";
import { getRadarrCatalogDefinition } from "./radarrDefinition";
import type { ArrCatalogKind } from "./registry";
import { getSonarrCatalogDefinition } from "./sonarrDefinition";

import { getReadarrCatalogDefinition } from "./readarrDefinition";

/** Resolve the catalog definition for an Arr kind. */
export function getArrCatalogDefinition(
  kind: ArrCatalogKind,
): AnyArrCatalogDefinition {
  if (kind === "readarr") return getReadarrCatalogDefinition();
  if (kind === "sonarr") return getSonarrCatalogDefinition();
  if (kind === "lidarr") return getLidarrCatalogDefinition();
  return getRadarrCatalogDefinition();
}
