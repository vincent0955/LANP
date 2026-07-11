// Publishes the backend as a self-contained single-file exe and stages it where
// tauri.conf.json's externalBin expects it. Runs as part of beforeBuildCommand so
// `tauri build` always bundles a backend that matches the current source.
//
// The target-triple suffix is required by Tauri's sidecar convention; this app
// only ships for Windows x64, so it is hardcoded.
import { execSync } from "node:child_process";
import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const frontendDir = dirname(dirname(fileURLToPath(import.meta.url)));
const apiProject = join(frontendDir, "..", "backend", "GameDashboard.Api");
const publishDir = join(frontendDir, "src-tauri", "binaries", "publish");
const binariesDir = join(frontendDir, "src-tauri", "binaries");

mkdirSync(binariesDir, { recursive: true });

execSync(
  [
    `dotnet publish "${apiProject}"`,
    "-c Release",
    "-r win-x64",
    "--self-contained true",
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    `-o "${publishDir}"`,
  ].join(" "),
  { stdio: "inherit" },
);

copyFileSync(
  join(publishDir, "GameDashboard.Api.exe"),
  join(binariesDir, "gamedashboard-api-x86_64-pc-windows-msvc.exe"),
);
// Bundled as a Tauri resource so it lands next to the sidecar in the install
// dir, where users can edit it (e.g. to set an ApiToken). The backend also has
// compiled-in defaults for every setting, so a missing file is fine.
copyFileSync(join(publishDir, "appsettings.json"), join(binariesDir, "appsettings.json"));

console.log("sidecar staged: binaries/gamedashboard-api-x86_64-pc-windows-msvc.exe");
