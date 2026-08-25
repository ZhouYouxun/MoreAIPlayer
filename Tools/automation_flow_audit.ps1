<#
.SYNOPSIS
    Static closed-loop audit for the birth-to-Moon-Lord automation scheduler.

.DESCRIPTION
    Checks that every declared goal has an explicit executor and progress metric, that planner
    goals cannot fall through to silent Idle, and that the project's key deadlock/recovery
    invariants remain present. This is a source regression check, not live gameplay proof.
#>

param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$auto = Join-Path $Root 'Companion\Automation'
$failures = New-Object System.Collections.Generic.List[string]

function Read-Raw([string]$relative) {
    return Get-Content -LiteralPath (Join-Path $Root $relative) -Raw
}

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { $script:failures.Add($message) }
}

$goalSource = Read-Raw 'Companion\Automation\AutoGoalId.cs'
$enumMatch = [regex]::Match($goalSource, 'public\s+enum\s+AutoGoalId\s*\{(?<body>[\s\S]*?)\n\}', 'Singleline')
Require $enumMatch.Success 'AutoGoalId enum could not be parsed.'
$goals = @()
if ($enumMatch.Success) {
    foreach ($match in [regex]::Matches($enumMatch.Groups['body'].Value,
        '(?m)^\s*(?<name>[A-Z][A-Za-z0-9_]*)\s*,?\s*$')) {
        $goals += $match.Groups['name'].Value
    }
}

$brain = Read-Raw 'Companion\Automation\AutoCompanionBrain.cs'
$runtime = Read-Raw 'Companion\Automation\AutoTaskRuntime.cs'
$planner = Read-Raw 'Companion\Automation\AutoProgressionPlanner.cs'
$watchdog = Read-Raw 'Companion\Automation\AutoWatchdog.cs'
$state = Read-Raw 'Companion\Automation\AutoCompanionState.cs'
$resource = Read-Raw 'Companion\Automation\AutoResourceController.cs'
$arena = Read-Raw 'Companion\Automation\AutoArenaController.cs'
$campaign = Read-Raw 'Companion\Automation\AutoCampaignController.cs'
$weaponStrategy = Read-Raw 'Companion\Automation\AutoVanillaWeaponStrategy.cs'
$weaponRoutes = Read-Raw 'Companion\Automation\AutoVanillaWeaponRouteController.cs'
$worldIndex = Read-Raw 'Companion\Automation\AutoWorldIndexSystem.cs'
$background = Read-Raw 'Companion\Automation\AutoBackgroundController.cs'
$selfRescue = Read-Raw 'Companion\Automation\AutoSelfRescueController.cs'
$navigator = Read-Raw 'Companion\AI\CompanionNavigator.cs'
$entitySystem = Read-Raw 'Companion\CompanionEntitySystem.cs'
$instance = Read-Raw 'Companion\CompanionInstance.cs'
$profile = Read-Raw 'Companion\CompanionProfile.cs'
$player = Read-Raw 'Companion\CompanionPlayer.cs'
$book = Read-Raw 'Companion\Content\CompanionAutomationBook.cs'

$executed = @([regex]::Matches($brain, 'case\s+AutoGoalId\.(?<name>\w+)\s*:') |
    ForEach-Object { $_.Groups['name'].Value } | Sort-Object -Unique)
$measured = @([regex]::Matches($runtime, 'case\s+AutoGoalId\.(?<name>\w+)\s*:') |
    ForEach-Object { $_.Groups['name'].Value } | Sort-Object -Unique)
$planned = @([regex]::Matches($planner, 'AutoGoalId\.(?<name>\w+)') |
    ForEach-Object { $_.Groups['name'].Value } | Sort-Object -Unique)

foreach ($goal in $goals) {
    Require ($executed -contains $goal) "Goal '$goal' has no explicit ExecutePlan case."
    Require ($measured -contains $goal) "Goal '$goal' has no explicit progress-signature case."
}
foreach ($goal in $planned) {
    Require ($goals -contains $goal) "Planner references undeclared goal '$goal'."
    Require ($executed -contains $goal) "Planner goal '$goal' has no executor."
}

