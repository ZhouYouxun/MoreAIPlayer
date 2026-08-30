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
$portfolio = Read-Raw 'Companion\Automation\AutoPlanPortfolio.cs'
$watchdog = Read-Raw 'Companion\Automation\AutoWatchdog.cs'
$state = Read-Raw 'Companion\Automation\AutoCompanionState.cs'
$resource = Read-Raw 'Companion\Automation\AutoResourceController.cs'
$arena = Read-Raw 'Companion\Automation\AutoArenaController.cs'
$campaign = Read-Raw 'Companion\Automation\AutoCampaignController.cs'
$weaponStrategy = Read-Raw 'Companion\Automation\AutoVanillaWeaponStrategy.cs'
$weaponRoutes = Read-Raw 'Companion\Automation\AutoVanillaWeaponRouteController.cs'
$worldIndex = Read-Raw 'Companion\Automation\AutoWorldIndexSystem.cs'
$worldKnowledge = Read-Raw 'Companion\Automation\AutoWorldKnowledge.cs'
$background = Read-Raw 'Companion\Automation\AutoBackgroundController.cs'
$selfRescue = Read-Raw 'Companion\Automation\AutoSelfRescueController.cs'
$navigator = Read-Raw 'Companion\AI\CompanionNavigator.cs'
$entitySystem = Read-Raw 'Companion\CompanionEntitySystem.cs'
$instance = Read-Raw 'Companion\CompanionInstance.cs'
$profile = Read-Raw 'Companion\CompanionProfile.cs'
$player = Read-Raw 'Companion\CompanionPlayer.cs'
$book = Read-Raw 'Companion\Content\CompanionAutomationBook.cs'
$bootstrap = Read-Raw 'Companion\Automation\AutoWorldBootstrapController.cs'
$spawnResolver = Read-Raw 'Companion\Automation\AutoSpawnResolver.cs'
$loot = Read-Raw 'Companion\Automation\AutoLootController.cs'
$chestProvenance = Read-Raw 'Companion\Automation\AutoChestProvenanceSystem.cs'
$crackDraw = Read-Raw 'Companion\Automation\AutoMiningCrackDrawSystem.cs'
$crafting = Read-Raw 'Companion\Automation\AutoCraftingService.cs'
$inventory = Read-Raw 'Companion\Automation\AutoInventory.cs'
$station = Read-Raw 'Companion\Automation\AutoStationController.cs'
$storage = Read-Raw 'Companion\Automation\AutoStorageController.cs'
$gear = Read-Raw 'Companion\Automation\AutoGearController.cs'
$campaignGear = Read-Raw 'Companion\Automation\AutoCampaignGearController.cs'
$budget = Read-Raw 'Companion\Automation\AutoResourceBudget.cs'
$shelter = Read-Raw 'Companion\Automation\AutoShelterController.cs'
$worldSession = Read-Raw 'Companion\Automation\AutoWorldSessionSystem.cs'
$encounterTrigger = Read-Raw 'Companion\Automation\AutoEncounterTriggerController.cs'
$encounterReadiness = Read-Raw 'Companion\Automation\AutoEncounterReadiness.cs'
$planSource = Read-Raw 'Companion\Automation\AutoPlan.cs'
$boss = Read-Raw 'Companion\Automation\AutoBossController.cs'
$topology = Read-Raw 'Companion\Automation\AutoWorldTopology.cs'
$combatPhone = Read-Raw 'Companion\Automation\AutoCombatPhoneService.cs'
$weaponHunt = Read-Raw 'Companion\Automation\AutoWeaponDropHuntController.cs'
$survival = Read-Raw 'Companion\Automation\AutoWorldSurvivalController.cs'
$recall = Read-Raw 'Companion\Automation\AutoRecallController.cs'
$recovery = Read-Raw 'Companion\Automation\AutoRecoveryController.cs'
$phoneWorld = Read-Raw 'Companion\Automation\AutoPhoneWorldService.cs'
$worldTravel = Read-Raw 'Companion\Automation\AutoWorldTravelController.cs'
$fieldcraft = Read-Raw 'Companion\Automation\AutoExpeditionFieldcraftController.cs'
$threat = Read-Raw 'Companion\Automation\AutoThreatController.cs'
$combat = Read-Raw 'Companion\Automation\AutoCombatController.cs'
$starterArsenal = Read-Raw 'Companion\Automation\AutoStarterArsenalController.cs'
$damagePolicy = Read-Raw 'Companion\Automation\AutoCompanionDamagePolicy.cs'
$actuator = Read-Raw 'Companion\Automation\AutoActuator.cs'
$globalItem = Read-Raw 'Companion\CompanionGlobalItem.cs'
$managerPanel = Read-Raw 'Companion\UI\CompanionManagerPanel.cs'
$inventoryPanel = Read-Raw 'Companion\UI\CompanionInventoryUI.cs'
$itemSlot = Read-Raw 'Companion\UI\CompanionItemSlot.cs'
$uiSystem = Read-Raw 'Companion\UI\CompanionUISystem.cs'

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
Require ($brain -match 'AutoWorldBootstrapController\.Reconcile\(ctx\)[\s\S]{0,900}CanCompleteAutomation\(ctx\)' -and
    $brain -notmatch 'if\s*\(NPC\.downedMoonlord\)' -and
    $campaign -match 'NPC\.downedMoonlord\s*&&\s*AutoEncounterReadiness\.TerminalWorldCapabilityReady\(ctx\)') `
    'An imported completed world can still dismiss a zero-start automation profile before bootstrap.'
Require ($book -match 'profile\.EnsureAutomationRunning\(\)[\s\S]{0,900}CompanionEntitySystem\.Summon') `
    'The Automation Tome does not repair an unfinished binding before summon.'
