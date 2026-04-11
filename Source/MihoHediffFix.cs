using System;
using System.Collections.Generic;
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

internal sealed class MihoRoleFamily
{
	internal MihoRoleFamily(int index, string name, HediffDef triggerDef, HediffDef startDef, List<HediffDef> tierDefs)
	{
		Index = index;
		Name = name;
		TriggerDef = triggerDef;
		StartDef = startDef;
		TierDefs = tierDefs;
		AllRoleDefs = new List<HediffDef>();
		if (startDef != null)
		{
			AllRoleDefs.Add(startDef);
		}

		for (int i = 0; i < tierDefs.Count; i++)
		{
			if (tierDefs[i] != null)
			{
				AllRoleDefs.Add(tierDefs[i]);
			}
		}
	}

	internal int Index { get; }

	internal string Name { get; }

	internal HediffDef TriggerDef { get; }

	internal HediffDef StartDef { get; }

	internal List<HediffDef> TierDefs { get; }

	internal List<HediffDef> AllRoleDefs { get; }
}

internal static class MihoFixState
{
	internal const string NoJobRuleDefName = "Miho_CheckHediffMakeHediff_Nojob";
	internal const int AggregatorBucketTicks = 60;

	private static readonly string[] RoleFamilyNames = new string[6]
	{
		"Huntress",
		"Shamaness",
		"Summoner",
		"Temptress",
		"Tinker",
		"Celestial"
	};

	private static readonly string[] TierSuffixes = new string[5]
	{
		"I",
		"II",
		"III",
		"IV",
		"V"
	};

	internal static readonly Dictionary<ThingDef, List<CheckHediffMakeHediffDef>> RulesByRace = new Dictionary<ThingDef, List<CheckHediffMakeHediffDef>>();
	internal static readonly List<MihoRoleFamily> RoleFamilies = new List<MihoRoleFamily>();
	internal static readonly Dictionary<HediffDef, MihoRoleFamily> RoleFamilyByRoleHediff = new Dictionary<HediffDef, MihoRoleFamily>();
	internal static readonly Dictionary<HediffDef, MihoRoleFamily> RoleFamilyByTrigger = new Dictionary<HediffDef, MihoRoleFamily>();
	internal static readonly Dictionary<HediffDef, int> TierLevelByDef = new Dictionary<HediffDef, int>();

	internal static HediffDef NoJobHediffDef;

	internal static void Initialize()
	{
		RulesByRace.Clear();
		RoleFamilies.Clear();
		RoleFamilyByRoleHediff.Clear();
		RoleFamilyByTrigger.Clear();
		TierLevelByDef.Clear();
		NoJobHediffDef = DefDatabase<HediffDef>.GetNamedSilentFail("Miho_Nojob");

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

		InitializeRoleFamilies();

		foreach (KeyValuePair<ThingDef, List<CheckHediffMakeHediffDef>> entry in RulesByRace)
		{
			List<CompProperties> comps = entry.Key.comps ?? (entry.Key.comps = new List<CompProperties>());
			comps.RemoveAll(static comp => comp is CompProperties_CheckHediffMakeHediff);
			if (!ContainsAggregatorComp(comps))
			{
				comps.Add(new CompProperties_MihoHediffAggregator());
			}
		}

		int totalRuleCount = 0;
		foreach (List<CheckHediffMakeHediffDef> rules in RulesByRace.Values)
		{
			totalRuleCount += rules.Count;
		}

		Log.Message($"[Miho Hediff Fix] Cached {totalRuleCount} hediff rules across {RulesByRace.Count} target races and {RoleFamilies.Count} role families.");
	}

	internal static bool TryGetRoleFamilyForRoleHediff(HediffDef def, out MihoRoleFamily family)
	{
		family = null;
		return def != null && RoleFamilyByRoleHediff.TryGetValue(def, out family);
	}

	internal static bool TryGetRoleFamilyForTrigger(HediffDef def, out MihoRoleFamily family)
	{
		family = null;
		return def != null && RoleFamilyByTrigger.TryGetValue(def, out family);
	}

