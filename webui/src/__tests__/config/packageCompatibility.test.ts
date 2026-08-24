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

interface SemanticVersion {
  major: number;
  minor: number;
  patch: number;
}

interface VersionInterval {
  minimum: SemanticVersion;
  maximum?: SemanticVersion;
}

function compareVersions(
  left: SemanticVersion,
  right: SemanticVersion,
): number {
  return (
    left.major - right.major ||
    left.minor - right.minor ||
    left.patch - right.patch
  );
}

function parseVersion(version: string): SemanticVersion {
  const match = version.match(/^(\d+)(?:\.(\d+))?(?:\.(\d+))?$/);
  if (!match) {
    throw new Error(`Unsupported semantic version: ${version}`);
  }

  return {
    major: Number(match[1]),
    minor: Number(match[2] ?? 0),
    patch: Number(match[3] ?? 0),
  };
}

function parseNodeRange(engineRange: string): VersionInterval[] {
  const intervals = engineRange.split("||").map((alternative) => {
    const match = alternative
      .trim()
      .match(/^(\^|>=)\s*v?(\d+(?:\.\d+){0,2})$/);
    if (!match) {
      throw new Error(`Unsupported Node engine range: ${engineRange}`);
    }

    const minimum = parseVersion(match[2]);
    return {
      minimum,
      maximum:
        match[1] === "^"
          ? { major: minimum.major + 1, minor: 0, patch: 0 }
          : undefined,
    };
  });

  return intervals.sort((left, right) =>
    compareVersions(left.minimum, right.minimum),
  );
}

function isNodeRangeSubset(
  candidateRange: string,
  supportedRange: string,
): boolean {
  const candidateIntervals = parseNodeRange(candidateRange);
  const supportedIntervals = parseNodeRange(supportedRange);

  return candidateIntervals.every((candidate) =>
    supportedIntervals.some(
      (supported) =>
        compareVersions(supported.minimum, candidate.minimum) <= 0 &&
        (supported.maximum === undefined ||
          (candidate.maximum !== undefined &&
            compareVersions(supported.maximum, candidate.maximum) >= 0)),
    ),
  );
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
    expect(isNodeRangeSubset(projectNodeEngine, dependencyNodeEngine!)).toBe(
      true,
    );
  });

  it("advertises only Node versions supported by direct development dependencies", () => {
    const projectNodeEngine = packageManifest.engines.node;

    for (const dependencyName of Object.keys(packageManifest.devDependencies)) {
      const dependencyNodeEngine =
        packageLock.packages[`node_modules/${dependencyName}`].engines?.node;
      if (!dependencyNodeEngine) {
        continue;
      }

      expect(
        isNodeRangeSubset(projectNodeEngine, dependencyNodeEngine),
        `${dependencyName} must support the complete project Node range`,
      ).toBe(true);
    }
  });

  it("runs CI workflows on a compatible Node version", () => {
    const workflowsDirectory = new URL(
      "../../../../.github/workflows/",
      import.meta.url,
    );
    const projectNodeMajor = parseNodeRange(
      packageManifest.engines.node,
    )[0].minimum.major;

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
