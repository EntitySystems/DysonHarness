# Writes version.json (shipped next to DysonHarness.exe) for the in-app updater.
# Version comes from MSBuild ($(Version) / $(InformationalVersion)); CI stamps CalVer.
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$Version = "",
    [string]$InformationalVersion = "",
    [string]$Rid = "win-x64",
    [string]$Repo = "EntitySystems/DysonHarness"
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.0.0" }
if ([string]::IsNullOrWhiteSpace($InformationalVersion)) { $InformationalVersion = $Version }
if ([string]::IsNullOrWhiteSpace($Rid)) { $Rid = "win-x64" }

$content = [ordered]@{
    version              = $Version.Trim()
    informationalVersion = $InformationalVersion.Trim()
    rid                  = $Rid.Trim()
    repo                 = $Repo.Trim()
} | ConvertTo-Json

$dir = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($dir)) {
    $dir = (Get-Location).Path
    $OutputPath = Join-Path $dir (Split-Path -Leaf $OutputPath)
}
if (-not (Test-Path -LiteralPath $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

# Avoid rewriting identical content (keeps incremental builds happy).
if (Test-Path -LiteralPath $OutputPath) {
    $existing = Get-Content -LiteralPath $OutputPath -Raw -ErrorAction SilentlyContinue
    if ($existing -eq $content) {
        exit 0
    }
}

[System.IO.File]::WriteAllText($OutputPath, $content)
