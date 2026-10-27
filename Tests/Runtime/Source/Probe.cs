using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using RimWorld;
using Verse;
using CheckHediffMakeHediff;
using Zades.MihoHediffFix;

namespace MihoRuntimeValidation;

public class ProbeMod : Mod
{
    public static readonly string Report = Environment.GetEnvironmentVariable("MIHO_TEST_REPORT");
    public static readonly string Phase = Environment.GetEnvironmentVariable("MIHO_TEST_PHASE") ?? "prepare";
    public static int Errors;
    public static void Write(object row) { lock(typeof(ProbeMod)) File.AppendAllText(Report, MiniJson.Serialize(row)+"\n"); }
    public static void Check(bool ok, string id, object detail = null) {
        Write(new {type = ok ? "pass" : "fail", id, detail});
        if (!ok) throw new Exception(id);
    }
    public ProbeMod(ModContentPack content) : base(content) {
        new Harmony("local.miho.runtimeprobe").Patch(AccessTools.Method(typeof(Log), nameof(Log.Error), new[]{typeof(string)}), prefix:new HarmonyMethod(typeof(ProbeMod), nameof(Error)));
        Write(new {type="constructor", version=VersionControl.CurrentVersionString, phase=Phase});
    }
    static void Error(string text) { Errors++; Write(new {type="game_error",text}); }
}

