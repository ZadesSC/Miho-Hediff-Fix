using System;
using System.Collections.Generic;
using CheckHediffMakeHediff;
using Verse;

namespace Zades.MihoHediffFix;

internal enum MihoClassHediffKind : byte
{
	None,
	Established,
	Starter,
	NoJob
}

internal readonly struct MihoTrackedHediff
{
	internal readonly int SourceIndex;
	internal readonly MihoClassHediffKind Kind;
	internal readonly int StarterIndex;

	internal MihoTrackedHediff(int sourceIndex, MihoClassHediffKind kind = MihoClassHediffKind.None, int starterIndex = -1)
	{
		SourceIndex = sourceIndex;
		Kind = kind;
		StarterIndex = starterIndex;
	}
}

internal readonly struct MihoRuleOutput
{
	internal readonly HediffDef Def;
	internal readonly int StarterIndex;

	internal MihoRuleOutput(HediffDef def, int starterIndex)
	{
		Def = def;
		StarterIndex = starterIndex;
	}
}

internal sealed class MihoRuleRuntime
{
	internal readonly CheckHediffMakeHediffDef Def;
	internal readonly int ResetTicks;
	internal readonly int[] SourceIndexes;
	internal readonly MihoRuleOutput[] Outputs;
	internal readonly bool IsClassRule;

	internal MihoRuleRuntime(CheckHediffMakeHediffDef def, int[] sourceIndexes, MihoRuleOutput[] outputs, bool isClassRule)
	{
		Def = def;
		ResetTicks = Math.Max(def.tickMultiflier, MihoFixState.AggregatorBucketTicks);
		SourceIndexes = sourceIndexes;
		Outputs = outputs;
		IsClassRule = isClassRule;
	}
}

internal sealed class MihoRaceRuntime
{
	private readonly Dictionary<HediffDef, MihoTrackedHediff> trackedHediffs = new Dictionary<HediffDef, MihoTrackedHediff>();

	internal List<MihoRuleRuntime> Rules { get; } = new List<MihoRuleRuntime>();
	internal int RelevantSourceCount { get; private set; }
	internal bool HasClassRules { get; private set; }

	internal void AddRule(CheckHediffMakeHediffDef rule)
	{
		var sources = new List<int>();
		if (rule.hediffDefs != null)
		{
			foreach (HediffDef def in rule.hediffDefs)
			{
				if (def == null)
				{
					continue;
				}
				if (!trackedHediffs.TryGetValue(def, out MihoTrackedHediff tracked))
				{
					tracked = new MihoTrackedHediff(RelevantSourceCount++);
					trackedHediffs.Add(def, tracked);
				}
				sources.Add(tracked.SourceIndex);
			}
		}

		var outputs = new List<MihoRuleOutput>();
		bool isClassRule = rule.defName == MihoFixState.NoJobRuleDefName;
		if (rule.makeHediffDefs != null)
		{
			foreach (HediffDef def in rule.makeHediffDefs)
			{
				if (def == null)
				{
					continue;
				}
				int starterIndex = MihoFixState.ClassDefinitions.TryGetValue(def, out MihoTrackedHediff tracked)
					&& tracked.Kind == MihoClassHediffKind.Starter ? tracked.StarterIndex : -1;
				isClassRule |= starterIndex >= 0;
				outputs.Add(new MihoRuleOutput(def, starterIndex));
			}
		}
		HasClassRules |= isClassRule;
		Rules.Add(new MihoRuleRuntime(rule, sources.ToArray(), outputs.ToArray(), isClassRule));
	}

	internal void RegisterClassDefinitions()
	{
		if (!HasClassRules)
		{
			return;
		}
		foreach (KeyValuePair<HediffDef, MihoTrackedHediff> entry in MihoFixState.ClassDefinitions)
		{
			int sourceIndex = trackedHediffs.TryGetValue(entry.Key, out MihoTrackedHediff source) ? source.SourceIndex : -1;
			trackedHediffs[entry.Key] = new MihoTrackedHediff(sourceIndex, entry.Value.Kind, entry.Value.StarterIndex);
		}
	}

	internal bool TryGetTrackedHediff(HediffDef def, out MihoTrackedHediff tracked)
	{
		if (def != null)
		{
			return trackedHediffs.TryGetValue(def, out tracked);
		}
		tracked = default;
		return false;
	}
}
