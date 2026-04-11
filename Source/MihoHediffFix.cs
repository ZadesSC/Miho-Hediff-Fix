using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CheckHediffMakeHediff;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Zades.MihoHediffFix;

public sealed class MihoHediffFixMod : Mod
{
	public MihoHediffFixMod(ModContentPack content)
		: base(content)
	{
	}
}

[StaticConstructorOnStartup]
public static class MihoHediffFixBootstrap
{
	static MihoHediffFixBootstrap()
	{
		try
		{
			MihoFixState.Initialize();
			new Harmony("zades.mihohedifffix").PatchAll(Assembly.GetExecutingAssembly());
			Log.Message("[Miho Hediff Fix] Initialized.");
		}
		catch (Exception ex)
		{
			Log.Error("[Miho Hediff Fix] Initialization failed: " + ex);
		}
	}
}

internal static class MihoFixState
{
	internal const string MihoPackageId = "miho.fortifiedoutremer";
	internal const string NoJobRuleDefName = "Miho_CheckHediffMakeHediff_Nojob";
	internal const int AggregatorBucketTicks = 60;

	internal static readonly Dictionary<ThingDef, List<CheckHediffMakeHediffDef>> RulesByRace = new Dictionary<ThingDef, List<CheckHediffMakeHediffDef>>();
	internal static readonly HashSet<ThingDef> OptimizedApparelDefs = new HashSet<ThingDef>();
	internal static readonly HashSet<HediffDef> OptimizedApparelHediffs = new HashSet<HediffDef>();

	internal static void Initialize()
	{
		RulesByRace.Clear();
		OptimizedApparelDefs.Clear();
		OptimizedApparelHediffs.Clear();

		foreach (CheckHediffMakeHediffDef rule in DefDatabase<CheckHediffMakeHediffDef>.AllDefsListForReading)
		{
			if (rule?.targetRace == null)
			{
				continue;
			}

			if (!RulesByRace.TryGetValue(rule.targetRace, out List<CheckHediffMakeHediffDef> rules))
			{
				rules = new List<CheckHediffMakeHediffDef>();
				RulesByRace[rule.targetRace] = rules;
			}

			rules.Add(rule);
		}

		foreach (KeyValuePair<ThingDef, List<CheckHediffMakeHediffDef>> entry in RulesByRace)
		{
			List<CompProperties> comps = entry.Key.comps ?? (entry.Key.comps = new List<CompProperties>());
			comps.RemoveAll(static comp => comp is CompProperties_CheckHediffMakeHediff);

			if (!comps.Any(static comp => comp is CompProperties_MihoHediffAggregator))
			{
				comps.Add(new CompProperties_MihoHediffAggregator());
			}
		}

		foreach (ThingDef thingDef in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (!IsMihoDef(thingDef) || thingDef.comps == null)
			{
				continue;
			}

			foreach (CompProperties_CauseHediff_Apparel comp in thingDef.comps.OfType<CompProperties_CauseHediff_Apparel>())
			{
				OptimizedApparelDefs.Add(thingDef);
				if (comp.hediff != null)
				{
					OptimizedApparelHediffs.Add(comp.hediff);
				}
			}
		}

		Log.Message($"[Miho Hediff Fix] Cached {RulesByRace.Values.Sum(static x => x.Count)} hediff rules across {RulesByRace.Count} target races and {OptimizedApparelDefs.Count} Miho apparel defs.");
	}