$stageSource = Read-Raw 'Companion\Automation\AutoCampaignStage.cs'
$stageMatch = [regex]::Match($stageSource,
    'public\s+enum\s+AutoCampaignStage\s*:\s*byte\s*\{(?<body>[\s\S]*?)\n\}', 'Singleline')
Require $stageMatch.Success 'AutoCampaignStage enum could not be parsed.'
$stages = @()
if ($stageMatch.Success) {
    foreach ($match in [regex]::Matches($stageMatch.Groups['body'].Value,
        '(?m)^\s*(?<name>[A-Z][A-Za-z0-9_]*)\s*,?\s*$')) {
        $stages += $match.Groups['name'].Value
    }
}
foreach ($stage in $stages) {
    Require ($campaign -match "AutoCampaignStage\.$([regex]::Escape($stage))") `
        "Campaign stage '$stage' is not represented in the milestone provider."
}
foreach ($combatStage in $stages | Where-Object { $_ -notin @('Foundation', 'Completed') }) {
    Require ($campaign -match "AutoCampaignStage\.$([regex]::Escape($combatStage))[\s\S]{0,100}NPCID\.") `
        "Combat stage '$combatStage' has no nearby target-NPC mapping."
}

$routeMatch = [regex]::Match($weaponStrategy,
    'public\s+enum\s+AutoWeaponRouteKind\s*:\s*byte\s*\{(?<body>[\s\S]*?)\n\}', 'Singleline')
