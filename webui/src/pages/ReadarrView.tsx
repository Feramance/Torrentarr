import { ArrCatalogView } from "./ArrCatalogView";

export function ReadarrView({ active }: { active: boolean }) {
  return <ArrCatalogView kind="readarr" active={active} />;
}