	internal static bool IsMihoDef(Def def)
	{
		ModContentPack pack = def?.modContentPack;
		return string.Equals(pack?.PackageId, MihoPackageId, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(pack?.PackageIdPlayerFacing, MihoPackageId, StringComparison.OrdinalIgnoreCase);
	}

	internal static bool IsOptimizedApparel(ThingDef thingDef)
	{
		return thingDef != null && OptimizedApparelDefs.Contains(thingDef);
	}

	internal static CompProperties_CauseHediff_Apparel GetOptimizedApparelComp(Apparel apparel)
	{
		if (apparel?.def?.comps == null || !IsOptimizedApparel(apparel.def))
		{
			return null;
		}

		return apparel.def.comps.OfType<CompProperties_CauseHediff_Apparel>().FirstOrDefault();
	}

	internal static void ApplyApparelHediff(Pawn pawn, Apparel apparel)
	{
		CompProperties_CauseHediff_Apparel props = GetOptimizedApparelComp(apparel);
		if (pawn?.health == null || props?.hediff == null || pawn.health.hediffSet.HasHediff(props.hediff, false))
		{
			return;
		}

		BodyPartRecord bodyPart = null;
		if (props.part != null)
		{
			bodyPart = pawn.RaceProps?.body?.GetPartsWithDef(props.part).FirstOrDefault();
		}

		pawn.health.AddHediff(props.hediff, bodyPart);
	}

	internal static void RemoveApparelHediffIfUnsupported(Pawn pawn, Apparel apparel)
	{
		CompProperties_CauseHediff_Apparel props = GetOptimizedApparelComp(apparel);
		if (pawn?.health == null || props?.hediff == null)
		{
			return;
		}

		if (IsHediffStillSupportedByWornApparel(pawn, props.hediff))
		{
			return;
		}

		RemoveAllHediffsOfDef(pawn, props.hediff);
	}

	internal static bool IsHediffStillSupportedByWornApparel(Pawn pawn, HediffDef hediffDef)
	{
		if (pawn?.apparel?.WornApparel == null || hediffDef == null)
		{
			return false;
		}

		for (int i = 0; i < pawn.apparel.WornApparel.Count; i++)
		{
			CompProperties_CauseHediff_Apparel props = GetOptimizedApparelComp(pawn.apparel.WornApparel[i]);
			if (props?.hediff == hediffDef)
			{
				return true;
			}
		}

		return false;
	}

	internal static void SyncApparelHediffs(Pawn pawn)
	{
		if (pawn?.health == null)
		{
			return;
		}

		if (pawn.apparel?.WornApparel != null)
		{
			for (int i = 0; i < pawn.apparel.WornApparel.Count; i++)
			{
				ApplyApparelHediff(pawn, pawn.apparel.WornApparel[i]);
			}
		}

		foreach (HediffDef hediffDef in OptimizedApparelHediffs)
		{
			if (!pawn.health.hediffSet.HasHediff(hediffDef, false))
			{
				continue;
			}

			if (!IsHediffStillSupportedByWornApparel(pawn, hediffDef))
			{
				RemoveAllHediffsOfDef(pawn, hediffDef);
			}
		}
	}

	internal static void RemoveAllHediffsOfDef(Pawn pawn, HediffDef hediffDef)
	{
		if (pawn?.health == null || hediffDef == null)
		{
			return;
		}

		Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef, false);
		while (hediff != null)
		{
			pawn.health.RemoveHediff(hediff);
			hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef, false);
		}
	}
}

public class CompProperties_MihoHediffAggregator : CompProperties
{
	public CompProperties_MihoHediffAggregator()
	{
		compClass = typeof(Comp_MihoHediffAggregator);
	}
}

public class Comp_MihoHediffAggregator : ThingComp
{
	private Pawn pawn;
	private Dictionary<string, int> remainingTicksByRule = new Dictionary<string, int>();
	private List<string> savedRuleKeys;
	private List<int> savedRuleValues;

	public override void PostSpawnSetup(bool respawningAfterLoad)
	{
		base.PostSpawnSetup(respawningAfterLoad);
		pawn = parent as Pawn;
		EnsureRuleState();
		MihoFixState.SyncApparelHediffs(pawn);
	}

	public override void CompTick()
	{
		if (pawn == null || !pawn.Spawned || pawn.Dead)
		{
			return;
		}

		if (!Gen.IsHashIntervalTick((Thing)pawn, MihoFixState.AggregatorBucketTicks))
		{
			return;
		}

		ProcessRules();
	}

	public override void PostExposeData()
	{
		base.PostExposeData();
		Scribe_Collections.Look(ref remainingTicksByRule, "remainingTicksByRule", LookMode.Value, LookMode.Value, ref savedRuleKeys, ref savedRuleValues);
		if (remainingTicksByRule == null)
		{
			remainingTicksByRule = new Dictionary<string, int>();
		}
	}

	private void EnsureRuleState()
	{
		if (pawn == null || !MihoFixState.RulesByRace.TryGetValue(pawn.def, out List<CheckHediffMakeHediffDef> rules))
		{
			remainingTicksByRule.Clear();
			return;
		}

		HashSet<string> validRuleNames = new HashSet<string>();
		for (int i = 0; i < rules.Count; i++)
		{
			CheckHediffMakeHediffDef rule = rules[i];
			if (rule == null || string.IsNullOrEmpty(rule.defName))
			{
				continue;
			}

			validRuleNames.Add(rule.defName);
			if (!remainingTicksByRule.ContainsKey(rule.defName) || remainingTicksByRule[rule.defName] <= 0)
			{
				remainingTicksByRule[rule.defName] = Math.Max(rule.tickMultiflier, MihoFixState.AggregatorBucketTicks);
			}
		}

		List<string> staleKeys = remainingTicksByRule.Keys.Where(key => !validRuleNames.Contains(key)).ToList();
		for (int i = 0; i < staleKeys.Count; i++)
		{
			remainingTicksByRule.Remove(staleKeys[i]);
		}
	}

