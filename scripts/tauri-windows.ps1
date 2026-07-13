if (-not $env:VALLEY_STEWARD_TAURI_ARGS) {
    throw "Tauri arguments were not provided by scripts/tauri.mjs."
}

$TauriArgs = @(ConvertFrom-Json -InputObject $env:VALLEY_STEWARD_TAURI_ARGS)

$vsDevCmd = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
if (-not (Test-Path -LiteralPath $vsDevCmd)) {
    throw "Microsoft C++ Build Tools were not found. Install the Visual C++ workload first."
}

$environment = & $env:ComSpec /d /s /c "call `"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set"
foreach ($line in $environment) {
    if ($line -match "^([^=]+)=(.*)$") {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], "Process")
    }
}

$cargoBin = Join-Path $env:USERPROFILE ".cargo\bin"
$env:PATH = "$cargoBin;$env:PATH"

& npm.cmd run tauri:raw -- @TauriArgs
exit $LASTEXITCODE
