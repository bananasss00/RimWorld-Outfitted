using HarmonyLib;
using RimWorld;
using Verse;

namespace Outfitted
{
    [HarmonyPatch (typeof(StatPart_Glow), "ActiveFor")]
    static class StatPart_Glow_ActiveFor_Patch
    {
        /*
         * Фикс бесконечного переодевания разгрузок, рюкзаков и др...
         *
                if (this.humanlikeOnly)
                {
                    Pawn pawn = t as Pawn;
                    if (pawn != null && !pawn.RaceProps.Humanlike)
                    {
                        return false;
                    }
                }
                return t.Spawned;

         * Когда вещь на ПЕШКЕ: стат "Общая скорость работы" ИГНОРИРУЕТ освещенность,
         * Когда вещь на ЗЕМЛЕ: стат "Общая скорость работы" УЧИТЫВАЕТ освещенность,
         */
        [HarmonyPrefix]
        public static bool StatPart_Glow_ActiveFor(Thing t, ref bool __result) => !t.def.IsApparel || (__result = false);
    }
}