Require ($book -match 'AutoSpawnResolver\.Resolve\(player, profile\.AutoState\)' -and
    $spawnResolver -match 'TryFindStandingPoint\(' -and
    $spawnResolver -notmatch 'Main\.MouseWorld') `
    'Automation spawn can still be driven by observer cursor state or skip terrain validation.'
Require ($instance -match 'if\s*\(!IsAutoIdentity\)\s*\r?\n\s*MirrorOwnerBeneficialBuffs') `
    'Automation can still inherit observer buffs, including mount or flight state.'
Require ($profile -match 'void\s+EnsureAutomationRunning\(\)[\s\S]{0,220}AutoState\.Running\s*=\s*true') `
    'Unfinished automation save repair is missing.'
Require ($entitySystem -match 'profile\.IsAutomationProfile\s*&&\s*!profile\.IsAutoRunning') `
    'Completed automation profiles can still enter the generic summon path.'
Require ($profile -match 'bool\s+IsAutomationProfile\s*=>\s*Origin\s*==\s*CompanionOrigin\.Automation') `
    'Permanent Automation Tome profile ownership is missing.'
Require ($player -match 'RepairAutomationBindingsAfterLoad\(\)[\s\S]{0,1400}EnsureAutomationRunning\(\)[\s\S]{0,300}EnsureAutomationCompleted\(\)') `
    'World bindings no longer restore automation ownership after profile recovery.'
Require ($player -match 'UniqueId != System\.Guid\.Empty' -and
    $player -match 'legacy:\{Main\.worldID\}:\{path\}' -and
    $player -match 'binding\.Completed\s*=\s*false;[\s\S]{0,500}unfinishedProfileIds') `
    'Legacy-world identity fallback or false-completion save repair is missing.'
Require ($state -match 'Enum\.ToObject\(typeof\(T\), value\)[\s\S]{0,160}Enum\.IsDefined\(typeof\(T\), candidate\)') `
    'Byte-backed automation enums can regress into whole-profile load failure.'
Require ($watchdog -match 'MaxRetryAttemptsBeforeAlternative\s*=\s*[1-9]') `
    'RetryLater escalation limit is missing or zero.'
Require ($planner -match 'AddInventoryCandidate\(ctx\)' -and
    $planner -match 'NeedsDepositBefore\(ctx, selected\)' -and
    $planner -match 'AutoInventory\.HasBackpackSpace\(ctx\.Profile\)[\s\S]{0,260}AutoGoalId\.DepositSurplus') `
    'Full-backpack planning is not represented as a parallel inventory-space need.'
Require ($runtime -match 'case AutoGoalId\.BuildShelter:\s*\r?\n\s*return ShelterProgressSignature\(ctx\);') `
    'Shelter build no longer uses its multi-material progress signature.'
Require ($resource -match 'TileID\.AshGrass' -and $resource -match 'TileID\.SnowBlock') `
    'Vanilla ash/snow tree replant ground support is incomplete.'
Require ($worldIndex -match 'List<Point>\s+TreeBases' -and
    $worldIndex -match 'FindNearestTreeBase\(') `
    'Whole-world tree-root indexing is missing.'
Require ($worldKnowledge -match 'MinimumMainTrunkRun\s*=\s*[3-9]' -and
    $worldKnowledge -match 'TryResolveMainTreeRoot\(' -and
    $worldKnowledge -match 'MeasureVerticalTrunkRun\(' -and
    $worldKnowledge -match 'IsGroundedAxeTile\(' -and
    $worldKnowledge -match 'doubledCenter\s*=\s*left\s*\+\s*right' -and
    $resource -match 'TryResolveMainTreeRoot\(treeTile, out Point canonicalRoot\)' -and
    ([regex]::Matches($resource, 'canonicalRoot != treeTile').Count -ge 2)) `
    'Tree selection can regress from the grounded main-trunk center to a nearer side root.'
