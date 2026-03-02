using HarmonyLib;
using MageQuitModFramework.Modding;

namespace MorePlayers.MP
{
    public class MPModule : BaseModule
    {
        public override string ModuleName => "More Players";

        protected override void OnLoad(Harmony harmony)
        {
            MPPatches.Initialize();
            PatchGroup(harmony, typeof(MPPatches));
        }

        protected override void OnUnload(Harmony harmony)
        {
            harmony.UnpatchSelf();
        }
    }
}
