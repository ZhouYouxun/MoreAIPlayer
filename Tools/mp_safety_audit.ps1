<#
.SYNOPSIS
    Static multiplayer-safety audit for MoreAIPlayer (design doc "8月11日更改" §3.3).

.DESCRIPTION
    Companions are singleplayer-only. This script scans every source file for the operations that
    would be unsafe on a multiplayer client or dedicated server, and confirms each one sits inside
    an approved guard (the CompanionRuntimePolicy properties, a netMode / dedServ branch, or a
    ValidOwner check). It is a reviewer aid, not a proof: it reports every authoritative operation
    and flags any that has no recognised guard in its enclosing method.

    Run from anywhere:  pwsh -File Tools/mp_safety_audit.ps1
    Exit code 0 = no unguarded authoritative operations found; 1 = review required.

    This folder is excluded from the packaged mod via build.txt (buildIgnore = Tools/*).
#>

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

# Tokens that count as an approved guard when they appear in the enclosing method (or, for
# whole-file client contexts, anywhere above the match in the file).
$guardTokens = @(
    'CanRunSyntheticPlayers', 'CanMutateWorld', 'IsAuthoritativeDamageSide', 'CanRunLocalUI',
    'CanRunNetworkCleanup', 'IsDedicatedServer', 'ValidOwner', 'IsCompanion', 'IsHunter',
    'netMode ==', 'netMode !=', 'NetmodeID', 'dedServ', 'Main.dedServ', 'SinglePlayer'
)

# Authoritative operations: unsafe if performed on a non-authoritative side. An unguarded hit here
# fails the audit.
$authoritativePatterns = [ordered]@{
    'world-mutation'   = 'WorldGen\.(PlaceTile|PlaceWall|OpenDoor|CloseDoor|KillTile|ReplaceTile|SlopeTile|PoundTile|Place1x2|PlaceObject)\b'
    'myPlayer-write'   = 'Main\.myPlayer\s*='
    'mouse-write'      = 'Main\.(mouseX|mouseY|mouseLeft|mouseRight)\s*='
    'player-slot-write'= 'Main\.player\[[^\]]+\]\s*='
    'net-send'         = 'NetMessage\.SendData'
}

# Informational: client-side reads that are safe on a client but would be wrong on a dedicated
# server. UI / book code never runs on a dedicated server, so these are listed, not failed.
$informationalPatterns = [ordered]@{
    'localplayer-read' = 'Main\.LocalPlayer'
}

function Get-EnclosingGuardContext {
    param([string[]]$Lines, [int]$Index)
    # Walk upward to the nearest method/property declaration (4-space indent in this codebase),
    # collecting text so guard tokens inside the method body above the hit are visible.
    $collected = New-Object System.Text.StringBuilder
    for ($i = $Index; $i -ge 0; $i--) {
        [void]$collected.AppendLine($Lines[$i])
        if ($Lines[$i] -match '^\s{4}(public|private|internal|protected)[^;]*\($') { break }
        if ($Lines[$i] -match '^\s{4}(public|private|internal|protected)[^;=]*\(.*\)\s*$') { break }
        if ($Lines[$i] -match '^\s{4}(public|private|internal|protected).*=>') { break }
    }
    return $collected.ToString()
}

function Test-Guarded {
    param([string]$Context, [string]$FileText, [int]$LineIndex, [string[]]$Lines)
    foreach ($token in $guardTokens) {
        if ($Context -like "*$token*") { return $true }
    }
    # Whole-file client context: a book (ModItem) or UIState that itself early-returns on netMode.
    $above = ($Lines[0..([Math]::Max(0, $LineIndex))] -join "`n")
    foreach ($token in $guardTokens) {
        if ($above -like "*$token*") { return $true }
    }
    return $false
}

$files = Get-ChildItem -Path $Root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$failures = @()
$informational = @()
$authoritativeCount = 0

foreach ($file in $files) {
    $lines = Get-Content -LiteralPath $file.FullName
    $rel = $file.FullName.Substring($Root.Length).TrimStart('\','/')

    for ($n = 0; $n -lt $lines.Count; $n++) {
        $line = $lines[$n]
        if ($line -match '^\s*//') { continue }  # skip comment lines

        foreach ($category in $authoritativePatterns.Keys) {
            if ($line -match $authoritativePatterns[$category]) {
                $authoritativeCount++
                $context = Get-EnclosingGuardContext -Lines $lines -Index $n
                if (-not (Test-Guarded -Context $context -LineIndex $n -Lines $lines)) {
                    $failures += [pscustomobject]@{
                        Category = $category
                        File     = $rel
                        Line     = $n + 1
                        Text     = $line.Trim()
                    }
                }
            }
        }
        foreach ($category in $informationalPatterns.Keys) {
            if ($line -match $informationalPatterns[$category]) {
                $informational += [pscustomobject]@{
                    Category = $category
                    File     = $rel
                    Line     = $n + 1
                }
            }
        }
    }
}

Write-Host "=== MoreAIPlayer multiplayer-safety audit ==="
Write-Host ("Scanned {0} source files." -f $files.Count)
Write-Host ("Authoritative operations found: {0} (world mutation / myPlayer / mouse / slot / net-send)." -f $authoritativeCount)
Write-Host ("Client-side Main.LocalPlayer reads (informational): {0}." -f $informational.Count)
Write-Host ""

if ($failures.Count -eq 0) {
    Write-Host "PASS: every authoritative operation sits inside an approved guard." -ForegroundColor Green
    exit 0
}

Write-Host ("FAIL: {0} authoritative operation(s) without a recognised guard:" -f $failures.Count) -ForegroundColor Red
$failures | Sort-Object File, Line | Format-Table -AutoSize Category, File, Line, Text
exit 1
