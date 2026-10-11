import { type JSX } from "react";
import { ArrCatalogView } from "./ArrCatalogView";
import { ReadarrView } from "./ReadarrView";

interface ArrViewProps {
  type: "radarr" | "sonarr" | "lidarr" | "readarr";
  active: boolean;
}

export function ArrView({ type, active }: ArrViewProps): JSX.Element {
  if (type === "radarr") {
    return <ArrCatalogView kind="radarr" active={active} />;
  }
  if (type === "lidarr") {
    return <ArrCatalogView kind="lidarr" active={active} />;
  }
  if (type === "readarr") {
    return <ReadarrView active={active} />;
  }
  return <ArrCatalogView kind="sonarr" active={active} />;
}
