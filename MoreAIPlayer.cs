using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria.Localization;
using Terraria.ModLoader;

namespace MoreAIPlayer
{
	// Please read https://github.com/tModLoader/tModLoader/wiki/Basic-tModLoader-Modding-Guide#mod-skeleton-contents for more information about the various files in a mod.
	public class MoreAIPlayer : Mod
	{
		/// <summary>The single mod instance, set the moment the mod is constructed. It exists so the
		/// static fault barrier (<see cref="Companion.CompanionFaultGuard"/>) can reach this mod's
		/// logger from any context without a ModContent lookup.</summary>
		public static MoreAIPlayer Instance { get; private set; }
		public static ModKeybind CompanionRescueKeybind { get; private set; }
		public static ModKeybind CompanionWheelKeybind { get; private set; }

		public MoreAIPlayer() => Instance = this;

		public override void Load()
		{
			// Mouse2 is Terraria's right mouse button. Clearing this binding deliberately falls
			// back to the historical double-right-click rescue gesture.
			CompanionRescueKeybind = KeybindLoader.RegisterKeybind(this, "CompanionRescue", "Mouse2");
			CompanionWheelKeybind = KeybindLoader.RegisterKeybind(this, "CompanionWheel", "V");
		}

		public static bool HasCompanionRescueKeybind =>
			CompanionRescueKeybind is not null && CompanionRescueKeybind.GetAssignedKeys().Count > 0;

		/// <summary>Human-readable binding for the Debug Book. An empty binding means the legacy
		/// right-click fallback; Mouse2 is written as a proper localized right-button label.</summary>
		public static string CompanionRescueKeyDisplayName()
		{
			string rightMouse = Language.GetTextValue("Mods.MoreAIPlayer.Items.CompanionDebugger.RescueDefaultKey");
			if (CompanionRescueKeybind is null)
				return rightMouse;

			var assigned = CompanionRescueKeybind.GetAssignedKeys();
			if (assigned.Count == 0)
				return rightMouse;

			var labels = new List<string>(assigned.Count);
			foreach (string key in assigned)
				labels.Add(key == "Mouse2" ? rightMouse : key);
			return string.Join(" / ", labels);
		}

		public override void Unload()
		{
			CompanionRescueKeybind = null;
			CompanionWheelKeybind = null;
			Instance = null;
		}
	}
}
