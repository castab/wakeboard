[CmdletBinding()]
param([string]$Output = "artifacts\win-x64")
$ErrorActionPreference = "Stop"
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
    & docker build --file build/Dockerfile --target export --output "type=local,dest=$destination" .
    if ($LASTEXITCODE -ne 0) { throw "Wakeboard build failed." }
} finally { Pop-Location }
$exe = Join-Path $destination "Wakeboard.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Build completed without producing Wakeboard.exe." }
Write-Host "Built $exe" -ForegroundColor Green
