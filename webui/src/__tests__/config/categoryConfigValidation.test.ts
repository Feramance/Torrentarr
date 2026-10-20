import { expect, it } from "vitest";
import {
  getCategoryCrossSectionIssues,
  getCategoryOverlapWarnings,
} from "../../config/categoryConfigValidation";

it.each(["qbittorrent", "transmission", "deluge"])(
  "applies category conflicts and overlap warnings equally to %s",
  (Type) => {
    const config = {
      "Radarr-main": { Managed: true, Category: "movies" },
      TorrentClient: {
        seedbox: { Type, ManagedCategories: ["movies", "movies/4k"] },
      },
    };
    expect(getCategoryCrossSectionIssues(config)).toEqual([
      expect.objectContaining({ path: ["Radarr-main", "Category"] }),
      expect.objectContaining({
        path: ["TorrentClient", "seedbox", "ManagedCategories"],
      }),
    ]);
    expect(getCategoryOverlapWarnings(config)).toHaveLength(1);
  },
);
