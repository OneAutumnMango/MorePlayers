using HarmonyLib;
using MageQuitModFramework.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MorePlayers.MP
{
    [HarmonyPatch]
    public static class MPPatches
    {
        // ── Initialize (runs from MPModule.OnLoad before patches) ────────────

        public static void Initialize()
        {
            int maxP  = Plugin.MaxPlayers;
            var field = AccessTools.Field(typeof(PlayerDropIn), "wizardNames");
            if (field == null)
            {
                Plugin.Log?.LogWarning("[MorePlayers] PlayerDropIn.wizardNames field not found.");
                return;
            }

            string[] baseNames  = { "JAFAR", "GANDALF", "MERLIN", "DUMBLE", "HARRY", "RON", "HERMIONE", "SNAPE", "SLAB", "BRUCE" };
            string[] extraNames = { "RADAGAST", "VOLDEMORT", "SARUMAN", "MORGANA", "CIRCE", "MEDUSA", "ELMINSTER", "SKELETOR" };

            if (maxP <= baseNames.Length) return;

            var extended = new string[maxP];
            for (int i = 0; i < maxP; i++)
            {
                if (i < baseNames.Length)
                    extended[i] = baseNames[i];
                else if (i - baseNames.Length < extraNames.Length)
                    extended[i] = extraNames[i - baseNames.Length];
                else
                    extended[i] = $"MAGE{i + 1}";
            }

            field.SetValue(null, extended);
            Plugin.Log?.LogInfo($"[MorePlayers] wizardNames extended to {maxP}.");
        }

        // ── PATCH 1: PlayerManager.DetermineBots ─────────────────────────────
        // Replaces direct (non-lambda) integer literals:
        //   Mathf.Min(10, ...)  and  while (num6 <= 10)  → MAX_PLAYERS
        //   == 9 (3-team trigger)                        → MAX_PLAYERS / 3 * 3
        //
        // The per-team <= 3 caps are inside LINQ closures — not patchable via transpiler.
        // Uses ReplaceIntConstant which mutates in-place, preserving branch labels.

        [HarmonyPatch(typeof(PlayerManager), "DetermineBots")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DetermineBots_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            instructions = GameModificationHelpers.ReplaceIntConstant(instructions, 10, Plugin.MaxPlayers);
            return GameModificationHelpers.ReplaceIntConstant(instructions, 9, Plugin.MaxPlayers / 3 * 3);
        }

        // ── PATCH 2: PlayerManager.DetermineBotsForOnlineUI ──────────────────
        // Mathf.Min(10, ...) → MAX_PLAYERS

        [HarmonyPatch(typeof(PlayerManager), "DetermineBotsForOnlineUI")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> DetermineBotsForOnlineUI_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => GameModificationHelpers.ReplaceIntConstant(instructions, 10, Plugin.MaxPlayers);

        // ── PATCH 3: OnlineLobby.RequestNewPlayers ────────────────────────────
        // num = 10 - PlayerManager.players.Count → MAX_PLAYERS

        [HarmonyPatch(typeof(OnlineLobby), "RequestNewPlayers")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> RequestNewPlayers_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => GameModificationHelpers.ReplaceIntConstant(instructions, 10, Plugin.MaxPlayers);

        // ── PATCH 4: PlayerSelection.Awake [Postfix] ─────────────────────────
        // The three private arrays are sized [10] at field-init time (before Awake).
        // Overwrite them immediately after Awake so Start() can index up to MAX-1.
        // Also extend public playerColors array for the extra slots.

        [HarmonyPatch(typeof(PlayerSelection), "Awake")]
        [HarmonyPostfix]
        private static void PlayerSelection_Awake_Postfix(PlayerSelection __instance)
        {
            int maxP = Plugin.MaxPlayers;

            GameModificationHelpers.SetPrivateField<NameSelector[]>(
                __instance, "nameSelectors", new NameSelector[maxP]);
            GameModificationHelpers.SetPrivateField<PlayerCard[]>(
                __instance, "playerCards", new PlayerCard[maxP]);
            GameModificationHelpers.SetPrivateField<PlayerSelectionState[]>(
                __instance, "playerSelectionStates", new PlayerSelectionState[maxP]);

            Color[] existing = __instance.playerColors;
            if (existing != null && existing.Length < maxP)
            {
                var ext = new Color[maxP];
                for (int i = 0; i < maxP; i++)
                    ext[i] = i < existing.Length
                        ? existing[i]
                        : Color.HSVToRGB((float)i / maxP, 0.85f, 0.9f);
                __instance.playerColors = ext;
            }

            Plugin.Log?.LogInfo($"[MorePlayers] PlayerSelection arrays resized to {maxP}.");
        }

        // ── PATCH 5: PlayerSelection.Start [Postfix] ─────────────────────────
        // Vanilla Start fills slots 0-9 from 10 scene children.
        // Clone playerCards[0] and botCards[0] for slots 10 to MAX-1.
        // Scale all cards down so two rows of MAX/2 fit the same canvas width.
        //
        // botCards must be extended here because RefreshBots() does:
        //   this.botCards[playerId - 1]
        // which crashes at index 10+ if botCards is still size 10.

        [HarmonyPatch(typeof(PlayerSelection), "Start")]
        [HarmonyPostfix]
        private static void PlayerSelection_Start_Postfix(PlayerSelection __instance)
        {
            int maxP = Plugin.MaxPlayers;
            if (maxP <= 10) return;

            var playerCards   = GameModificationHelpers.GetPrivateField<PlayerCard[]>(__instance, "playerCards");
            var nameSelectors = GameModificationHelpers.GetPrivateField<NameSelector[]>(__instance, "nameSelectors");

            // ── Step 0: Extend the shared PlayerColors ScriptableObject ─────────
            // PlayerCard.SetPlayerColor() accesses playerColors.colors[playerIndex + 1].
            // All 10 scene cards share the same PlayerColors ScriptableObject asset.
            // The asset only has 11 entries (indices 0-10 for players 1-10).
            // For players 11-16 (playerIndex 10-15) we need colors[11]-colors[16].
            // Extend the shared asset before Init is called on any cloned card.
            var sharedPC = playerCards[0].playerColors;
            if (sharedPC != null && sharedPC.colors != null && sharedPC.colors.Length < maxP + 1)
            {
                var orig = sharedPC.colors;
                var ext  = new Color[maxP + 1];
                for (int c = 0; c < maxP + 1; c++)
                    ext[c] = c < orig.Length
                        ? orig[c]
                        : Color.HSVToRGB((float)c / (maxP + 1), 0.85f, 0.9f);
                sharedPC.colors = ext;
            }

            // ── Extend playerCards ───────────────────────────────────────────
            PlayerCard pcTemplate = playerCards[0];
            Transform  pcRoot     = pcTemplate.transform.parent;
            float      scale      = 10f / maxP;

            // Scale down the 10 existing player cards
            for (int i = 0; i < 10; i++)
                if (playerCards[i] != null)
                    playerCards[i].transform.localScale = Vector3.one * scale;

            // Clone player cards for slots 10 to maxP-1
            for (int i = 10; i < maxP; i++)
            {
                GameObject cloned = UnityEngine.Object.Instantiate(pcTemplate.gameObject, pcRoot);
                cloned.name = $"Player{i + 1} Card";

                var tf  = cloned.transform;
                var pos = tf.localPosition;
                pos.y   = -564.8f;
                tf.localPosition = pos;
                tf.localScale    = Vector3.one * scale;

                var card = cloned.GetComponent<PlayerCard>();
                card.Init(i, __instance.elementIcons, __instance.hatIcons,
                          __instance.teamTexts, __instance.robes,
                          __instance.playerColors[i]);

                var ns = cloned.transform.Find("Name")?.Find("Name Selector")
                               ?.GetComponent<NameSelector>();

                playerCards[i]   = card;
                nameSelectors[i] = ns;
            }

            GameModificationHelpers.SetPrivateField<PlayerCard[]>(__instance, "playerCards", playerCards);
            GameModificationHelpers.SetPrivateField<NameSelector[]>(__instance, "nameSelectors", nameSelectors);

            // ── Extend botCards (public field) ───────────────────────────────
            BotCard[] botCards = __instance.botCards;
            if (botCards != null && botCards.Length < maxP)
            {
                var extBotCards = new BotCard[maxP];
                for (int i = 0; i < botCards.Length; i++)
                    extBotCards[i] = botCards[i];

                BotCard bcTemplate = botCards[0];
                Transform bcRoot   = bcTemplate.transform.parent;

                for (int i = botCards.Length; i < maxP; i++)
                {
                    GameObject cloned = UnityEngine.Object.Instantiate(bcTemplate.gameObject, bcRoot);
                    cloned.name = $"Bot{i + 1} Card";

                    var tf  = cloned.transform;
                    var pos = tf.localPosition;
                    pos.y   = -564.8f;
                    tf.localPosition = pos;

                    var bc = cloned.GetComponent<BotCard>();
                    bc.playerColor = __instance.playerColors[i];
                    bc.Init();
                    bc.SetTeam(TeamColor.None);
                    extBotCards[i] = bc;
                }

                __instance.botCards = extBotCards;
            }

            Plugin.Log?.LogInfo($"[MorePlayers] PlayerSelection.Start extended to {maxP} cards.");
        }

        // ── PATCH 6: PlayerSelection.AddPlayerFull [Transpiler] ──────────────
        // (index >= 5) ? -131f : 67f — 5 is Ldc_I4_5 (direct, NOT in lambda)
        // Replace with MAX/2 for correct two-row layout.

        [HarmonyPatch(typeof(PlayerSelection), "AddPlayerFull")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> AddPlayerFull_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => GameModificationHelpers.ReplaceIntConstant(instructions, 5, Plugin.MaxPlayers / 2);

        // ── PATCH 7: PlayerSelection.StartGame [Transpiler] ──────────────────
        // for (i = 0; i < 10; ...) hides all cards → MAX_PLAYERS

        [HarmonyPatch(typeof(PlayerSelection), "StartGame")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> StartGame_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => GameModificationHelpers.ReplaceIntConstant(instructions, 10, Plugin.MaxPlayers);

        // ── PATCH 8a: PlayerSelection.ErrorCheck [Prefix] ────────────────────
        // The > 5 per-team check is inside a LINQ lambda closure; ldc.i4.5 is in
        // the closure's generated method, not in ErrorCheck's IL.
        // Strategy: let original run, record whether teams are valid under new cap,
        // then suppress the "too many wizards" error in the Postfix if appropriate.

        [HarmonyPatch(typeof(PlayerSelection), "ErrorCheck")]
        [HarmonyPrefix]
        private static bool ErrorCheck_Prefix(PlayerSelection __instance)
        {
            int halfMax = Plugin.MaxPlayers / 2;
            bool teamOverfull = false;
            foreach (TeamColor tc in new[] { TeamColor.Red, TeamColor.Blue, TeamColor.Yellow })
            {
                if (PlayerManager.players.Count(kvp => kvp.Value.teamColor == tc) > halfMax)
                {
                    teamOverfull = true;
                    break;
                }
            }
            _teamOverfullUnderNewCap = teamOverfull;
            return true;
        }

        [ThreadStatic]
        private static bool _teamOverfullUnderNewCap;

        // ── PATCH 8b: PlayerSelection.ErrorCheck [Postfix] ───────────────────

        [HarmonyPatch(typeof(PlayerSelection), "ErrorCheck")]
        [HarmonyPostfix]
        private static void ErrorCheck_Postfix(PlayerSelection __instance, ref bool __result)
        {
            if (!__result || _teamOverfullUnderNewCap) return;

            var errorText = AccessTools.Field(typeof(PlayerSelection), "errorText")
                ?.GetValue(__instance) as UnityEngine.UI.Text;

            if (errorText != null && errorText.text == "Too many wizards on one team.")
            {
                errorText.text = "";
                __result = false;
            }
        }

        // ── PATCH 9: PlayerDropIn.Update [Transpiler] ─────────────────────────
        // for (i = 1; i <= 10; i = i2 + 1) → MAX_PLAYERS

        [HarmonyPatch(typeof(PlayerDropIn), "Update")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> PlayerDropIn_Update_Transpiler(
            IEnumerable<CodeInstruction> instructions)
            => GameModificationHelpers.ReplaceIntConstant(instructions, 10, Plugin.MaxPlayers);

        // ── PATCH 10: BattleManager.Awake [Postfix] ──────────────────────────
        // Extend player_colors (public) and rebuild muted/light (private Color[13])
        // to MAX_PLAYERS using the same GameUtility.AverageColors call as vanilla.

        [HarmonyPatch(typeof(BattleManager), "Awake")]
        [HarmonyPostfix]
        private static void BattleManager_Awake_Postfix(BattleManager __instance)
        {
            int maxP = Plugin.MaxPlayers;
            Color[] existing = __instance.player_colors;
            if (existing == null || existing.Length >= maxP) return;

            var colors = new Color[maxP];
            for (int i = 0; i < maxP; i++)
                colors[i] = i < existing.Length
                    ? existing[i]
                    : Color.HSVToRGB((float)i / maxP, 0.85f, 0.9f);
            __instance.player_colors = colors;

            var muted = new Color[maxP];
            var light = new Color[maxP];
            for (int i = 0; i < maxP; i++)
            {
                muted[i] = GameUtility.AverageColors(colors[i], Color.white, 0.2f, false);
                light[i] = GameUtility.AverageColors(colors[i], Color.white, 0.4f, false);
            }

            GameModificationHelpers.SetPrivateField<Color[]>(__instance, "muted_player_colors", muted);
            GameModificationHelpers.SetPrivateField<Color[]>(__instance, "light_player_colors", light);

            Plugin.Log?.LogInfo($"[MorePlayers] BattleManager color arrays extended to {maxP}.");
        }

        // ── PATCH 11: SelectionMenu.ChangeNumberOfBots [Transpiler] ──────────
        // (num + (up ? 1 : 10)) % 11
        //   10 → MAX_PLAYERS      (wrap-back value)
        //   11 → MAX_PLAYERS + 1  (modulo divisor)

        [HarmonyPatch(typeof(SelectionMenu), "ChangeNumberOfBots")]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ChangeNumberOfBots_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            instructions = GameModificationHelpers.ReplaceIntConstant(instructions, 10, Plugin.MaxPlayers);
            return GameModificationHelpers.ReplaceIntConstant(instructions, 11, Plugin.MaxPlayers + 1);
        }

        // ── PATCH 12: SpellManager.Awake [Postfix] ───────────────────────────
        // Extend private int[] ai_draft_weights from 10 to MAX_PLAYERS.
        // Extra picks get weight 1 (minimum priority for AI draft).

        [HarmonyPatch(typeof(SpellManager), "Awake")]
        [HarmonyPostfix]
        private static void SpellManager_Awake_Postfix(SpellManager __instance)
        {
            int maxP = Plugin.MaxPlayers;
            if (maxP <= 10) return;

            var field = AccessTools.Field(typeof(SpellManager), "ai_draft_weights");
            if (field == null)
            {
                Plugin.Log?.LogWarning("[MorePlayers] SpellManager.ai_draft_weights not found.");
                return;
            }

            int[] baseWeights = { 144, 89, 55, 34, 21, 13, 8, 5, 3, 2 };
            var ext = new int[maxP];
            for (int i = 0; i < maxP; i++)
                ext[i] = i < baseWeights.Length ? baseWeights[i] : 1;

            field.SetValue(__instance, ext);
            Plugin.Log?.LogInfo($"[MorePlayers] ai_draft_weights extended to {maxP}.");
        }
    }
}
