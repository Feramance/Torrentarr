import { expect, it } from "vitest";
import {
  inspectionOutcome,
  maintenanceActionTitle,
  maintenanceOperations,
  maintenanceCategories,
} from "../../config/maintenanceOperations";
import type { MaintenanceOperationResult } from "../../api/types";

it("keeps all ten tools in three compact groups", () => {
  expect(maintenanceOperations).toHaveLength(10);
  expect(
    new Set(maintenanceOperations.map((tool) => tool.operation)).size,
  ).toBe(10);
  expect(maintenanceCategories).toEqual([
    "Recovery",
    "Organization",
    "Storage",
  ]);
  for (const tool of maintenanceOperations)
    expect(maintenanceCategories).toContain(tool.category);
});
it("distinguishes unchecked, empty, available, blocked and incomplete results", () => {
  const result: MaintenanceOperationResult = {
    clientInstanceId: "fixture",
    operation: "OrphanScan",
    state: "ready",
    examined: 1,
    excluded: 0,
    uninspectable: 0,
    actionable: 0,
    blockedActions: 0,
  };
  expect(inspectionOutcome()).toBe("Not checked");
  expect(inspectionOutcome(result)).toBe("Nothing found");
  expect(inspectionOutcome({ ...result, actionable: 1 })).toBe(
    "Changes available",
  );
  expect(inspectionOutcome({ ...result, blockedActions: 1 })).toBe(
    "Changes blocked",
  );
  expect(inspectionOutcome({ ...result, state: "blocked" })).toBe(
    "Could not check completely",
  );
  expect(
    inspectionOutcome({ ...result, state: "partial", uninspectable: 1 }),
  ).toBe("Could not check completely");
});
it("names concrete tag and category changes", () => {
  const action = {
    id: "fixture",
    operation: "PrivateTagging" as const,
    clientInstanceId: "fixture",
    reason: "fixture",
    bytes: 0,
    destructive: false,
    requiredCapabilities: [],
  };
  expect(
    maintenanceActionTitle({
      ...action,
      kind: "AddTag",
      arguments: { tag: "private" },
    }),
  ).toBe("Add tag “private”");
  expect(
    maintenanceActionTitle({
      ...action,
      kind: "SetCategory",
      arguments: { category: "movies" },
    }),
  ).toBe("Change category to “movies”");
});
