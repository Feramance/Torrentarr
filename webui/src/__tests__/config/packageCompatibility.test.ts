import { readdirSync, readFileSync } from "node:fs";
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
      version?: string;
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

function majorVersion(versionRange: string): number {
  const match = versionRange.match(/\d+/);
  if (!match) {
    throw new Error(`Unsupported version range: ${versionRange}`);
  }

  return Number(match[0]);
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
    expect(
      majorVersion(packageManifest.devDependencies[dependencyName]),
    ).toBeGreaterThanOrEqual(7);
    expect(majorVersion(lockedDependency.version!)).toBeGreaterThanOrEqual(7);
    expect(dependencyNodeEngine).toBeDefined();
    expect(minimumNodeMajor(dependencyNodeEngine!)).toBeLessThanOrEqual(
      minimumNodeMajor(projectNodeEngine),
    );
  });

  it("runs CI workflows on a compatible Node version", () => {
    const workflowsDirectory = new URL(
      "../../../../.github/workflows/",
      import.meta.url,
    );
    const projectNodeMajor = minimumNodeMajor(packageManifest.engines.node);

    for (const workflowName of readdirSync(workflowsDirectory)) {
      const workflow = readFileSync(
        new URL(workflowName, workflowsDirectory),
        "utf8",
      );
      if (!workflow.includes("actions/setup-node@")) {
        continue;
      }

      const configuredNodeMajors = [
        ...workflow.matchAll(/\bnode-version:\s*(?:\[\s*)?["']?(\d+)/g),
      ].map((match) => Number(match[1]));

      expect(
        configuredNodeMajors,
        `${workflowName} must declare a concrete Node version`,
      ).not.toHaveLength(0);
      for (const configuredNodeMajor of configuredNodeMajors) {
        expect(
          configuredNodeMajor,
          `${workflowName} must support the project's Node baseline`,
        ).toBeGreaterThanOrEqual(projectNodeMajor);
      }
    }
  });
});