	internal static bool TryGetRoleFamilyForRule(CheckHediffMakeHediffDef rule, out MihoRoleFamily family)
	{
		family = null;
		if (rule?.makeHediffDefs == null || rule.makeHediffDefs.Count != 1)
		{
			return false;
		}

		HediffDef targetDef = rule.makeHediffDefs[0];
		if (!TryGetRoleFamilyForRoleHediff(targetDef, out family))
		{
			return false;
		}

		return targetDef == family.StartDef;
	}

	internal static int GetTierLevel(HediffDef def)
	{
		return def != null && TierLevelByDef.TryGetValue(def, out int level) ? level : 0;
	}

	private static void InitializeRoleFamilies()
	{
		for (int i = 0; i < RoleFamilyNames.Length; i++)
		{
			string familyName = RoleFamilyNames[i];
			HediffDef triggerDef = DefDatabase<HediffDef>.GetNamedSilentFail("Miho_PositiveFlower_Child" + familyName + "_High");
			HediffDef startDef = DefDatabase<HediffDef>.GetNamedSilentFail("Miho_" + familyName + "_Start_Hediff");

			List<HediffDef> tierDefs = new List<HediffDef>(TierSuffixes.Length);
			for (int level = 0; level < TierSuffixes.Length; level++)
			{
				HediffDef tierDef = DefDatabase<HediffDef>.GetNamedSilentFail("Miho_" + familyName + "_" + TierSuffixes[level] + "_Hediff");
				tierDefs.Add(tierDef);
			}

			MihoRoleFamily family = new MihoRoleFamily(i, familyName, triggerDef, startDef, tierDefs);
			RoleFamilies.Add(family);

			if (triggerDef != null)
			{
				RoleFamilyByTrigger[triggerDef] = family;
			}

			if (startDef != null)
			{
				RoleFamilyByRoleHediff[startDef] = family;
			}

			for (int level = 0; level < family.TierDefs.Count; level++)
			{
				HediffDef tierDef = family.TierDefs[level];
				if (tierDef == null)
				{
					continue;
				}

				RoleFamilyByRoleHediff[tierDef] = family;
				TierLevelByDef[tierDef] = level + 1;
			}
		}
	}

