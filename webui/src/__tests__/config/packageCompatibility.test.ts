import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

interface PackageManifest {
  devDependencies: Record<string, string>;
  engines: Record<string, string>;
}

interface PackageLock {
  packages: Record<
    string,
    {
      devDependencies?: Record<string, string>;
      engines?: Record<string, string>;
    }
  >;
}

const packageManifest = JSON.parse(
  readFileSync(new URL("../../../package.json", import.meta.url), "utf8"),
) as PackageManifest;
const packageLock = JSON.parse(
  readFileSync(new URL("../../../package-lock.json", import.meta.url), "utf8"),
) as PackageLock;

function minimumNodeMajor(engineRange: string): number {
  const match = engineRange.match(/>=\s*(\d+)/);
  if (!match) {
    throw new Error(`Unsupported Node engine range: ${engineRange}`);
  }

  return Number(match[1]);
}

describe("development dependency compatibility", () => {
  it("keeps jest-dom compatible with the supported Node version", () => {
    const dependencyName = "@testing-library/jest-dom";
    const lockedDependency =
      packageLock.packages[`node_modules/${dependencyName}`];
    const projectNodeEngine = packageManifest.engines.node;
    const dependencyNodeEngine = lockedDependency.engines?.node;

    expect(packageLock.packages[""].devDependencies?.[dependencyName]).toBe(
      packageManifest.devDependencies[dependencyName],
    );
    expect(dependencyNodeEngine).toBeDefined();
    expect(minimumNodeMajor(dependencyNodeEngine!)).toBeLessThanOrEqual(
      minimumNodeMajor(projectNodeEngine),
    );
  });
});
