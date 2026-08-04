[CmdletBinding()]
param([string]$Output = "artifacts\win-x64", [string]$Version = "dev")
$ErrorActionPreference = "Stop"
$displayVersion = $Version.Trim() -replace '^v', ''
if (-not $displayVersion) { $displayVersion = "dev" }
if ($displayVersion -eq "dev") {
    $assemblyVersion = "0.0.0"
} elseif ($displayVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    $assemblyVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
} else {
    throw "Invalid -Version '$Version'. Use a semantic version such as 1.2.3, 1.2.3-beta.1, or 'dev'."
}
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$destination = Join-Path $root $Output
if (Test-Path -LiteralPath $destination) {
    $resolved = (Resolve-Path -LiteralPath $destination).Path
    if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe output path: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push-Location $root
try {
    & docker build --file build/Dockerfile --target export --build-arg "WAKEBOARD_VERSION=$displayVersion" --build-arg "ASSEMBLY_VERSION=$assemblyVersion" --output "type=local,dest=$destination" .
    if ($LASTEXITCODE -ne 0) { throw "Wakeboard build failed." }
} finally { Pop-Location }
$exe = Join-Path $destination "Wakeboard.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Build completed without producing Wakeboard.exe." }
Write-Host "Built $exe (version $displayVersion)" -ForegroundColor Green
