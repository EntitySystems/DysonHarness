# Writes version.json (shipped next to DysonHarness.exe) for the in-app updater.
# Version comes from MSBuild ($(Version) / $(InformationalVersion)); CI stamps CalVer + channel.
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$Version = "",
    [string]$InformationalVersion = "",
    [string]$Rid = "win-x64",
    [string]$Repo = "EntitySystems/DysonHarness",
    [string]$Channel = ""
)

$ErrorActionPreference = "Continue"

function Get-ChannelFromBranch([string]$BranchName) {
    switch ($BranchName.ToLowerInvariant()) {
        { $_ -in @("main", "master") } { return "stable" }
        "release-preview" { return "preview" }
        default { return "preview" }
    }
}

function Get-NormalizedChannel([string]$Raw) {
    switch ($Raw.ToLowerInvariant()) {
        "stable" { return "stable" }
        "preview" { return "preview" }
        default { return $null }
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "1.0.0" }
if ([string]::IsNullOrWhiteSpace($InformationalVersion)) { $InformationalVersion = $Version }
if ([string]::IsNullOrWhiteSpace($Rid)) { $Rid = "win-x64" }

$resolvedChannel = $null
if (-not [string]::IsNullOrWhiteSpace($Channel)) {
    $resolvedChannel = Get-NormalizedChannel $Channel.Trim()
}

if ([string]::IsNullOrWhiteSpace($resolvedChannel)) {
    $branch = ""
    $refName = [Environment]::GetEnvironmentVariable("GITHUB_REF_NAME")
    if (-not [string]::IsNullOrWhiteSpace($refName) -and
        [Environment]::GetEnvironmentVariable("GITHUB_ACTIONS") -eq "true") {
        $branch = $refName.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($branch)) {
        try {
            Push-Location (Split-Path -Parent $PSScriptRoot)
            $gitOut = & git rev-parse --abbrev-ref HEAD 2>$null
            if ($LASTEXITCODE -eq 0 -and $gitOut) {
                $branch = [string]$gitOut.Trim()
                if ([string]::IsNullOrWhiteSpace($branch) -or $branch -eq "HEAD") {
                    $branch = ""
                }
            }
        }
        catch {
            $branch = ""
        }
        finally {
            Pop-Location -ErrorAction SilentlyContinue
        }
    }
    $resolvedChannel = if ([string]::IsNullOrWhiteSpace($branch)) {
        "preview"
    }
    else {
        Get-ChannelFromBranch $branch
    }
}

$content = [ordered]@{
    version              = $Version.Trim()
    informationalVersion = $InformationalVersion.Trim()
    channel              = $resolvedChannel
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