	private static bool ContainsAggregatorComp(List<CompProperties> comps)
	{
		for (int i = 0; i < comps.Count; i++)
		{
			if (comps[i] is CompProperties_MihoHediffAggregator)
			{
				return true;
			}
		}

		return false;
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
	private readonly HashSet<HediffDef> presentDefs = new HashSet<HediffDef>();
	private readonly List<Hediff> hediffsToRemove = new List<Hediff>();
	private List<string> savedRuleKeys;
	private List<int> savedRuleValues;
	private bool[] roleHasStart;
	private bool[] roleHasTrigger;
	private int[] highestRoleTier;

	public override void PostSpawnSetup(bool respawningAfterLoad)
	{
		base.PostSpawnSetup(respawningAfterLoad);
		pawn = parent as Pawn;
		EnsureRoleBuffers();
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
		Scribe_Collections.Look(ref remainingTicksByRule, "remainingTicksByRule", LookMode.Value, LookMode.Value, ref savedRuleKeys, ref savedRuleValues);
		if (remainingTicksByRule == null)
		{
			remainingTicksByRule = new Dictionary<string, int>();
		}
	}

	private void EnsureRoleBuffers()
	{
		int count = MihoFixState.RoleFamilies.Count;
		if (roleHasStart == null || roleHasStart.Length != count)
		{
			roleHasStart = new bool[count];
		}

		if (roleHasTrigger == null || roleHasTrigger.Length != count)
		{
			roleHasTrigger = new bool[count];
		}

		if (highestRoleTier == null || highestRoleTier.Length != count)
		{
			highestRoleTier = new int[count];
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

		hediffsToRemove.Clear();
		foreach (string key in remainingTicksByRule.Keys)
		{
			if (!validRuleNames.Contains(key))
			{
				hediffsToRemove.Clear();
				break;
			}
		}

		List<string> staleKeys = new List<string>();
		foreach (string key in remainingTicksByRule.Keys)
		{
			if (!validRuleNames.Contains(key))
			{
				staleKeys.Add(key);
			}
		}

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

		RefreshPawnState();
		NormalizeRoleFamilies();

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

			bool changed = ApplyRule(rule);
			remainingTicksByRule[rule.defName] = resetTicks;
			if (changed)
			{
				RefreshPawnState();
				NormalizeRoleFamilies();
			}
		}
	}

	private void RefreshPawnState()
	{
		EnsureRoleBuffers();
		presentDefs.Clear();
		Array.Clear(roleHasStart, 0, roleHasStart.Length);
		Array.Clear(roleHasTrigger, 0, roleHasTrigger.Length);
		Array.Clear(highestRoleTier, 0, highestRoleTier.Length);

		if (pawn?.health?.hediffSet?.hediffs == null)
		{
			return;
		}

		List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
		for (int i = 0; i < hediffs.Count; i++)
		{
			Hediff hediff = hediffs[i];
			HediffDef def = hediff?.def;
			if (def == null)
			{
				continue;
			}

			presentDefs.Add(def);

			if (MihoFixState.TryGetRoleFamilyForTrigger(def, out MihoRoleFamily triggerFamily))
			{
				roleHasTrigger[triggerFamily.Index] = true;
			}

			if (!MihoFixState.TryGetRoleFamilyForRoleHediff(def, out MihoRoleFamily roleFamily))
			{
				continue;
			}

			if (def == roleFamily.StartDef)
			{
				roleHasStart[roleFamily.Index] = true;
			}

			int tierLevel = MihoFixState.GetTierLevel(def);
			if (tierLevel > highestRoleTier[roleFamily.Index])
			{
				highestRoleTier[roleFamily.Index] = tierLevel;
			}
		}
	}

	private void NormalizeRoleFamilies()
	{
		MihoRoleFamily winningFamily = SelectWinningRoleFamily(includeTriggers: true);
		if (winningFamily == null)
		{
			return;
		}

		bool changed = false;
		if (CountActiveRoleFamilies() > 1)
		{
			hediffsToRemove.Clear();
			List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
			for (int i = 0; i < hediffs.Count; i++)
			{
				Hediff hediff = hediffs[i];
				if (hediff?.def == null || !MihoFixState.TryGetRoleFamilyForRoleHediff(hediff.def, out MihoRoleFamily family))
				{
					continue;
				}

				if (family != winningFamily)
				{
					hediffsToRemove.Add(hediff);
				}
			}

			for (int i = 0; i < hediffsToRemove.Count; i++)
			{
				pawn.health.RemoveHediff(hediffsToRemove[i]);
			}

			changed = hediffsToRemove.Count > 0;
			hediffsToRemove.Clear();
		}

		if (MihoFixState.NoJobHediffDef != null && presentDefs.Contains(MihoFixState.NoJobHediffDef))
		{
			RemoveAllHediffsOfDef(MihoFixState.NoJobHediffDef);
			changed = true;
		}

		if (changed)
		{
			RefreshPawnState();
		}
	}

	private bool HasAnySourceHediff(CheckHediffMakeHediffDef rule)
	{
		if (rule?.hediffDefs == null)
		{
			return false;
		}

		for (int i = 0; i < rule.hediffDefs.Count; i++)
		{
			HediffDef hediffDef = rule.hediffDefs[i];
			if (hediffDef != null && presentDefs.Contains(hediffDef))
			{
				return true;
			}
		}

		return false;
	}

	private bool ApplyRule(CheckHediffMakeHediffDef rule)
	{
		if (pawn?.health == null || rule?.makeHediffDefs == null || rule.hediffSeverity <= 0f)
		{
			return false;
		}

		if (rule.defName == MihoFixState.NoJobRuleDefName)
		{
			return false;
		}

		if (MihoFixState.TryGetRoleFamilyForRule(rule, out MihoRoleFamily targetFamily))
		{
			return ApplyRoleStartRule(rule, targetFamily);
		}

		bool changed = false;
		for (int i = 0; i < rule.makeHediffDefs.Count; i++)
		{
			HediffDef hediffDef = rule.makeHediffDefs[i];
			if (ApplyRuleHediff(hediffDef, rule.hediffSeverity))
			{
				changed = true;
			}
		}

		return changed;
	}

	private bool ApplyRoleStartRule(CheckHediffMakeHediffDef rule, MihoRoleFamily targetFamily)
	{
		if (targetFamily?.StartDef == null)
		{
			return false;
		}

		if (HasAnyRoleState(targetFamily))
		{
			return false;
		}

		if (HasConflictingRoleState(targetFamily))
		{
			return false;
		}

		bool changed = ApplyRuleHediff(targetFamily.StartDef, rule.hediffSeverity);
		if (changed && MihoFixState.NoJobHediffDef != null && presentDefs.Contains(MihoFixState.NoJobHediffDef))
		{
			RemoveAllHediffsOfDef(MihoFixState.NoJobHediffDef);
		}

		return changed;
	}

	private bool ApplyRuleHediff(HediffDef hediffDef, float severity)
	{
		if (hediffDef == null || severity <= 0f || pawn?.health == null)
		{
			return false;
		}

		Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef, false);
		if (existing != null)
		{
			existing.Severity += severity;
			return true;
		}

		Hediff newHediff = HediffMaker.MakeHediff(hediffDef, pawn);
		newHediff.Severity = severity;
		pawn.health.AddHediff(newHediff);
		return true;
	}