public class ProbeGame : GameComponent
{
    bool done; int frames;
    Pawn pawn; string pawnId; List<int> expectedTicks;
    static readonly Type CompType = typeof(Comp_MihoHediffAggregator);
    static int[] Ticks(ThingComp comp) => (int[])AccessTools.Field(CompType, "remainingRuleTicks").GetValue(comp);
    static void Process(ThingComp comp) => AccessTools.Method(CompType,"ProcessRules").Invoke(comp,null);
    static void Check(bool ok,string id,object detail=null) => ProbeMod.Check(ok,id,detail);
    public ProbeGame(Game game) {}
    public override void ExposeData() {
        Scribe_References.Look(ref pawn,"mihoProbePawn");
        Scribe_Values.Look(ref pawnId,"mihoProbePawnId");
        Scribe_Collections.Look(ref expectedTicks,"mihoProbeExpectedTicks",LookMode.Value);
    }
    static string Hash(string path) { using(var sha=SHA256.Create()) return string.Concat(sha.ComputeHash(File.ReadAllBytes(path)).Select(b=>b.ToString("x2"))); }
    void Startup() {
        var asm=typeof(Comp_MihoHediffAggregator).Assembly;
        Check(AppDomain.CurrentDomain.GetAssemblies().Count(a=>a.GetName().Name=="MihoHediffFix")==1,"single_mod_assembly");
        Check(Hash(asm.Location)==Environment.GetEnvironmentVariable("MIHO_EXPECTED_HASH"),"identical_dll_hash",Hash(asm.Location));
        var mod=LoadedModManager.RunningModsListForReading.Single(m=>m.PackageId=="zades.mihohedifffix");
        Check(mod.foldersToLoadDescendingOrder.Count()==1 && Path.GetFullPath(mod.foldersToLoadDescendingOrder.Single()).TrimEnd('/')==Path.GetFullPath(mod.RootDir).TrimEnd('/'),"root_load_folder",mod.foldersToLoadDescendingOrder);
        var patch=Harmony.GetPatchInfo(AccessTools.Method(typeof(ThingComp_CheckHediffMakeHediff),"CompTick"));
        Check(patch?.Prefixes.Any(p=>p.owner=="zades.mihohedifffix")==true,"harmony_prefix_installed");
        foreach(var race in new[]{ThingDef.Named("Alien_Miho"),ThingDef.Named("Miho_Abomi")}) {
            Check(race.comps.Count(c=>c is CompProperties_MihoHediffAggregator)==1,"single_aggregator_"+race.defName);
            Check(!race.comps.Any(c=>c is CompProperties_CheckHediffMakeHediff || c.compClass?.FullName=="MihoOptimization.Comp_MihoHediffAggregator"),"original_comps_removed_"+race.defName);
        }
    }
    void Prepare() {
        var kind=PawnKindDef.Named("Miho_PlayerColonistNonMagic");
        pawn=PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind,Faction.OfPlayer,forceGenerateNewPawn:true,forceNoIdeo:true));
        GenSpawn.Spawn(pawn,Find.CurrentMap.Center+new IntVec3(4,0,0),Find.CurrentMap);pawnId=pawn.ThingID;
        var comp=pawn.TryGetComp<Comp_MihoHediffAggregator>();
        Check(comp!=null && pawn.AllComps.Count(c=>c is Comp_MihoHediffAggregator)==1,"spawned_pawn_aggregator");
        var rules=DefDatabase<CheckHediffMakeHediffDef>.AllDefsListForReading.Where(r=>r.targetRace==pawn.def).ToList();
        Check(rules.Count==10 && Ticks(comp).Length==rules.Count,"all_ten_miho_rules_cached",rules.Select(r=>r.defName).ToArray());
        foreach(var h in pawn.health.hediffSet.hediffs.Where(h=>rules.Any(r=>r.hediffDefs.Contains(h.def))).ToList())pawn.health.RemoveHediff(h);
        Process(comp);
        Check(Ticks(comp).SequenceEqual(rules.Select(r=>Math.Max(r.tickMultiflier,60))),"missing_sources_reset_timers");
        var tea=rules.Single(r=>r.defName=="Miho_CheckHediffMakeHediff_Tea");int index=rules.IndexOf(tea);
        var source=pawn.health.AddHediff(tea.hediffDefs[0]);var target=tea.makeHediffDefs[0];
        float Before()=>pawn.health.hediffSet.GetFirstHediffOfDef(target)?.Severity??0f;
        var targetHediff=pawn.health.hediffSet.GetFirstHediffOfDef(target) ?? pawn.health.AddHediff(target);
        targetHediff.Severity=0.2f; // Miho initializes this hediff at its maximum severity.
        float before=Before();Ticks(comp)[index]=120;Process(comp);
        Check(Ticks(comp)[index]==60 && Math.Abs(Before()-before)<0.0001f,"timer_waits_before_due");
        Process(comp);
        Check(Math.Abs(Before()-before-tea.hediffSeverity)<0.0001f && Ticks(comp)[index]==tea.tickMultiflier,"due_rule_adds_severity_once",new {before,after=Before(),delta=tea.hediffSeverity,timer=Ticks(comp)[index]});
        pawn.health.RemoveHediff(source);before=Before();Ticks(comp)[index]=60;Process(comp);
        Check(Math.Abs(Before()-before)<0.0001f && Ticks(comp)[index]==tea.tickMultiflier,"removed_source_stops_rule");
        // Exercise the actual public tick hook across the hash interval without advancing other pawn systems.
        source=pawn.health.AddHediff(tea.hediffDefs[0]);Ticks(comp)[index]=6000;
        for(int i=0;i<60;i++){ Find.TickManager.DoSingleTick();comp.CompTick(); }
        Check(Ticks(comp)[index]<6000,"public_comp_tick_runs_in_game");
        // An old comp surviving on an existing pawn must be suppressed by the Harmony prefix.
        Ticks(comp)[index]=1777;before=Before();
        var old=new ThingComp_CheckHediffMakeHediff();old.parent=pawn;
        old.Initialize(new CompProperties_CheckHediffMakeHediff {targetRace=pawn.def,hediffDefs=tea.hediffDefs,makeHediffDefs=tea.makeHediffDefs,ticksToHediff=0,hediffSeverity=tea.hediffSeverity});
        old.PostSpawnSetup(false);old.CompTick();
        Check(Math.Abs(Before()-before)<0.0001f,"legacy_comp_tick_suppressed");
        ProbeMod.Write(new {type="comp_diagnostic", comps=pawn.AllComps.Select(c=>new {name=c.GetType().FullName, ticks=c is Comp_MihoHediffAggregator ? Ticks(c) : null}).ToArray(), defComps=pawn.def.comps.Select(c=>c.compClass.FullName).ToArray()});
        Check(pawn.AllComps.Count(c=>c.GetType().Name=="Comp_MihoHediffAggregator")==1,"single_aggregator_before_save");
        Ticks(comp)[index]=1777;expectedTicks=Ticks(comp).ToList();
        GameDataSaveLoader.SaveGame("MihoProbe");
        Check(File.Exists(Path.Combine(GenFilePaths.SaveDataFolderPath,"Saves/MihoProbe.rws")),"save_created");
    }
    void Resume() {
        Check(pawn!=null && pawn.ThingID==pawnId && pawn.Spawned,"saved_pawn_restored");
        var comp=pawn.TryGetComp<Comp_MihoHediffAggregator>();
        Check(comp!=null && Ticks(comp).SequenceEqual(expectedTicks),"rule_timers_survive_fresh_process_reload",Ticks(comp));
        Process(comp);
        var tea=DefDatabase<CheckHediffMakeHediffDef>.GetNamed("Miho_CheckHediffMakeHediff_Tea");
        var rules=DefDatabase<CheckHediffMakeHediffDef>.AllDefsListForReading.Where(r=>r.targetRace==pawn.def).ToList();
        Check(Ticks(comp)[rules.IndexOf(tea)]==1717,"restored_timer_continues");
    }
    public override void GameComponentUpdate() {
        if(done || Current.ProgramState!=ProgramState.Playing || Find.CurrentMap==null || LongEventHandler.ShouldWaitForEvent || ++frames<20)return;
        Find.TickManager.Pause();
        try {
            DebugSettings.enableStoryteller=false;
            ProbeMod.Write(new {type="start",version=VersionControl.CurrentVersionString,phase=ProbeMod.Phase});
            Startup();if(ProbeMod.Phase=="resume")Resume();else Prepare();
            done=true;ProbeMod.Write(new {type="complete",errors=ProbeMod.Errors});
        } catch(Exception e) {done=true;ProbeMod.Write(new {type="fatal",error=e.ToString()});}
    }
}
