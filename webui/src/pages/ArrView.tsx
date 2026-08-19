import { type JSX } from "react";
import { RadarrView } from "./RadarrView";
import { SonarrView } from "./SonarrView";
import { LidarrView } from "./LidarrView";
import { ReadarrView } from "./ReadarrView";

interface ArrViewProps {
  type: "radarr" | "sonarr" | "lidarr" | "readarr";
  active: boolean;
}

export function ArrView({ type, active }: ArrViewProps): JSX.Element {
  if (type === "radarr") {
    return <RadarrView active={active} />;
  }
  if (type === "lidarr") {
    return <LidarrView active={active} />;
  }
  if (type === "readarr") {
    return <ReadarrView active={active} />;
  }
  return <SonarrView active={active} />;
}