	private bool HasAnyRoleState(MihoRoleFamily family)
	{
		if (family == null)
		{
			return false;
		}

		return roleHasStart[family.Index] || highestRoleTier[family.Index] > 0;
	}

	private bool HasConflictingRoleState(MihoRoleFamily targetFamily)
	{
		for (int i = 0; i < MihoFixState.RoleFamilies.Count; i++)
		{
			MihoRoleFamily family = MihoFixState.RoleFamilies[i];
			if (family == targetFamily)
			{
				continue;
			}

			if (HasAnyRoleState(family))
			{
				return true;
			}
		}

		return false;
	}

	private int CountActiveRoleFamilies()
	{
		int count = 0;
		for (int i = 0; i < MihoFixState.RoleFamilies.Count; i++)
		{
			if (roleHasStart[i] || highestRoleTier[i] > 0)
			{
				count++;
			}
		}

		return count;
	}

	private MihoRoleFamily SelectWinningRoleFamily(bool includeTriggers)
	{
		MihoRoleFamily bestFamily = null;
		int bestScore = int.MinValue;
		int bestTieBreak = int.MaxValue;

		for (int i = 0; i < MihoFixState.RoleFamilies.Count; i++)
		{
			MihoRoleFamily family = MihoFixState.RoleFamilies[i];
			bool hasActiveRoleState = roleHasStart[i] || highestRoleTier[i] > 0;
			if (!hasActiveRoleState && !(includeTriggers && roleHasTrigger[i]))
			{
				continue;
			}

			int score = highestRoleTier[i] * 100;
			if (roleHasStart[i])
			{
				score += 10;
			}

			if (includeTriggers && roleHasTrigger[i])
			{
				score += 1;
			}

			int tieBreak = GetFamilyTieBreak(family);
			if (score > bestScore || (score == bestScore && tieBreak < bestTieBreak))
			{
				bestFamily = family;
				bestScore = score;
				bestTieBreak = tieBreak;
			}
		}

		return bestFamily;
	}

	private int GetFamilyTieBreak(MihoRoleFamily family)
	{
		int familyCount = MihoFixState.RoleFamilies.Count;
		if (pawn == null || familyCount <= 0)
		{
			return family?.Index ?? 0;
		}

		int preferredIndex = pawn.thingIDNumber;
		if (preferredIndex < 0)
		{
			preferredIndex = -preferredIndex;
			if (preferredIndex < 0)
			{
				preferredIndex = 0;
			}
		}

		preferredIndex %= familyCount;
		int tieBreak = family.Index - preferredIndex;
		if (tieBreak < 0)
		{
			tieBreak += familyCount;
		}

		return tieBreak;
	}

	private void RemoveAllHediffsOfDef(HediffDef hediffDef)
	{
		if (hediffDef == null || pawn?.health == null)
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