Require ($resource -match 'AutoWorldIndexSystem\.FindNearestTreeBase' -and
    $resource -match 'AutoWorldIndexSystem\.ScanComplete[\s\S]{0,180}AutoExplorationController\.Explore') `
    'GatherWood cannot leave an exhausted local forest without watchdog pacing.'
Require ($background -match 'AutoExplorationController\.Explore\(ctx\)' -and
    $background -notmatch 'patrolDirection\s*\*=\s*-1') `
    'Backoff work can regress to local pacing instead of stable outward exploration.'
Require ($background -notmatch 'LastPathFailed\)\s*\r?\n\s*\{\s*\r?\n\s*patrolDirection\s*\*=\s*-1') `
    'Background patrol can still reverse every failed path frame.'
Require ($watchdog -match 'ImmediateAlternativeAttempts\s*=\s*[2-9]' -and
    $watchdog -match 'runtime\.AttemptCount\s*<\s*ImmediateAlternativeAttempts') `
    'One rejected resource target can still escalate immediately to blocked pacing.'
Require ($brain -match 'Every route failure advances a persistent world frontier' -and
    $brain -match 'AutoExplorationController\.Explore\(ctx\)' -and
    $brain -notmatch 'void\s+Wander\(') `
    'Watchdog recovery can regress to local two-point wandering.'
Require ($navigator -match 'bool\s+needUp\s*=\s*dy\s*<\s*-24f' -and
    $navigator -notmatch 'bool\s+needUp\s*=\s*dy\s*<\s*-8f') `
    'One-tile path step-ups can still be converted into jump input.'
Require ($navigator -match 'CanWalkUpSingleTile\(' -and
    $navigator -match '!oneTileWalkUp[\s\S]{0,160}NoGroundAhead') `
    'Direct steering can still jump instead of walking onto a clear one-tile step.'
Require ($navigator -match 'HasSafelyPassedGroundWaypoint' -and
    $navigator -match 'me\.velocity\.Y != 0f[\s\S]{0,220}steerDirection = goalDirection') `
    'Path following can still reverse in the air to revisit an obsolete waypoint.'
Require ($planner -match 'AutoPlanPortfolio' -and
    $planner -match 'AddFoundationCandidates\(ctx\)' -and
    $planner -match 'Portfolio\.Add\(AutoPlan\.Gather\(AutoGoalId\.GatherWood' -and
    $planner -match 'Portfolio\.Add\(AutoPlan\.Gather\(AutoGoalId\.MineOre' -and
    $planner -notmatch 'StarterWoodTarget') `
    'Foundation can regress from parallel providers to a prescribed opening sequence.'
Require ($campaign -match 'DetermineWorldStage\(' -and
    $campaign -match 'NeedsPersonalFoundation\(ctx\)' -and
    $planner -match 'if\s*\(needsPersonalFoundation\)[\s\S]{0,100}AddFoundationCandidates\(ctx\)' -and
    $planner -match 'CampaignStage is not AutoCampaignStage\.Foundation[\s\S]{0,100}AddCampaignCandidates\(ctx\)') `
    'Established-world progress and the automation player personal bootstrap are no longer parallel facts.'
Require ($planner -match 'CampaignStage == AutoCampaignStage\.Completed\s*&&[\s\S]{0,120}CanCompleteAutomation\(ctx\)' -and
    $planner -notmatch 'if\s*\(ctx\.State\.CampaignStage == AutoCampaignStage\.Completed\)\s*\r?\n\s*return AutoPlan\.Idle') `
    'A completed imported world can still suppress the personal foundation portfolio.'
Require ($brain -match 'AutoWorldBootstrapController\.Reconcile\(ctx\)' -and
    $bootstrap -match 'ForgetReviewedChestsNear' -and
    $bootstrap -match 'AutoWorldIndexSystem\.RestartScan\(\)' -and
    $loot -match 'BootstrapSearchRadiusTiles') `
    'Summon-time world/inventory/chest reconciliation is incomplete.'
