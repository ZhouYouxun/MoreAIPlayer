using Terraria.ModLoader;

namespace MoreAIPlayer.Companion.Automation;

/// <summary>Clears automation facts which are valid only inside one loaded world. Persistent task,
/// inventory and coordinate memory lives on the per-world profile; controller cooldowns and global
/// forecasts do not, and must never leak when the same character enters another save.</summary>
public sealed class AutoWorldSessionSystem : ModSystem
{
    public override void OnWorldLoad() => ResetWorldSession();

    public override void OnWorldUnload() => ResetWorldSession();

    public override void Unload() => ResetWorldSession();

    private static void ResetWorldSession()
    {
        AutoCampaignController.ResetWorldSession();
        AutoEncounterTriggerController.ResetWorldSession();
        AutoArenaController.ResetWorldSession();
        AutoPhoneWorldService.ResetWorldSession();
        AutoResourceBudget.ResetWorldSession();
    }
}
