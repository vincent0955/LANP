// Publishes the backend as a self-contained single-file exe and stages it where
// tauri.conf.json's externalBin expects it. Runs as part of beforeBuildCommand so
// `tauri build` always bundles a backend that matches the current source.
//
// The target-triple suffix is required by Tauri's sidecar convention; this app
// only ships for Windows x64, so it is hardcoded.
import { execSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync } from "node:fs";
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

// The bundled container runtime (docs/docker-migration.md → Part 2): the WSL2
// rootfs tarball ships inside the installer so first run needs no download.
// Built by scripts/runtime/build-wsl-distro.sh (requires Linux or Docker),
// which is why it can't be produced inline here and is required up front.
const tarballName = "gamedashboard-wsl-rootfs.tar.gz";
const tarballSource = join(frontendDir, "..", "scripts", "runtime", tarballName);
if (!existsSync(tarballSource)) {
  console.error(
    `missing ${tarballSource}\n` +
      "Build it first (from any machine with Docker):\n" +
      '  docker run --rm -v "<repo>/scripts/runtime:/work" -w /work alpine:3.21 \\\n' +
      '    sh -c "apk add --no-cache bash curl tar coreutils ca-certificates && bash ./build-wsl-distro.sh"',
  );
  process.exit(1);
}
copyFileSync(tarballSource, join(binariesDir, tarballName));

console.log("sidecar staged: binaries/gamedashboard-api-x86_64-pc-windows-msvc.exe");
console.log(`runtime tarball staged: binaries/${tarballName}`);