Require ($bootstrap -match 'InitializeHousingPlan\(state, establishedWorld\)' -and
    $bootstrap -match 'HasNearbyStockedChest\(me\)' -and
    $budget -match 'StartedInEstablishedWorld[\s\S]{0,500}MinimumRooms' -and
    $shelter -match 'RequiredHousingRooms\(state\)') `
    'Established-world housing is no longer isolated from the imported world NPC population.'
Require ($campaignGear -match 'WorldHasSeededHardmodeOre\(ctx\)' -and
    $campaignGear -match 'WorldGen\.SavedOreTiers\.Cobalt' -and
    $campaignGear -match 'ctx\.State\.AltarsBroken\s*=\s*3') `
    'An imported Hardmode world can regress into unnecessary personal altar seeding.'
foreach ($controller in @('AutoCampaignController', 'AutoEncounterTriggerController',
    'AutoArenaController', 'AutoPhoneWorldService', 'AutoResourceBudget')) {
    Require ($worldSession -match "$controller\.ResetWorldSession\(\)") `
        "Per-world transient reset is missing for '$controller'."
}
Require ($crackDraw -match 'DrawTileCracks\(1, player\.hitTile\)' -and
    $crackDraw -match 'player\.hitReplace') `
    'Synthetic mining damage is not routed into the vanilla crack renderer.'
Require ($planner -notmatch 'return AutoPlan\.Simple\(AutoGoalId\.GatherGel\);') `
    'Missing early-game gel can still become a mandatory slime-farming plan.'
Require ($resource -match 'if\s*\(targetMet\)\s*\r?\n\s*return AutoTaskResult\.Succeeded;' -and
    $resource -notmatch 'targetMet[\s\S]{0,500}ServicePendingReplant') `
    'Starter wood completion can still be held hostage by the replant side task.'
Require ($resource -match 'SpawnReplantExclusionTiles\s*=\s*100' -and
    $resource -match 'MinimumTreeSpacingTiles\s*=\s*5' -and
    $resource -match 'MaximumReplantUseAttempts\s*=\s*3' -and
    $resource -match 'FailedReplantCooldownTicks\s*=\s*[1-9][0-9]{2,}' -and
    $resource -notmatch 'ServicePendingReplant\(AutoContext ctx, bool allowLongTravel') `
    'Replanting is no longer a bounded, spaced, spawn-excluded side task.'
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
    $selfRescue -match 'ReturnRouteFailed[\s\S]{0,300}ForceRecall' -and
    $selfRescue -match 'AutoRecallController\.TryUseOwnedRecall') `
    'Physical checkpoint return has no bounded legal-recall fallback.'
Require ($selfRescue -match 'RejectActiveTarget' -and $selfRescue -match 'StrategyVariant\+\+' -and
    $selfRescue -match 'ExplorationDirection') `
    'Route recovery no longer rejects the bad target and changes both route and strategy.'
Require ($selfRescue -match '!planChanged && signature != lastSignature' -and
    $selfRescue -match 'progressSerial != lastProgressSerial' -and
    $selfRescue -match 'A portfolio switch is not progress' -and
    $selfRescue -notmatch 'if\s*\(observedPlanKey != plan\.StableKey\)') `
    'A portfolio switch can still reset physical deadlock detection without real progress.'
Require ($selfRescue -match 'MaximumMeaninglessIdleTicks\s*=\s*60' -and
    $selfRescue -match 'RecoverMeaninglessIdle\(ctx, plan\)' -and
    $selfRescue -match 'PurposefullyApproached\(ctx\)' -and
    $selfRescue -notmatch 'MovedSinceMeaninglessIdleAnchor' -and
    $selfRescue -match 'RecoverMeaninglessIdle[\s\S]{0,500}ChangeRoute\(ctx, plan[\s\S]{0,220}AutoExplorationController\.Explore\(ctx\)') `
    'The one-second meaningless-idle breaker no longer changes route in the same frame.'
Require ($crafting -match 'TryCraftUntil\(' -and
    $crafting -match 'MaximumCraftsPerTransaction\s*=\s*[1-9]' -and
    $crafting -match 'for \(int operation = 0; operation < MaximumCraftsPerTransaction' -and
    $crafting -match 'AutoInventory\.CanFit\(profile, result\)' -and
    $crafting -match 'RecipeCost\(candidate, profile\)' -and
    $crafting -match 'KeepThreshold\(required\.type, profile\.AutoState\)' -and
    $crafting -notmatch 'controlLeft|controlRight|Navigator\.Idle|Thread\.Sleep|Task\.Delay|itemAnimation|[Cc]raft(Cooldown|Delay|Wait)') `
    'Instant batch crafting can regress into per-frame or movement-blocking work.'
Require ($station -match 'TryCraft\(ctx\.Instance, ctx\.Profile, itemType\)[\s\S]{0,900}FindAndBindInventoryIndex[\s\S]{0,500}MoveAndPlace' -and
    $shelter -match 'bool made = AutoCraftingService\.TryCraft[\s\S]{0,500}FindAndBindInventoryIndex[\s\S]{0,180}AutoTaskResult\.Succeeded' -and
    $storage -match 'TryCraft\(ctx\.Instance, ctx\.Profile, ItemID\.Chest\)[\s\S]{0,900}FindAndBindInventoryIndex[\s\S]{0,500}MoveAndPlace' -and
    $inventory -match 'FindAndBindInventoryIndex\([\s\S]{0,700}BackpackToInventoryIndex') `
    'A newly crafted placeable can regain an artificial presentation frame before physical use.'

Require ($chestProvenance -match 'PostWorldGen\(\)[\s\S]{0,400}CaptureGeneratedChests\(\)' -and
    $chestProvenance -match 'SaveWorldData\(TagCompound tag\)' -and
    $chestProvenance -match 'LoadWorldData\(TagCompound tag\)' -and
    $chestProvenance -match 'No marker means this is an old/foreign save' -and
    $chestProvenance -match 'IsWorldGeneratedChest\(Chest chest\)' -and
    $chestProvenance -match 'IsAutomationOwnedChest\(Chest chest\)' -and
    $chestProvenance -match 'override void PlaceInWorld' -and
    $chestProvenance -match 'override void RightClick') `
    'The persistent fail-closed chest provenance ledger is incomplete.'
Require (([regex]::Matches($loot, 'AutoChestProvenanceSystem\.IsWorldGeneratedChest').Count -ge 3) -and
    $loot -match 'Re-prove provenance immediately before moving the first item') `
    'Chest looting is not provenance-gated at discovery, target validation and final transfer.'
Require ($storage -match 'FindNearbyAutomationChestId' -and
    $storage -notmatch 'FindNearbyChestId' -and
    ([regex]::Matches($storage, 'AutoChestProvenanceSystem\.IsAutomationOwnedChest').Count -ge 3) -and
    $bootstrap -match 'AutoChestProvenanceSystem\.IsAutomationOwnedChest' -and
    $shelter -match 'AutoChestProvenanceSystem\.IsAutomationOwnedChest') `
    'Player containers can still be adopted by storage, bootstrap or shelter capture.'

Require ($campaign -notmatch 'SpawnOnPlayer|SpawnWOF|TriggerLunarApocalypse' -and
    $campaignGear -notmatch 'SpawnOnPlayer|SpawnWOF|TriggerLunarApocalypse' -and
    $encounterTrigger -notmatch 'SpawnOnPlayer|SpawnWOF|TriggerLunarApocalypse') `
    'Campaign progression contains a direct boss/event spawn bypass.'
Require ($boss -notmatch 'ApplyFailureInsurance|statDefense\s*\+=|immune\s*=\s*true|statLife\s*=') `
    'Boss failure recovery can still inject defense, immunity or life.'
Require ($arena -notmatch 'ForcePrepareInsurance|ClearTile\(' -and
    $weaponRoutes -notmatch 'ForceExposeOrb|ClearTile\(') `
    'Last-rung recovery can still rewrite blocked terrain instead of rejecting the route.'
Require ($encounterReadiness -match 'ReadyToStart\s*=>\s*VitalityReady\s*&&\s*MiningAndArmourReady\s*&&\s*WeaponReady\s*&&' -and
    $planner -match 'if\s*\(readiness\.ReadyExceptTiming\)' -and
    $campaign -match 'if\s*\(!readiness\.TimingReady\)[\s\S]{0,100}AutoEncounterTriggerController\.Prepare\(ctx\)') `
    'Encounter permission is no longer a strict conjunction of preparation facts.'
Require ($encounterTrigger -match 'SuspiciousLookingEye' -and
    $encounterTrigger -match 'BloodySpine\s*:\s*ItemID\.WormFood' -and
    $encounterTrigger -match 'GuideVoodooDoll' -and
    $encounterTrigger -match 'PlanteraBulb' -and
    $encounterTrigger -match 'LihzahrdPowerCell' -and
    $encounterTrigger -match 'CelestialSigil') `
    'Legal vanilla encounter trigger routes are incomplete.'
Require ($planSource -match 'identityParameter\s*=\s*Goal is AutoGoalId\.ExploreWorld or' -and
    $planSource -match 'AutoGoalId\.AcquireWeaponUpgrade\s*\?\s*Amount\s*:\s*0') `
    'Live gather/craft amounts can still erase stable plan recovery history.'
Require ($runtime -notmatch 'case AutoGoalId\.AcquireWeaponUpgrade:[\s\S]{0,260}ReviewedChestCount' -and
    $runtime -notmatch 'case AutoGoalId\.AcquireWeaponUpgrade:[\s\S]{0,260}ExplorationSegments' -and
    $runtime -match 'case AutoGoalId\.SummonBoss:[\s\S]{0,100}AutoEncounterTriggerController\.ProgressSignature') `
    'Task progress can still be faked by unrelated exploration/chest counters.'
Require ($topology -match 'Main\.remixWorld\s*\|\|\s*Main\.zenithWorld' -and
    $planner -match 'terminalWorldAdaptation' -and
    $arena -match 'NaturalTempleRoomViable') `
    'Inverted-world or natural Golem-room adaptation is missing.'
Require ($combatPhone -match 'assessment\.Confidence' -and
    $combatPhone -match 'DecisionScore\s*=') `
    'Weapon-selection confidence no longer affects the actual comparison score.'
Require ($worldTravel -match 'NavigateToward\(me, waypoint, destination\)' -and
    $navigator -match 'CurrentGoalWorld\s*=\s*purposeWorld') `
    'Long waypoint travel no longer exposes net approach to the one-second purpose detector.'
Require ($weaponRoutes -match 'HuntDrops\s*=>\s*AutoWeaponDropHuntController\.Hunt' -and
    $weaponHunt -match 'FindFeasibleTarget\(ctx\)' -and
    $weaponHunt -match 'AutoThreatController\.TryEngageTarget') `
    'The weapon-drop hunt has regressed to generic exploration without a hunted target.'
Require ($survival -match 'Main\.dontStarveWorld' -and
    $survival -match 'AutoLootController\.LootBestWorldItem' -and
    $planSource -match 'FoodRouteItemMarker' -and
    $planner -match 'AutoPlan\.FoodRoute\(\)') `
    'Constant-world food survival has no legal parallel supply route.'
Require ($recall -match 'UseItemInSlot' -and
    $recall -match 'MagicMirror' -and
    $phoneWorld -notmatch '\.Center\s*=' -and
    $watchdog -notmatch '\.Center\s*=' -and
    $recovery -notmatch '\.Center\s*=' -and
    $selfRescue -notmatch '\.Center\s*=') `
    'A recovery path can still teleport without a real recall item.'
Require ($storage -match 'TryDropJunkWhenFull\(' -and
    $storage -match 'noGrabDelay:\s*true' -and
    $storage -match 'DroppedJunkPickupDelayTicks\s*=\s*[1-9]' -and
    $storage -match 'item\.favorited|item\.questItem' -and
    $gear -notmatch 'DiscardOneObsoleteWeapon') `
    'Full-backpack junk relief can silently delete items or immediately recollect its own drop.'
Require ($portfolio -match 'SwitchMargin\s*=\s*[1-9]' -and
    $portfolio -match 'MinimumHealthyCommitmentTicks\s*=\s*[1-9]' -and
    $portfolio -match 'IsPlanCoolingDown' -and
    $portfolio -match 'CoverageScore' -and
    $portfolio -match 'PortfolioSwitchesWithoutProgress') `
    'Parallel portfolio anti-thrashing or multi-need scoring is incomplete.'
Require ($runtime -match 'lastDiagnosticLogTick' -and
    $runtime -match 'now - lastDiagnosticLogTick >= 15' -and
    $runtime -match 'Logger\?\.Debug' -and
    $runtime -match 'LastKnownPosition\.X / 16f') `
    'Transition diagnostics are no longer preserved in client.log for live pacing reports.'
Require ($managerPanel -match 'CollectQuickEntries\(\)' -and
    $managerPanel -match 'DrawAutomationQuickTab\(' -and
    $managerPanel -match 'automationWidth\s*=\s*174' -and
    $managerPanel -match 'CompanionUISystem\.Open\(slot\)' -and
    $managerPanel -match '!instance\.IsAutoIdentity\)\s*\r?\n\s*entries\.Add\(instance\)') `
    'The automation identity no longer has a thin live-backpack tab separate from the ordinary manager.'
Require ($itemSlot -match 'Action\s+onChanged' -and
    $itemSlot -match 'beforeStack\s*!=\s*after\.stack' -and
    $itemSlot -match 'onChanged\?\.Invoke\(\)' -and
    $inventoryPanel -match 'NotifyInventoryEdited\(' -and
    $inventoryPanel -match 'BackpackContentSignature\(\)\s*!=\s*before' -and
    $inventoryPanel -match 'companion\.AutoBrain\.NotifyExternalInventoryEdit\(\)' -and
    $runtime -match 'void\s+NotifyExternalInventoryEdit\(\)[\s\S]{0,700}CurrentPlanKey\s*=\s*0' -and
    $runtime -match 'NotifyExternalInventoryEdit\(\)[\s\S]{0,900}deferredPlans\.Clear\(\)' -and
    $brain -match 'NotifyExternalInventoryEdit\(\)[\s\S]{0,320}selfRescue\.NotifyExternalInventoryEdit' -and
    $brain -match 'NotifyExternalInventoryEdit\(\)[\s\S]{0,420}fieldcraft\.NotifyExternalInventoryEdit' -and
    $selfRescue -match 'NotifyExternalInventoryEdit\(Vector2 currentPosition\)[\s\S]{0,400}returningToCheckpoint\s*=\s*false') `
    'Live backpack insertion/removal no longer forces immediate automation replanning.'
Require ($uiSystem -match 'BuildStamp\s*=\s*"2026-08-30\s+#53\s+bounded-unarmed-retreat"') `
    'The live diagnostic build stamp was not advanced with bounded unarmed retreat.'
Require ($starterArsenal -match 'DesiredArrowStock\s*=\s*100' -and
    $starterArsenal -match 'ItemID\.WoodenSword' -and
    $starterArsenal -match 'ItemID\.WoodenBow' -and
    $starterArsenal -match 'ItemID\.WoodenArrow' -and
    $starterArsenal -match 'HasCloseRangeWeapon\(profile\).*HasBow\(profile\).*CountArrows\(profile\)' -and
    $starterArsenal -match 'TryCraftUntil\(ctx\.Instance, ctx\.Profile' -and
    $gear -match 'AutoStarterArsenalController\.Ensure\(ctx\)[\s\S]{0,180}starter != AutoTaskResult\.Succeeded' -and
    $planner -match 'AutoStarterArsenalController\.MissingWoodCost' -and
    $planner -match 'AutoStarterArsenalController\.MissingStoneCost' -and
    $planner -match 'if \(bench && cap\.Wood >= starterWood && cap\.Stone >= starterStone\)[\s\S]{0,350}AutoGoalId\.CraftGear') `
    'The fresh profile can regress to fighting before a close weapon, bow and real arrow reserve.'
Require ($combat -match 'AutoStarterArsenalController\.RequirementSatisfied' -and
    $combat -match 'AutoThreatController\.TryEngageTarget\(ctx, target\)' -and
    $combat -notmatch 'AttackTiles' -and
    $combat -notmatch 'BestToolIndex\(ctx\.Me, item => item\.axe\)' -and
    $threat -match 'MeleeApproachTiles\s*=\s*2\.25f' -and
    $threat -match 'MeleeUseTiles\s*=\s*3\.5f' -and
    $threat -match 'else if \(distance > stopRange\)' -and
    $threat -match 'if \(ranged \|\| distance <= useRange\)' -and
    $threat -match 'RetreatFromThreat\(ctx, target, 6f\)' -and
    $threat -notmatch 'ctx\.Me\.Center \+ new Vector2\(away \* 6f') `
    'Ordinary slime combat can regress to stopping outside melee reach or using an axe fallback.'
Require ($damagePolicy -match 'BuiltInDefense\s*=\s*10' -and
    $damagePolicy -match 'IncomingDamageMultiplier\s*=\s*0\.8f' -and
    $damagePolicy -match 'modifiers\.FinalDamage \*= IncomingDamageMultiplier' -and
    $player -match 'AutoCompanionDamagePolicy\.ApplyBuiltInDefense\(Player\)' -and
    $player -match 'override void ModifyHurt\(ref Player\.HurtModifiers modifiers\)' -and
    $player -match '!instance\.IsAutoIdentity && !MoreAIPlayerClientConfig\.CompanionDamageEnabled' -and
    $entitySystem -match 'instance\.IsAutoIdentity \|\| configuredDamageEnabled' -and
    $entitySystem -match 'AutoCompanionDamagePolicy\.ApplyFinalDamageReduction\(companion, ref modifiers\)') `
    'Automation damage can regress to invulnerability or lose its explicit 10 defense/20% reduction.'
Require ($brain -notmatch 'private const int (NormalReviveTicks|BossReviveTicks)' -and
    $brain -match 'instance\.ReviveTicksLeft\s*=\s*0' -and
    $brain -match 'ReviveAtSafeSpot\(me, state\);\s*\r?\n\s*return false;' -and
    $state -match 'MemoryReviveTicks\s*=\s*0' -and
    $state -match 'instance\.ReviveTicksLeft\s*=\s*0') `
    'The automation identity can regain a delayed respawn instead of same-update revival.'
Require ($fieldcraft -match 'TryMaintainUndergroundLighting\(' -and
    $fieldcraft -match 'HasNearbyTorch\(' -and
    $fieldcraft -match 'LightingSuccessCooldownTicks\s*=\s*[1-9]' -and
    $fieldcraft -match 'pendingLightingTile' -and
    $fieldcraft -match 'ResolvePendingLighting\(' -and
    $fieldcraft -match 'TileIsTorch\(' -and
    $fieldcraft -match 'LightingFailureLimit\s*=\s*[1-9]' -and
    $fieldcraft -match 'EnsureReusableTorch\(' -and
    $brain -match 'fieldcraft\.TryMaintainUndergroundLighting\(ctx\)' -and
    $globalItem -match 'override bool ConsumeItem\(Item item, Player player\)[\s\S]{0,300}IsAutoCompanion\(player\)') `
    'Observer-facing underground lighting or the automation-only reusable torch rule is incomplete.'
Require ($fieldcraft -match 'TryBuildVerticalAccess\(' -and
    $fieldcraft -match 'IsRopeItem' -and
    $fieldcraft -match 'ItemID\.WoodPlatform' -and
    $fieldcraft -match 'VerticalFailureLimit\s*=\s*[1-9]' -and
    $fieldcraft -match 'ropeFailures' -and
    $fieldcraft -match 'platformFailures' -and
    $fieldcraft -match 'ropeBackoffUntil' -and
    $fieldcraft -match 'platformBackoffUntil' -and
    $worldTravel -match 'Fieldcraft\.TryBuildVerticalAccess\(ctx, destination\)' -and
    $actuator -match 'AutoBrain\.TryBuildVerticalAccess\(center\)') `
    'Vertical travel no longer prefers bounded rope placement with a platform fallback.'
Require ($threat -match 'FindApproachingOrdinaryHostile\(' -and
    $threat -match 'PrepareWeaponEarly\(' -and
    $threat -match 'IsHighThreat\(' -and
    $threat -match 'CanBeStoppedByBarrier\(' -and
    $threat -match '!target\.noTileCollide\s*&&\s*!target\.noGravity' -and
    $threat -match 'weaponSlot\s*<\s*0\s*\|\|\s*!assessment\.AmmoReady\s*\|\|\s*!assessment\.ManaReady' -and
    $threat -match 'TryRetreatUnarmed\(ctx, target\)' -and
    $fieldcraft -match 'TryBuildThreatBarrier\(' -and
    $fieldcraft -match 'BarrierFailureLimit\s*=\s*[1-9]' -and
    $fieldcraft -match 'barrierBackoffUntil' -and
    $fieldcraft -match 'CountSpendableBarrierBlocks\(' -and
    $fieldcraft -match 'AutoStorageController\.KeepThreshold' -and
    $fieldcraft -match 'RetreatFromThreat\(' -and
    $fieldcraft -match 'retreatGoalUntil') `
    'Approaching-threat weapon preparation or bounded high-threat barricading is incomplete.'
Require ($fieldcraft -match 'MaximumUnarmedRetreatTicks\s*=\s*(?:[1-5]?[0-9]|60)' -and
    $fieldcraft -match 'UnarmedPlannerGraceTicks\s*=\s*[1-9][0-9]{2,}' -and
    $fieldcraft -match 'TryRetreatUnarmed\(' -and
    $fieldcraft -match 'unarmedRetreatGoal' -and
    $fieldcraft -match 'unarmedRetreatStartedTick' -and
    $fieldcraft -match 'unarmedPlannerGraceUntil' -and
    $fieldcraft -match 'return false;[\s\S]{0,260}ctx\.Navigator\.Navigate\(ctx\.Me, unarmedRetreatGoal\)' -and
    $fieldcraft -notmatch 'retreatNpc != threat\.whoAmI \|\| retreatSide != side') `
    'Unarmed threat handling can regain infinite side-flipping before the opening plan executes.'
Require ($planner -match 'AutoWeaponRouteKind\[\]\s+WeaponRoutes' -and
    $planner -match 'IsUpgradeRouteAvailable\(ctx, route\)') `
    'Weapon acquisition alternatives are no longer maintained in parallel.'
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
foreach ($field in @('HousingRoomsCompleted', 'WorldAdaptationInitialized',
    'StartedInEstablishedWorld', 'HousingRoomTarget', 'pendingReplantSites', 'HasDeathReturnTarget',
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
