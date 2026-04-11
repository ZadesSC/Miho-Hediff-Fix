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

internal sealed class MihoRaceRuntime
{
	private readonly Dictionary<HediffDef, int> sourceIndexesByDef = new Dictionary<HediffDef, int>();

	internal List<CheckHediffMakeHediffDef> Rules { get; } = new List<CheckHediffMakeHediffDef>();

	internal int RelevantSourceCount => sourceIndexesByDef.Count;

	internal void AddRule(CheckHediffMakeHediffDef rule)
	{
		Rules.Add(rule);
		if (rule?.hediffDefs == null)
		{
			return;
		}

		for (int i = 0; i < rule.hediffDefs.Count; i++)
		{
			HediffDef sourceDef = rule.hediffDefs[i];
			if (sourceDef != null && !sourceIndexesByDef.ContainsKey(sourceDef))
			{
				sourceIndexesByDef[sourceDef] = sourceIndexesByDef.Count;
			}
		}
	}

	internal bool TryGetSourceIndex(HediffDef def, out int index)
	{
		if (def != null && sourceIndexesByDef.TryGetValue(def, out index))
		{
			return true;
		}

		index = -1;
		return false;
	}
}

internal static class MihoFixState
{
	internal const string MihoPackageId = "miho.fortifiedoutremer";
	internal const string NoJobRuleDefName = "Miho_CheckHediffMakeHediff_Nojob";
	internal const int AggregatorBucketTicks = 60;

	internal static readonly Dictionary<ThingDef, MihoRaceRuntime> RulesByRace = new Dictionary<ThingDef, MihoRaceRuntime>();

	internal static void Initialize()
	{
		RulesByRace.Clear();

		foreach (CheckHediffMakeHediffDef rule in DefDatabase<CheckHediffMakeHediffDef>.AllDefsListForReading)
		{
			if (rule?.targetRace == null)
			{
				continue;
			}

			if (!RulesByRace.TryGetValue(rule.targetRace, out MihoRaceRuntime runtime))
			{
				runtime = new MihoRaceRuntime();
				RulesByRace[rule.targetRace] = runtime;
			}

			runtime.AddRule(rule);
		}

		foreach (KeyValuePair<ThingDef, MihoRaceRuntime> entry in RulesByRace)
		{
			List<CompProperties> comps = entry.Key.comps ?? (entry.Key.comps = new List<CompProperties>());
			comps.RemoveAll(static comp => comp is CompProperties_CheckHediffMakeHediff);
			if (!comps.Any(static comp => comp is CompProperties_MihoHediffAggregator))
			{
				comps.Add(new CompProperties_MihoHediffAggregator());
			}
		}

		Log.Message($"[Miho Hediff Fix] Cached {RulesByRace.Values.Sum(static x => x.Rules.Count)} hediff rules across {RulesByRace.Count} target races.");
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
	private int[] remainingRuleTicks = Array.Empty<int>();
	private Dictionary<string, int> legacyRemainingTicksByRule = new Dictionary<string, int>();
	private List<string> savedRuleKeys;
	private List<int> savedRuleValues;
	private List<int> savedRemainingRuleTicks;
	private bool[] presentSourceDefs = Array.Empty<bool>();
	private MihoRaceRuntime activeRaceRuntime;
	private bool ruleStateReady;

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

		if (!Gen.IsHashIntervalTick(pawn, MihoFixState.AggregatorBucketTicks))
		{
			return;
		}

		ProcessRules();
	}

	public override void PostExposeData()
	{
		base.PostExposeData();
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			savedRemainingRuleTicks = remainingRuleTicks == null || remainingRuleTicks.Length == 0 ? null : new List<int>(remainingRuleTicks);
		}