Require $routeMatch.Success 'AutoWeaponRouteKind enum could not be parsed.'
if ($routeMatch.Success) {
    foreach ($match in [regex]::Matches($routeMatch.Groups['body'].Value,
        '(?m)^\s*(?<name>[A-Z][A-Za-z0-9_]*)\s*,?\s*$')) {
        $route = $match.Groups['name'].Value
        Require ($weaponRoutes -match "AutoWeaponRouteKind\.$([regex]::Escape($route))") `
            "Weapon route '$route' has no explicit executor mapping."
    }
}

Require ($brain -match 'default:\s*\r?\n\s*navigator\.Idle\(ctx\.Me\);\s*\r?\n\s*return AutoTaskResult\.Faulted;') `
    'Unknown future goals do not fail closed in ExecutePlan.'
Require ($brain -match 'watchdog\.OnBackoff\(plan\)[\s\S]{0,180}background\.Maintain\(ctx, plan\)') `
    'Watchdog backoff no longer dispatches bounded background work.'
# Live role is a one-way latch. Persistent Running may be cleared on completion or repaired after
# load, but neither transition is allowed to route this same slot through the ordinary escort AI.
Require ($entitySystem -match 'if\s*\(instance\.IsAutoIdentity\)\s*\{[\s\S]{0,420}instance\.AutoBrain\.Update') `
    'Live automation dispatch no longer uses the latched instance identity.'
Require ($entitySystem -notmatch 'if\s*\(profile\.IsAutoRunning\)\s*\r?\n\s*instance\.AutoBrain\.Update') `
    'Live automation can still fall through to ordinary AI when the persistent Running flag changes.'
Require ($instance -match 'if\s*\(profile\.IsAutoRunning\)\s*\r?\n\s*IsAutoIdentity\s*=\s*true') `
    'Automation identity is not promoted into a one-way live latch.'
Require ($instance -notmatch 'IsAutoIdentity\s*=\s*profile\.IsAutoRunning') `
    'BindProfile still demotes the live automation identity from persistent state.'
Require ($brain -match 'MarkAutomationCompletedForCurrentWorld\(\)[\s\S]{0,800}instance\.DismissRequested\s*=\s*true') `
    'Moon Lord completion does not retire the live automation entity safely.'
Require ($book -match 'profile\.EnsureAutomationRunning\(\)[\s\S]{0,420}CompanionEntitySystem\.Summon') `
    'The Automation Tome does not repair an unfinished binding before summon.'
Require ($profile -match 'void\s+EnsureAutomationRunning\(\)[\s\S]{0,220}AutoState\.Running\s*=\s*true') `
    'Unfinished automation save repair is missing.'
Require ($entitySystem -match 'profile\.IsAutomationProfile\s*&&\s*!profile\.IsAutoRunning') `
    'Completed automation profiles can still enter the generic summon path.'
Require ($profile -match 'bool\s+IsAutomationProfile\s*=>\s*Origin\s*==\s*CompanionOrigin\.Automation') `
    'Permanent Automation Tome profile ownership is missing.'
Require ($player -match 'RepairAutomationBindingsAfterLoad\(\)[\s\S]{0,1400}EnsureAutomationRunning\(\)[\s\S]{0,300}EnsureAutomationCompleted\(\)') `
    'World bindings no longer restore automation ownership after profile recovery.'
Require ($state -match 'Enum\.ToObject\(typeof\(T\), value\)[\s\S]{0,160}Enum\.IsDefined\(typeof\(T\), candidate\)') `
    'Byte-backed automation enums can regress into whole-profile load failure.'
Require ($watchdog -match 'MaxRetryAttemptsBeforeAlternative\s*=\s*[1-9]') `
    'RetryLater escalation limit is missing or zero.'
Require ($planner -match 'NeedsDepositBefore\(ctx, plan\)') `
    'Full-backpack planning is not output-capacity aware.'
Require ($runtime -match 'case AutoGoalId\.BuildShelter:\s*\r?\n\s*return ShelterProgressSignature\(ctx\);') `
    'Shelter build no longer uses its multi-material progress signature.'
Require ($resource -match 'TileID\.AshGrass' -and $resource -match 'TileID\.SnowBlock') `
    'Vanilla ash/snow tree replant ground support is incomplete.'
Require ($worldIndex -match 'List<Point>\s+TreeBases' -and
    $worldIndex -match 'FindNearestTreeBase\(') `
    'Whole-world tree-root indexing is missing.'
Require ($resource -match 'AutoWorldIndexSystem\.FindNearestTreeBase' -and
    $resource -match 'AutoWorldIndexSystem\.ScanComplete[\s\S]{0,180}AutoExplorationController\.Explore') `
    'GatherWood cannot leave an exhausted local forest without watchdog pacing.'
Require ($background -match 'pausedPlan\.Goal is AutoGoalId\.GatherWood[\s\S]{0,400}AutoExplorationController\.Explore') `
    'Resource backoff no longer uses stable outward exploration.'
Require ($background -notmatch 'LastPathFailed\)\s*\r?\n\s*\{\s*\r?\n\s*patrolDirection\s*\*=\s*-1') `
    'Background patrol can still reverse every failed path frame.'
Require ($watchdog -match 'ImmediateAlternativeAttempts\s*=\s*[2-9]' -and
    $watchdog -match 'runtime\.AttemptCount\s*<\s*ImmediateAlternativeAttempts') `
    'One rejected resource target can still escalate immediately to blocked pacing.'
Require ($brain -match 'wanderTarget\s*=\s*me\.Center\s*\+\s*new Vector2' -and
    $brain -match 'navigator\.Navigate\(me, wanderTarget\)') `
    'Watchdog recovery no longer retains an absolute relocation target.'
Require ($brain -notmatch 'navigator\.Navigate\(me, me\.Center \+ new Vector2\(wanderDir') `
    'Watchdog recovery has regressed to a moving relative target.'
Require ($navigator -match 'bool\s+needUp\s*=\s*dy\s*<\s*-24f' -and
    $navigator -notmatch 'bool\s+needUp\s*=\s*dy\s*<\s*-8f') `
    'One-tile path step-ups can still be converted into jump input.'
Require ($navigator -match 'CanWalkUpSingleTile\(' -and
    $navigator -match '!oneTileWalkUp[\s\S]{0,160}NoGroundAhead') `
    'Direct steering can still jump instead of walking onto a clear one-tile step.'
Require ($navigator -match 'HasSafelyPassedGroundWaypoint' -and
    $navigator -match 'me\.velocity\.Y != 0f[\s\S]{0,220}steerDirection = goalDirection') `
    'Path following can still reverse in the air to revisit an obsolete waypoint.'
Require ($planner -match 'StarterWoodTarget\s*=\s*500' -and
    $planner -match 'cap\.Wood < StarterWoodTarget \|\| st\.PendingReplantCount > 0[\s\S]{0,260}MineOre') `
    'Foundation no longer enforces 500 wood, replanting, then initial ore mining.'
Require ($planner -notmatch 'return AutoPlan\.Simple\(AutoGoalId\.GatherGel\);') `
    'Missing early-game gel can still become a mandatory slime-farming plan.'
Require ($resource -match 'targetMet[\s\S]{0,700}PendingReplantCount == 0' -and
    $resource -match 'AutoInventory\.Count\(ctx\.Profile, ItemID\.Acorn\) > 0[\s\S]{0,100}AlternativeRequired') `
    'Starter wood collection can silently abandon an unresolved replant debt.'
Require ($brain -match 'escape\.TryMineBodyObstruction\(ctx\)[\s\S]{0,500}selfRescue\.ObserveAndRecover') `
    'Body-overlap early return bypasses the independent self-rescue observer.'
Require ($brain -match 'selfRescue\.TryContinueRecovery\(ctx, escape\)[\s\S]{0,180}CapturePersistentMemory') `
    'A pending self-rescue no longer owns the tick before ordinary plan execution.'
Require ($selfRescue -match 'IsStationary\(' -and $selfRescue -match 'IsOscillating\(' -and
    $selfRescue -match 'ticksWithoutRealProgress') `
    'Spatial self-check no longer distinguishes stationary deadlock and small-area oscillation.'
foreach ($action in @('MineEscape', 'ChangeRoute', 'ReturnHome', 'ForceRecall')) {
    Require ($selfRescue -match "AutoSelfRescueAction\.$action") `
        "Self-rescue ladder action '$action' is missing."
}
Require ($selfRescue -match 'ReturnNoApproachTicks' -and
    $selfRescue -match 'ReturnRouteFailed[\s\S]{0,300}ForceRecall') `
    'Physical checkpoint return has no bounded forced-recall fallback.'
Require ($selfRescue -match 'RejectActiveTarget' -and $selfRescue -match 'StrategyVariant\+\+' -and
    $selfRescue -match 'ExplorationDirection') `
    'Route recovery no longer rejects the bad target and changes both route and strategy.'
Require ($selfRescue -match 'signature != lastSignature \|\| progressSerial != lastProgressSerial') `
    'Self-rescue ladder can be reset by raw movement instead of real task/world progress.'
foreach ($locale in Get-ChildItem -LiteralPath (Join-Path $Root 'Localization') -Filter '*.hjson') {
    $localeText = Get-Content -LiteralPath $locale.FullName -Raw
    Require ($localeText -match 'AutomationSelfRescue:' -and
        $localeText -match 'AutomationSelfRescueReason:' -and
        $localeText -match 'AutomationSelfRescueAction:') `
        "Self-rescue chat localization is incomplete in '$($locale.Name)'."
}
Require ($arena -match 'WallOfFlesh\s*=>\s*1000' -and
    $arena -match 'WallOfFlesh or AutoCampaignStage\.Plantera or AutoCampaignStage\.Golem') `
    'Long Wall-of-Flesh bridge or underground Golem excavation redundancy regressed.'
foreach ($field in @('HousingRoomsCompleted', 'pendingReplantSites', 'HasDeathReturnTarget',
    'DeathReturnTarget', 'StageFailureCount', 'StrategyVariant')) {
    Require ($state -match [regex]::Escape($field)) "Persistent recovery field '$field' is missing."
}

if ($failures.Count -gt 0) {
    Write-Host "Automation flow audit FAILED ($($failures.Count))" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host " - $failure" }
    exit 1
}

Write-Host "Automation flow audit passed: $($goals.Count) goals, $($stages.Count) campaign stages, $($planned.Count) planner references, explicit execution/progress coverage, recovery invariants present."
exit 0
