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

	internal static void Initialize()
	{
		RulesByRace.Clear();

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
	
		Log.Message($"[Miho Hediff Fix] Cached {RulesByRace.Values.Sum(static x => x.Count)} hediff rules across {RulesByRace.Count} target races.");
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
