using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Outfitted
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class HotSwappableAttribute : Attribute
    {
    }

    [StaticConstructorOnStartup]
    public static class OutfittedMod
    {
        internal static bool showApparelScores;

        internal static bool isSaveStorageSettingsEnabled;

        static OutfittedMod()
        {
            isSaveStorageSettingsEnabled = ModLister.GetActiveModWithIdentifier("savestoragesettings.kv.rw") != null;

            new Harmony("rimworld.outfitted").PatchAll();
        }

        private static readonly SimpleCurve HitPointsPercentScoreFactorCurve = new SimpleCurve
        {
            new CurvePoint( 0f, 0f ),
            new CurvePoint( 0.2f, 0.2f ),
            new CurvePoint( 0.22f, 0.6f ),
            new CurvePoint( 0.5f, 0.6f ),
            new CurvePoint( 0.52f, 1f ),
        };

        private static readonly SimpleCurve InsulationTemperatureScoreFactorCurve_Need = new SimpleCurve
        {
            new CurvePoint( 0f, 1f ),
            new CurvePoint( 30f, 4f ),
        };

        private static readonly SimpleCurve InsulationFactorCurve = new SimpleCurve
        {
            new CurvePoint( -20f, -3f ),
            new CurvePoint( -10f, -2f ),
            new CurvePoint( 10f, 2f ),
            new CurvePoint( 20f, 3f )
        };

        public static float ApparelScoreRaw(Pawn pawn, Apparel apparel, NeededWarmth neededWarmth = NeededWarmth.Any)
        {
            var outfit = pawn?.outfits?.CurrentOutfit as ExtendedOutfit;
            if (pawn != null && outfit == null)
            {
                Log.ErrorOnce("Outfitted :: Not an ExtendedOutfit, something went wrong.", 399441);
                return 0f;
            }

            float score = 0.1f + ApparelScoreRawPriorities(apparel, outfit);

            if (pawn != null && outfit?.AutoWorkPriorities == true)
            {
                score += ApparelScoreAutoWorkPriorities( pawn, apparel );
            }

            if (apparel.def.useHitPoints)
            {
                float x = (float)apparel.HitPoints / apparel.MaxHitPoints;
                score *= HitPointsPercentScoreFactorCurve.Evaluate(x);
            }

            score += apparel.GetSpecialApparelScoreOffset();

            if (pawn != null && outfit != null)
                score += ApparelScoreRawInsulation(pawn, apparel, outfit, neededWarmth);

            if (pawn?.def?.race?.Animal == false)
            {
                if (outfit?.PenaltyWornByCorpse == true && apparel.WornByCorpse && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.DeadMansApparel, true))
                {
                    score -= 0.5f;
                    if (score > 0f)
                    {
                        score *= 0.1f;
                    }
                }

                if (apparel.Stuff == ThingDefOf.Human.race.leatherDef)
                {
                    if (ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.HumanLeatherApparelSad, true))
                    {
                        score -= 0.5f;
                        if (score > 0f)
                        {
                            score *= 0.1f;
                        }
                    }
                    if (ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.HumanLeatherApparelHappy, true))
                    {
                        score += 0.12f;
                    }
                }
            }

            //royalty titles
            if (pawn?.royalty?.AllTitlesInEffectForReading?.Count > 0)
            {
                HashSet<ThingDef> tmpAllowedApparels = new HashSet<ThingDef>();
                HashSet<ThingDef> tmpRequiredApparels = new HashSet<ThingDef>();
                HashSet<BodyPartGroupDef> tmpBodyPartGroupsWithRequirement = new HashSet<BodyPartGroupDef>();
                QualityCategory qualityCategory = QualityCategory.Awful;
                foreach (RoyalTitle item in pawn.royalty.AllTitlesInEffectForReading)
                {
                    if (item.def.requiredApparel != null)
                    {
                        for (int i = 0; i < item.def.requiredApparel.Count; i++)
                        {
                            tmpAllowedApparels.AddRange(item.def.requiredApparel[i].AllAllowedApparelForPawn(pawn, ignoreGender: false, includeWorn: true));
                            tmpRequiredApparels.AddRange(item.def.requiredApparel[i].AllRequiredApparelForPawn(pawn, ignoreGender: false, includeWorn: true));
                            tmpBodyPartGroupsWithRequirement.AddRange(item.def.requiredApparel[i].bodyPartGroupsMatchAny);
                        }
                    }
                    if (item.def.requiredMinimumApparelQuality > qualityCategory)
                    {
                        qualityCategory = item.def.requiredMinimumApparelQuality;
                    }
                }

                if (apparel.TryGetQuality(out QualityCategory qc) && qc < qualityCategory)
                {
                    score *= 0.25f;
                }

                bool isRequired = apparel.def.apparel.bodyPartGroups.Any(bp => tmpBodyPartGroupsWithRequirement.Contains(bp));
                if (isRequired)
                {
                    foreach (ThingDef tmpRequiredApparel in tmpRequiredApparels)
                    {
                        tmpAllowedApparels.Remove(tmpRequiredApparel);
                    }
                    if (tmpAllowedApparels.Contains(apparel.def))
                    {
                        score *= 10f;
                    }
                    if (tmpRequiredApparels.Contains(apparel.def))
                    {
                        score *= 25f;
                    }
                }
            }

            return score;
        }

        static float ApparelScoreRawPriorities(Apparel apparel, ExtendedOutfit outfit)
        {
            if (outfit?.StatPriorities.Any() != true) {
                return 0f;
            }

            return outfit.StatPriorities
                         .Select(sp => new
                         {
                             weight = sp.Weight,
                             value = apparel.def.equippedStatOffsets.GetStatOffsetFromList(sp.Stat) + apparel.GetStatValue(sp.Stat),
                             def = sp.Stat.defaultBaseValue,
                         })
                         .Average(sp => (Math.Abs(sp.def) < 0.001f ? sp.value : (sp.value - sp.def)/sp.def) * Mathf.Pow(sp.weight, 3));
        }

        static float ApparelScoreAutoWorkPriorities( Pawn pawn, Apparel apparel )
        {
            return WorkPriorities.WorktypeStatPriorities( pawn )
                                 .Select( sp => ( apparel.def.equippedStatOffsets.GetStatOffsetFromList( sp.Stat )
                                                + apparel.GetStatValue( sp.Stat )
                                                - sp.Stat.defaultBaseValue ) * sp.Weight )
                                 .Sum(); // NOTE: weights were already normalized to sum to 1.
        }

        static float ApparelScoreRawInsulation(Pawn pawn, Apparel apparel, ExtendedOutfit outfit, NeededWarmth neededWarmth)
        {
            float insulation;

            if (outfit.targetTemperaturesOverride)
            {
                // NOTE: We can't rely on the vanilla check for taking off gear for temperature, because
                // we need to consider all the wardrobe changes taken together; each individual change may
                // note push us over the thresholds, but several changes together may.
                // Return 1 for temperature offsets here, we'll look at the effects of any gear we have to 
                // take off below.
                // NOTE: This is still suboptimal, because we're still only considering one piece of apparel
                // to wear at each time. A better solution would be reducing the problem to a series of linear
                // equations, and then solving that system. 
                // I'm not sure that's feasible at all; first off for simple computational reasons: the linear
                // system to solve would be fairly massive, optimizing for dozens of pawns and hundreds of pieces 
                // of gear simultaneously. Second, many of the stat functions aren't actually linear, and would
                // have to be made to be linear.
                bool currentlyWorn = pawn.apparel.WornApparel.Contains(apparel);

                var currentRange = pawn.ComfortableTemperatureRange();
                var candidateRange = currentRange;
                if(outfit.AutoTemp)
                {
                    var seasonalTemp = pawn.Map.mapTemperature.SeasonalTemp;
                    outfit.targetTemperatures = new FloatRange(seasonalTemp - outfit.autoTempOffset, seasonalTemp + outfit.autoTempOffset);
                }
                var targetRange = outfit.targetTemperatures;
                var apparelOffset = GetInsulationStats(apparel);

                if(!currentlyWorn)
                {
                    // effect of this piece of apparel
                    candidateRange.min += apparelOffset.min;
                    candidateRange.max += apparelOffset.max;

                    foreach (var otherApparel in pawn.apparel.WornApparel)
                    {
                        // effect of taking off any other apparel that is incompatible
                        if (!ApparelUtility.CanWearTogether(apparel.def, otherApparel.def, pawn.RaceProps.body))
                        {
                            var otherInsulationRange = GetInsulationStats(otherApparel);

                            candidateRange.min -= otherInsulationRange.min;
                            candidateRange.max -= otherInsulationRange.max;
                        }
                    }
                }

                // did we get any closer to our target range? (smaller distance is better, negative values are overkill).
                var currentDistance = new FloatRange( Mathf.Max( currentRange.min - targetRange.min, 0f ),
                                                      Mathf.Max( targetRange.max  - currentRange.max, 0f ) );
                var candidateDistance = new FloatRange( Mathf.Max( candidateRange.min - targetRange.min, 0f ),
                                                        Mathf.Max( targetRange.max    - candidateRange.max, 0f ) );

                // improvement in distances
                insulation = InsulationFactorCurve.Evaluate( currentDistance.min - candidateDistance.min ) +
                             InsulationFactorCurve.Evaluate( currentDistance.max - candidateDistance.max );
#if DEBUG
                Log.Message( $"{pawn.Name.ToStringShort} :: {apparel.LabelCap}\n" +
                             $"\ttarget range: {targetRange}, current range: {currentRange}, candidate range {candidateRange}\n" +
                             $"\tcurrent distance: {currentDistance}, candidate distance: {candidateDistance}\n" +
                             $"\timprovement: {(currentDistance.min - candidateDistance.min) + (currentDistance.max - candidateDistance.max)}, insulation score: {insulation}\n" );
#endif
            }
            else
            {
                float statValue;
                if (neededWarmth == NeededWarmth.Warm)
                {
                    statValue = apparel.GetStatValue(StatDefOf.Insulation_Cold, true);
                    insulation = InsulationTemperatureScoreFactorCurve_Need.Evaluate(statValue);
                }
                else if (neededWarmth == NeededWarmth.Cool)
                {
                    statValue = apparel.GetStatValue(StatDefOf.Insulation_Heat, true);
                    insulation = InsulationTemperatureScoreFactorCurve_Need.Evaluate(statValue);
                }
                else
                {
                    insulation = 1f;
                }
            }
            return insulation;
        }

        static FloatRange GetInsulationStats(Apparel apparel)
        {
            var insulationCold = apparel.GetStatValue(StatDefOf.Insulation_Cold);
            var insulationHeat = apparel.GetStatValue(StatDefOf.Insulation_Heat);

            return new FloatRange(-insulationCold, insulationHeat);
        }
    }
}
