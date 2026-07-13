import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const tauriArgs = process.argv.slice(2);
let result;

if (process.platform === "win32") {
  const windowsScript = fileURLToPath(new URL("./tauri-windows.ps1", import.meta.url));
  result = spawnSync(
    "powershell.exe",
    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", windowsScript],
    {
      stdio: "inherit",
      env: {
        ...process.env,
        VALLEY_STEWARD_TAURI_ARGS: JSON.stringify(tauriArgs),
      },
    },
  );
} else {
  result = spawnSync("npm", ["run", "tauri:raw", "--", ...tauriArgs], { stdio: "inherit" });
}

if (result.error) {
  console.error(result.error.message);
  process.exit(1);
}
process.exit(result.status ?? 1);
