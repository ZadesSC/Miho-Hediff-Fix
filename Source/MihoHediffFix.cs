using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
	internal const int ClassFamilyCount = 6;
	internal static readonly Dictionary<HediffDef, MihoTrackedHediff> ClassDefinitions = new Dictionary<HediffDef, MihoTrackedHediff>();

	private static void CacheClassDefinitions()
	{
		ClassDefinitions.Clear();
		string[] families = { "Huntress", "Shamaness", "Summoner", "Temptress", "Tinker", "Celestial" };
		string[] levels = { "I", "II", "III", "IV", "V" };
		for (int i = 0; i < families.Length; i++)
		{
			RegisterClassDef("Miho_" + families[i] + "_Start_Hediff", MihoClassHediffKind.Starter, i);
			foreach (string level in levels)
			{
				RegisterClassDef("Miho_" + families[i] + "_" + level + "_Hediff", MihoClassHediffKind.Established);
			}
		}
		RegisterClassDef("Miho_Nojob", MihoClassHediffKind.NoJob);
	}

	private static void RegisterClassDef(string name, MihoClassHediffKind kind, int starterIndex = -1)
	{
		HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(name);
		if (def != null)
		{
			ClassDefinitions[def] = new MihoTrackedHediff(-1, kind, starterIndex);
		}
	}

	internal static readonly Dictionary<ThingDef, MihoRaceRuntime> RulesByRace = new Dictionary<ThingDef, MihoRaceRuntime>();

	internal static void Initialize()
	{
		// Recent Miho builds include their own aggregator. Initialize it first so
		// replacement is independent of startup-constructor ordering; leave its
		// unrelated patches enabled.
		Type upstreamBootstrap = AccessTools.TypeByName("MihoOptimization.MihoOptimizationCore");
		if (upstreamBootstrap != null)
		{
			RuntimeHelpers.RunClassConstructor(upstreamBootstrap.TypeHandle);
		}

		RulesByRace.Clear();
		CacheClassDefinitions();

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
			entry.Value.RegisterClassDefinitions();
			List<CompProperties> comps = entry.Key.comps ?? (entry.Key.comps = new List<CompProperties>());
			comps.RemoveAll(static comp => comp is CompProperties_CheckHediffMakeHediff
				|| comp.compClass?.FullName == "MihoOptimization.Comp_MihoHediffAggregator");
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
	private bool hasClass;
	private bool hasStarter;
	private Hediff[] classStarters = Array.Empty<Hediff>();
	private List<Hediff> classCleanup;

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
		List<MihoRuleRuntime> rules = activeRaceRuntime.Rules;
		for (int i = 0; i < rules.Count; i++)
		{
			MihoRuleRuntime rule = rules[i];
			if (string.IsNullOrEmpty(rule.Def.defName) || (rule.IsClassRule && hasClass))
			{
				continue;
			}

			int resetTicks = rule.ResetTicks;
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
		Array.Clear(presentSourceDefs, 0, presentSourceDefs.Length);
		Array.Clear(classStarters, 0, classStarters.Length);
		classCleanup?.Clear();
		hasClass = false;
		hasStarter = false;
		List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
		for (int i = 0; i < hediffs.Count; i++)
		{
			Hediff hediff = hediffs[i];
			if (!activeRaceRuntime.TryGetTrackedHediff(hediff?.def, out MihoTrackedHediff tracked))
			{
				continue;
			}
			if (tracked.SourceIndex >= 0)
			{
				presentSourceDefs[tracked.SourceIndex] = true;
			}
			switch (tracked.Kind)
			{
				case MihoClassHediffKind.Established:
					hasClass = true;
					break;
				case MihoClassHediffKind.Starter:
					hasStarter = true;
					classStarters[tracked.StarterIndex] = hediff;
					QueueClassCleanup(hediff);
					break;
				case MihoClassHediffKind.NoJob:
					QueueClassCleanup(hediff);
					break;
			}
		}

		if (hasClass && classCleanup != null && classCleanup.Count != 0)
		{
			for (int i = 0; i < classCleanup.Count; i++)
			{
				Hediff hediff = classCleanup[i];
				pawn.health.RemoveHediff(hediff);
				if (activeRaceRuntime.TryGetTrackedHediff(hediff.def, out MihoTrackedHediff tracked) && tracked.SourceIndex >= 0)
				{
					presentSourceDefs[tracked.SourceIndex] = false;
				}
			}
			classCleanup.Clear();
			Array.Clear(classStarters, 0, classStarters.Length);
			hasStarter = false;
		}
	}

	private void QueueClassCleanup(Hediff hediff)
	{
		// Allocate at most once on the usual path; retain capacity across snapshots.
		(classCleanup ??= new List<Hediff>(MihoFixState.ClassFamilyCount + 1)).Add(hediff);
	}

	private bool HasAnySourceHediff(MihoRuleRuntime rule)
	{
		int[] indexes = rule.SourceIndexes;
		for (int i = 0; i < indexes.Length; i++)
		{
			if (presentSourceDefs[indexes[i]])
			{
				return true;
			}
		}
		return false;
	}

	private void EnsureSourceBuffer()
	{
		int starterCount = activeRaceRuntime?.HasClassRules == true ? MihoFixState.ClassFamilyCount : 0;
		if (classStarters.Length != starterCount)
		{
			classStarters = starterCount == 0 ? Array.Empty<Hediff>() : new Hediff[starterCount];
		}
		int count = activeRaceRuntime?.RelevantSourceCount ?? 0;
		if (presentSourceDefs == null || presentSourceDefs.Length != count)
		{
			presentSourceDefs = count == 0 ? Array.Empty<bool>() : new bool[count];
		}
	}

	private void InitializeRemainingRuleTicks(List<MihoRuleRuntime> rules)
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

			MihoRuleRuntime rule = rules[i];
			int resetTicks = rule.ResetTicks;
			if (!string.IsNullOrEmpty(rule.Def.defName) && legacyRemainingTicksByRule.TryGetValue(rule.Def.defName, out int legacyTicks) && legacyTicks > 0)
			{
				remainingRuleTicks[i] = legacyTicks;
				continue;
			}

			remainingRuleTicks[i] = resetTicks;
		}

		legacyRemainingTicksByRule.Clear();
	}

	private void ApplyRule(MihoRuleRuntime rule)
	{
		if (rule.Def.hediffSeverity <= 0f)
		{
			return;
		}
		MihoRuleOutput[] outputs = rule.Outputs;
		for (int i = 0; i < outputs.Length; i++)
		{
			MihoRuleOutput output = outputs[i];
			if (output.StarterIndex < 0)
			{
				HealthUtility.AdjustSeverity(pawn, output.Def, rule.Def.hediffSeverity);
				continue;
			}
			if (hasClass)
			{
				continue;
			}
			Hediff existing = classStarters[output.StarterIndex];
			if (existing != null)
			{
				// Repair legacy overfilled starters without topping up a progressing one.
				if (existing.Severity > 1f)
				{
					existing.Severity = 1f;
				}
				continue;
			}
			if (hasStarter)
			{
				continue;
			}
			HealthUtility.AdjustSeverity(pawn, output.Def, Math.Min(rule.Def.hediffSeverity, 1f));
			classStarters[output.StarterIndex] = pawn.health.hediffSet.GetFirstHediffOfDef(output.Def);
			// Later outputs/rules in this same interval must see the newly started class.
			hasStarter = true;
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
