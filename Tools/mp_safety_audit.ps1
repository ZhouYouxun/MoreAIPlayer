<#
.SYNOPSIS
    Static multiplayer-safety audit for MoreAIPlayer.

.DESCRIPTION
    Companions and player scaling are intentionally single-player-only. This reviewer aid checks
    authoritative operations against a guard in their enclosing member and enforces several
    project-specific invariants that previously regressed silently.

    This is not live multiplayer proof. Run from anywhere:
      pwsh -File Tools/mp_safety_audit.ps1

    Exit code 0 = no recognised regression; 1 = review required.
    Tools are excluded from the packaged mod by build.txt (buildIgnore = Tools/*).
#>

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$authoritativePatterns = [ordered]@{
    'world-mutation'    = 'WorldGen\.(PlaceTile|PlaceWall|OpenDoor|CloseDoor|KillTile|ReplaceTile|SlopeTile|PoundTile|Place1x2|PlaceObject)\b'
    'myPlayer-write'    = 'Main\.myPlayer\s*='
    'mouse-write'       = 'Main\.(mouseX|mouseY|mouseLeft|mouseRight)\s*='
    'player-slot-write' = 'Main\.player\[[^\]]+\]\s*='
    'net-send'          = 'NetMessage\.SendData'
}

# Guards are category-specific. In particular, client-only UI and cleanup permissions no longer
# excuse world mutation or synthetic player-slot writes.
$guardPatterns = @{
    'world-mutation'    = 'CanMutateWorld|Main\.netMode\s*!=\s*NetmodeID\.MultiplayerClient|Main\.netMode\s*==\s*NetmodeID\.MultiplayerClient|Main\.netMode\s*==\s*NetmodeID\.SinglePlayer'
    'myPlayer-write'    = 'CanRunSyntheticPlayers|Main\.netMode\s*(==|!=)\s*NetmodeID\.SinglePlayer|ValidOwner|IsVirtualSlot'
    'mouse-write'       = 'CanRunSyntheticPlayers|Main\.netMode\s*(==|!=)\s*NetmodeID\.SinglePlayer|ValidOwner'
    'player-slot-write' = 'CanRunSyntheticPlayers|Main\.netMode\s*(==|!=)\s*NetmodeID\.SinglePlayer|ReferenceEquals\(Main\.player|Main\.player\[[^\]]+\]\s*==\s*instance\.Entity'
    # Sending can legitimately be client- or server-authoritative, but the side must be explicit.
    'net-send'          = 'Main\.netMode|NetmodeID\.(MultiplayerClient|Server)|IsAuthoritativeDamageSide'
}

function Get-EnclosingMemberContext {
    param([string[]]$Lines, [int]$Index)

    $start = 0
    for ($i = $Index; $i -ge 0; $i--) {
        if ($Lines[$i] -match '^\s{4}(public|private|internal|protected)\b.*(\(|=>)') {
            $start = $i
            break
        }
    }
    return ($Lines[$start..$Index] -join "`n") -replace '(?m)//.*$', ''
}

function Get-MethodText {
    param([string]$Path, [string]$MethodName)

    $lines = Get-Content -LiteralPath $Path
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "\b$([regex]::Escape($MethodName))\s*\(") {
            $start = $i
            break
        }
    }
    if ($start -lt 0) { return $null }

    $depth = 0
    $bodyStarted = $false
    $collected = New-Object System.Collections.Generic.List[string]
    for ($i = $start; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $collected.Add($line)
        $depth += ([regex]::Matches($line, '\{')).Count
        if ($depth -gt 0) { $bodyStarted = $true }
        $depth -= ([regex]::Matches($line, '\}')).Count
        if ($bodyStarted -and $depth -eq 0) { break }
    }
    return $collected -join "`n"
}

function Add-InvariantFailure {
    param([string]$Name, [string]$File, [string]$Message)
    $script:failures += [pscustomobject]@{
        Category = "invariant:$Name"
        File = $File
        Line = '-'
        Text = $Message
    }
}

$files = Get-ChildItem -Path $Root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$failures = @()
$informationalCount = 0
$authoritativeCount = 0

foreach ($file in $files) {
    $lines = Get-Content -LiteralPath $file.FullName
    $rel = $file.FullName.Substring($Root.Length).TrimStart('\','/')

    for ($n = 0; $n -lt $lines.Count; $n++) {
        $line = $lines[$n]
        if ($line -match '^\s*//') { continue }
        if ($line -match 'Main\.LocalPlayer') { $informationalCount++ }

        foreach ($category in $authoritativePatterns.Keys) {
            if ($line -notmatch $authoritativePatterns[$category]) { continue }
            $authoritativeCount++
            $context = Get-EnclosingMemberContext -Lines $lines -Index $n
            if ($context -notmatch $guardPatterns[$category]) {
                $failures += [pscustomobject]@{
                    Category = $category
                    File = $rel
                    Line = $n + 1
                    Text = $line.Trim()
                }
            }
        }
    }
}

