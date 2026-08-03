using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

		public MoreAIPlayer() => Instance = this;

		public override void Unload() => Instance = null;
	}
}
