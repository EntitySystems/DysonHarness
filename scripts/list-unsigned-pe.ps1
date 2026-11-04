# Prints absolute paths of .exe and .dll under -Root whose Authenticode status is not Valid.
# One path per line. Exits 0 when every PE is already valid (no lines).
param(
    [Parameter(Mandatory)]
    [string]$Root
)

$ErrorActionPreference = "Stop"
$rootFull = (Resolve-Path -LiteralPath $Root).Path
Get-ChildItem -LiteralPath $rootFull -Recurse -File |
    Where-Object { $_.Extension -in '.exe', '.dll' } |
    Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne "Valid" } |
    ForEach-Object { $_.FullName }
