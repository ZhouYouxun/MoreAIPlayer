namespace MoreAIPlayer.Companion.AI;

/// <summary>
/// Why the companion is or is not attacking this tick. Recorded by the brain every update and
/// shown live in the backpack panel.
///
/// This exists because "the companion just stands there" has been reported several times with
/// half a dozen different root causes behind it, each indistinguishable from the others in
/// game: no target, no weapon selected, out of range, a mode suppressing combat, or the item
/// use already spent elsewhere in the same tick all look identical from outside. Guessing
/// between them cost several rounds of trial and error, so the brain now says which one it is.
/// </summary>
public enum CompanionAttackState
{
    /// <summary>Nothing hostile worth engaging is in range.</summary>
    NoTarget,

    /// <summary>A target exists but no slot held anything usable against it.</summary>
    NoWeapon,

    /// <summary>The current mode deliberately suppresses fighting (catching, fishing).</summary>
    ModeSuppressed,

    /// <summary>A posted companion refused a target it could not reach without leaving its anchor.</summary>
    HoldLeashed,

    /// <summary>Target is beyond the chosen weapon's usable range; the companion is closing in.</summary>
    Approaching,

    /// <summary>Terrain blocks the shot and the target is not close enough to justify firing anyway.</summary>
    NoLineOfSight,

    /// <summary>Rogue stealth mode: holding fire until the stealth bar is full.</summary>
    StealthCharging,

    /// <summary>Something else already spent this tick's single item use (summon upkeep, mining…).</summary>
    UseAlreadySpent,

    /// <summary>The weapon slot could not be taken because another animation is still running.</summary>
    AnimationBusy,

    /// <summary>Actually attacking.</summary>
    Attacking
}

public static class CompanionAttackStateText
{
    public static string Describe(CompanionAttackState state) => state switch
    {
        CompanionAttackState.NoTarget => "无目标",
        CompanionAttackState.NoWeapon => "未选中武器（检查武器栏）",
        CompanionAttackState.ModeSuppressed => "当前模式暂不作战",
        CompanionAttackState.HoldLeashed => "驻守中，目标超出警戒范围",
        CompanionAttackState.Approaching => "接近目标中",
        CompanionAttackState.NoLineOfSight => "视线被地形阻挡",
        CompanionAttackState.StealthCharging => "潜伏中，等待隐身充能",
        CompanionAttackState.UseAlreadySpent => "本帧攻击被其他动作占用",
        CompanionAttackState.AnimationBusy => "上一次挥动尚未结束",
        _ => "攻击中"
    };
}