	private void ProcessRules()
	{
		EnsureRuleState();
		if (pawn == null || !MihoFixState.RulesByRace.TryGetValue(pawn.def, out List<CheckHediffMakeHediffDef> rules))
		{
			return;
		}

		for (int i = 0; i < rules.Count; i++)
		{
			CheckHediffMakeHediffDef rule = rules[i];
			if (rule == null || string.IsNullOrEmpty(rule.defName))
			{
				continue;
			}

			int resetTicks = Math.Max(rule.tickMultiflier, MihoFixState.AggregatorBucketTicks);

			if (!HasAnySourceHediff(rule))
			{
				remainingTicksByRule[rule.defName] = resetTicks;
				continue;
			}

			int remainingTicks = remainingTicksByRule.TryGetValue(rule.defName, out int value) ? value : resetTicks;
			remainingTicks -= MihoFixState.AggregatorBucketTicks;
			if (remainingTicks > 0)
			{
				remainingTicksByRule[rule.defName] = remainingTicks;
				continue;
			}

			ApplyRule(rule);
			remainingTicksByRule[rule.defName] = resetTicks;
		}
	}

	private bool HasAnySourceHediff(CheckHediffMakeHediffDef rule)
	{
		if (pawn?.health == null || rule?.hediffDefs == null)
		{
			return false;
		}

		for (int i = 0; i < rule.hediffDefs.Count; i++)
		{
			HediffDef hediffDef = rule.hediffDefs[i];
			if (hediffDef != null && pawn.health.hediffSet.HasHediff(hediffDef, false))
			{
				return true;
			}
		}

		return false;
	}

	private void ApplyRule(CheckHediffMakeHediffDef rule)
	{
		if (pawn?.health == null || rule?.makeHediffDefs == null || rule.hediffSeverity <= 0f)
		{
			return;
		}

		if (rule.defName == MihoFixState.NoJobRuleDefName && HasAnyTargetHediff(rule))
		{
			return;
		}

		for (int i = 0; i < rule.makeHediffDefs.Count; i++)
		{
			HediffDef hediffDef = rule.makeHediffDefs[i];
			if (hediffDef == null)
			{
				continue;
			}

			HealthUtility.AdjustSeverity(pawn, hediffDef, rule.hediffSeverity);
		}
	}

	private bool HasAnyTargetHediff(CheckHediffMakeHediffDef rule)
	{
		if (pawn?.health == null || rule?.makeHediffDefs == null)
		{
			return false;
		}

		for (int i = 0; i < rule.makeHediffDefs.Count; i++)
		{
			HediffDef hediffDef = rule.makeHediffDefs[i];
			if (hediffDef != null && pawn.health.hediffSet.HasHediff(hediffDef, false))
			{
				return true;
			}
		}

		return false;
	}
}

[HarmonyPatch(typeof(ThingComp_CheckHediffMakeHediff), nameof(ThingComp_CheckHediffMakeHediff.CompTick))]
public static class Patch_ThingComp_CheckHediffMakeHediff_CompTick
{
	public static bool Prefix(ThingComp_CheckHediffMakeHediff __instance)
	{
		Pawn pawn = ((ThingComp)__instance).parent as Pawn;
		if (pawn != null && MihoFixState.RulesByRace.ContainsKey(pawn.def))
		{
			return false;
		}

		return true;
	}
}

[HarmonyPatch(typeof(CompCauseHediff_Apparel), nameof(CompCauseHediff_Apparel.CompTick))]
public static class Patch_CompCauseHediff_Apparel_CompTick
{
	public static bool Prefix(CompCauseHediff_Apparel __instance)
	{
		Apparel apparel = __instance.parent as Apparel;
		if (apparel != null && MihoFixState.IsOptimizedApparel(apparel.def))
		{
			return false;
		}

		return true;
	}
}

[HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelAdded))]
public static class Patch_Pawn_ApparelTracker_Notify_ApparelAdded
{
	public static void Postfix(Pawn_ApparelTracker __instance, Apparel apparel)
	{
		if (__instance?.pawn == null || apparel == null)
		{
			return;
		}

		if (MihoFixState.IsOptimizedApparel(apparel.def))
		{
			MihoFixState.ApplyApparelHediff(__instance.pawn, apparel);
		}
	}
}

[HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelRemoved))]
public static class Patch_Pawn_ApparelTracker_Notify_ApparelRemoved
{
	public static void Postfix(Pawn_ApparelTracker __instance, Apparel apparel)
	{
		if (__instance?.pawn == null || apparel == null)
		{
			return;
		}

		if (MihoFixState.IsOptimizedApparel(apparel.def))
		{
			MihoFixState.RemoveApparelHediffIfUnsupported(__instance.pawn, apparel);
		}
	}
}