# Regression invariant 1: ModItem.NetSend runs on clients and servers. It may serialize the item's
# cached data, but must not guess an owner through Main.LocalPlayer.
$staffRel = 'Companion\Content\PhantomStaff.cs'
$staffPath = Join-Path $Root $staffRel
$staffNetSend = (Get-MethodText -Path $staffPath -MethodName 'NetSend') -replace '(?m)//.*$', ''
if ([string]::IsNullOrWhiteSpace($staffNetSend)) {
    Add-InvariantFailure -Name 'staff-net-context' -File $staffRel -Message 'NetSend method was not found.'
} elseif ($staffNetSend -match 'Main\.LocalPlayer') {
    Add-InvariantFailure -Name 'staff-net-context' -File $staffRel -Message 'NetSend reads Main.LocalPlayer.'
}

$staffText = Get-Content -LiteralPath $staffPath -Raw
if ($staffText -notmatch 'MaximumNetworkSnapshotBytes\s*=\s*48\s*\*\s*1024') {
    Add-InvariantFailure -Name 'staff-packet-budget' -File $staffRel -Message 'Expected a 48 KiB compressed snapshot budget.'
}
if ($staffText -notmatch 'MaximumDecompressedSnapshotBytes' -or $staffText -notmatch 'CopyWithLimit') {
    Add-InvariantFailure -Name 'staff-decompression-limit' -File $staffRel -Message 'Bounded snapshot decompression is missing.'
}

# Regression invariant 2: persisted scale intent may remain non-1 in multiplayer, so every runtime
# gameplay/draw consumer must use the fail-closed RuntimeScale facade.
$scaleRel = 'PlayerScalePlayer.cs'
$scalePath = Join-Path $Root $scaleRel
foreach ($method in @('PostUpdateMiscEffects', 'PostUpdateRunSpeeds', 'ModifyWeaponDamage', 'ModifyItemScale', 'TransformDrawData')) {
    $methodText = Get-MethodText -Path $scalePath -MethodName $method
    if ([string]::IsNullOrWhiteSpace($methodText) -or $methodText -notmatch 'RuntimeScale') {
        Add-InvariantFailure -Name 'runtime-scale' -File $scaleRel -Message "$method does not use RuntimeScale."
    }
}

foreach ($scaleConsumer in @('PlayerScaleGlobalItem.cs', 'PlayerScaleGlobalProjectile.cs')) {
    $consumerText = Get-Content -LiteralPath (Join-Path $Root $scaleConsumer) -Raw
    if ($consumerText -notmatch 'RuntimeScale') {
        Add-InvariantFailure -Name 'runtime-scale' -File $scaleConsumer -Message 'Multiplayer-safe RuntimeScale is not used.'
    }
}

# Regression invariant 3: client UI hooks execute for multiplayer clients too; companion-only UI
# must not update or register interface layers when synthetic companions are disabled.
$uiRel = 'Companion\UI\CompanionUISystem.cs'
$uiPath = Join-Path $Root $uiRel
foreach ($method in @('UpdateUI', 'ModifyInterfaceLayers')) {
    $methodText = Get-MethodText -Path $uiPath -MethodName $method
    if ([string]::IsNullOrWhiteSpace($methodText) -or $methodText -notmatch 'CanRunCompanionUI') {
        Add-InvariantFailure -Name 'multiplayer-ui-gate' -File $uiRel -Message "$method is not gated by CanRunCompanionUI."
    }
}

Write-Host '=== MoreAIPlayer multiplayer-safety audit ==='
Write-Host ("Scanned {0} source files." -f $files.Count)
Write-Host ("Authoritative operations reviewed: {0}." -f $authoritativeCount)
Write-Host ("Main.LocalPlayer reads listed for manual client-context review: {0}." -f $informationalCount)
Write-Host ''

if ($failures.Count -eq 0) {
    Write-Host 'PASS: authoritative operations are locally guarded and multiplayer invariants hold.' -ForegroundColor Green
    exit 0
}

Write-Host ("FAIL: {0} multiplayer-safety issue(s) require review:" -f $failures.Count) -ForegroundColor Red
$failures | Sort-Object File, Line | Format-Table -AutoSize Category, File, Line, Text
exit 1
