#!/usr/bin/env node
// Pre-commit hook: run npm run build from webui/ (cross-platform, no bash required).
const path = require("path");
const { execSync } = require("child_process");

const repoRoot = process.cwd();
const webuiDir = path.join(repoRoot, "webui");
process.chdir(webuiDir);
execSync("npm run build", { stdio: "inherit" });