		Scribe_Collections.Look(ref savedRemainingRuleTicks, "remainingRuleTicks", LookMode.Value);
		Scribe_Collections.Look(ref legacyRemainingTicksByRule, "remainingTicksByRule", LookMode.Value, LookMode.Value, ref savedRuleKeys, ref savedRuleValues);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			remainingRuleTicks = savedRemainingRuleTicks == null || savedRemainingRuleTicks.Count == 0 ? Array.Empty<int>() : savedRemainingRuleTicks.ToArray();
			legacyRemainingTicksByRule ??= new Dictionary<string, int>();
			activeRaceRuntime = null;
			ruleStateReady = false;
		}
	}

	private void EnsureRuleState()
	{
		if (pawn == null || !MihoFixState.RulesByRace.TryGetValue(pawn.def, out MihoRaceRuntime runtime))
		{
			activeRaceRuntime = null;
			presentSourceDefs = Array.Empty<bool>();
			remainingRuleTicks = Array.Empty<int>();
			legacyRemainingTicksByRule.Clear();
			ruleStateReady = false;
			return;
		}

		if (ReferenceEquals(activeRaceRuntime, runtime) && ruleStateReady && remainingRuleTicks != null && remainingRuleTicks.Length == runtime.Rules.Count)
		{
			return;
		}

		activeRaceRuntime = runtime;
		EnsureSourceBuffer();
		InitializeRemainingRuleTicks(runtime.Rules);
		ruleStateReady = true;
	}

	private void ProcessRules()
	{
		EnsureRuleState();
		if (pawn == null || activeRaceRuntime == null)
		{
			return;
		}

		RefreshPawnState();
		List<CheckHediffMakeHediffDef> rules = activeRaceRuntime.Rules;
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
				remainingRuleTicks[i] = resetTicks;
				continue;
			}

			int remainingTicks = remainingRuleTicks[i] - MihoFixState.AggregatorBucketTicks;
			if (remainingTicks > 0)
			{
				remainingRuleTicks[i] = remainingTicks;
				continue;
			}

			ApplyRule(rule);
			remainingRuleTicks[i] = resetTicks;
		}
	}

	private void RefreshPawnState()
	{
		EnsureSourceBuffer();
		Array.Clear(presentSourceDefs, 0, presentSourceDefs.Length);
		if (pawn?.health?.hediffSet?.hediffs == null || activeRaceRuntime == null)
		{
			return;
		}

		List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
		for (int i = 0; i < hediffs.Count; i++)
		{
			HediffDef def = hediffs[i]?.def;
			if (activeRaceRuntime.TryGetSourceIndex(def, out int sourceIndex))
			{
				presentSourceDefs[sourceIndex] = true;
			}
		}
	}

	private bool HasAnySourceHediff(CheckHediffMakeHediffDef rule)
	{
		if (rule?.hediffDefs == null || activeRaceRuntime == null)
		{
			return false;
		}

		for (int i = 0; i < rule.hediffDefs.Count; i++)
		{
			HediffDef hediffDef = rule.hediffDefs[i];
			if (hediffDef != null && activeRaceRuntime.TryGetSourceIndex(hediffDef, out int sourceIndex) && presentSourceDefs[sourceIndex])
			{
				return true;
			}
		}

		return false;
	}

	private void EnsureSourceBuffer()
	{
		int count = activeRaceRuntime?.RelevantSourceCount ?? 0;
		if (presentSourceDefs == null || presentSourceDefs.Length != count)
		{
			presentSourceDefs = count == 0 ? Array.Empty<bool>() : new bool[count];
		}
	}

	private void InitializeRemainingRuleTicks(List<CheckHediffMakeHediffDef> rules)
	{
		int[] previousTicks = remainingRuleTicks;
		if (previousTicks == null || previousTicks.Length != rules.Count)
		{
			remainingRuleTicks = new int[rules.Count];
			if (previousTicks != null && previousTicks.Length > 0)
			{
				Array.Copy(previousTicks, remainingRuleTicks, Math.Min(previousTicks.Length, rules.Count));
			}
		}

		for (int i = 0; i < rules.Count; i++)
		{
			if (remainingRuleTicks[i] > 0)
			{
				continue;
			}

			CheckHediffMakeHediffDef rule = rules[i];
			int resetTicks = Math.Max(rule.tickMultiflier, MihoFixState.AggregatorBucketTicks);
			if (!string.IsNullOrEmpty(rule.defName) && legacyRemainingTicksByRule.TryGetValue(rule.defName, out int legacyTicks) && legacyTicks > 0)
			{
				remainingRuleTicks[i] = legacyTicks;
				continue;
			}

			remainingRuleTicks[i] = resetTicks;
		}

		legacyRemainingTicksByRule.Clear();
	}

	private void ApplyRule(CheckHediffMakeHediffDef rule)
	{
		if (pawn?.health == null || rule?.makeHediffDefs == null || rule.hediffSeverity <= 0f)
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
}

[HarmonyPatch(typeof(ThingComp_CheckHediffMakeHediff), nameof(ThingComp_CheckHediffMakeHediff.CompTick))]
public static class Patch_ThingComp_CheckHediffMakeHediff_CompTick
{
	public static bool Prefix(ThingComp_CheckHediffMakeHediff __instance)
	{
		Pawn pawn = __instance.parent as Pawn;
		if (pawn != null && MihoFixState.RulesByRace.ContainsKey(pawn.def))
		{
			return false;
		}

		return true;
	}
}
