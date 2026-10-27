using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections;
using HarmonyLib;
using RimWorld;
using Verse;
using CheckHediffMakeHediff;

namespace MihoRuntimeValidation;
public class ProbeMod : Mod {
 public static string Report=Environment.GetEnvironmentVariable("MIHO_TEST_REPORT");
 public static string Mode=Environment.GetEnvironmentVariable("MIHO_MODE");
 public static string Phase=Environment.GetEnvironmentVariable("MIHO_TEST_PHASE");
 public static int Errors;
 public static readonly int WarmupMs=int.TryParse(Environment.GetEnvironmentVariable("MIHO_WARMUP_MS"),out int warmup)?Math.Max(0,warmup):2000;
 public static void Write(object row){lock(typeof(ProbeMod))File.AppendAllText(Report,MiniJson.Serialize(row)+"\n");}
 public static void Check(bool ok,string id,object detail=null){Write(new{type=ok?"pass":"fail",id,detail});if(!ok)throw new Exception(id);}
 public ProbeMod(ModContentPack content):base(content){
  new Harmony("local.miho.comparison").Patch(AccessTools.Method(typeof(Log),nameof(Log.Error),new[]{typeof(string)}),prefix:new HarmonyMethod(typeof(ProbeMod),nameof(Error)));
  Write(new{type="constructor",mode=Mode,phase=Phase,version=VersionControl.CurrentVersionString});
 }
 static void Error(string text){Errors++;Write(new{type="game_error",text});}
}
[StaticConstructorOnStartup]
public static class Selection {
 static Selection(){
  var upstream=AccessTools.TypeByName("MihoOptimization.MihoOptimizationCore");
  if(upstream!=null)RuntimeHelpers.RunClassConstructor(upstream.TypeHandle);
  if(ProbeMod.Mode!="upstream")RuntimeHelpers.RunClassConstructor(AccessTools.TypeByName("Zades.MihoHediffFix.MihoHediffFixBootstrap").TypeHandle);
  // The original standalone DLL predates bundled-optimizer coexistence handling.
  if(ProbeMod.Mode=="original")foreach(var d in DefDatabase<ThingDef>.AllDefsListForReading)
   d.comps?.RemoveAll(c=>c.compClass?.FullName=="MihoOptimization.Comp_MihoHediffAggregator");
 }
}
public class ProbeGame : GameComponent {
 static readonly string[] Families={"Huntress","Shamaness","Summoner","Temptress","Tinker","Celestial"};
 static readonly HashSet<string> Jobs=new HashSet<string>(Families.SelectMany(f=>new[]{"I","II","III","IV","V"}.Select(l=>"Miho_"+f+"_"+l+"_Hediff")));
 static readonly HashSet<string> Starts=new HashSet<string>(Families.Select(f=>"Miho_"+f+"_Start_Hediff"));
 Pawn pawn;ThingComp[] work,aggregators;List<CheckHediffMakeHediffDef> rules;
 bool done;int frames;IEnumerator suite;string scenario;int elapsed;int calls;int starterAdds;int starterRemoves;bool instrument;
 Dictionary<string,int> acquired;Dictionary<string,float> peaks;List<object> timeline;
 public ProbeGame(Game game){}
 static bool IsAgg(ThingComp c)=>c.GetType().Name=="Comp_MihoHediffAggregator";
 static int[] Timers(ThingComp c)=>(int[])AccessTools.Field(c.GetType(),"remainingRuleTicks").GetValue(c);
 static string Hash(string p){using(var s=SHA256.Create())return string.Concat(s.ComputeHash(File.ReadAllBytes(p)).Select(x=>x.ToString("x2")));}
 float Sev(string name)=>pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named(name))?.Severity??0;
 void Reset(){
  instrument=false;
  foreach(var h in pawn.health.hediffSet.hediffs.Where(h=>Jobs.Contains(h.def.defName)||Starts.Contains(h.def.defName)||h.def.defName=="Miho_Nojob"||h.def.defName.StartsWith("Miho_PositiveFlower_Child")).ToList())pawn.health.RemoveHediff(h);
  foreach(var c in aggregators){var a=Timers(c);for(int i=0;i<a.Length;i++)a[i]=Math.Max(60,rules[i].tickMultiflier);}
  pawn.mindState.mentalStateHandler.Reset();
  acquired=new Dictionary<string,int>();peaks=new Dictionary<string,float>();timeline=new List<object>();calls=0;starterAdds=0;starterRemoves=0;
 }
 void Pill(string family){
  var item=ThingMaker.MakeThing(ThingDef.Named("Miho_PositiveFlower_Child"+family));item.stackCount=1;
  item.Ingested(pawn,0f);
  ProbeMod.Write(new{type="ingested",scenario,elapsed,family,sourceSeverity=Sev("Miho_PositiveFlower_Child"+family+"_High")});
 }
 static ProbeGame Active;
 public static void Ambient(Thing __instance,ref float __result){if(Active?.pawn==__instance)__result=21f;}
 public static void Adjust(Pawn pawn,HediffDef hdDef){if(Active?.instrument==true&&pawn==Active.pawn&&Starts.Contains(hdDef.defName))Active.calls++;}
 public static void Added(Hediff hediff){if(Active?.instrument==true&&hediff.pawn==Active.pawn&&Starts.Contains(hediff.def.defName))Active.starterAdds++;}
 public static void Removed(Hediff hediff){if(Active?.instrument==true&&hediff.pawn==Active.pawn&&Starts.Contains(hediff.def.defName))Active.starterRemoves++;}
 void Observe(bool snapshot=false){
  foreach(var h in pawn.health.hediffSet.hediffs){
   if(Jobs.Contains(h.def.defName)&&!acquired.ContainsKey(h.def.defName)){acquired[h.def.defName]=elapsed;ProbeMod.Write(new{type="acquired",scenario,elapsed,days=elapsed/60000.0,job=h.def.defName});}
   if(Starts.Contains(h.def.defName)){if(!peaks.ContainsKey(h.def.defName)||peaks[h.def.defName]<h.Severity)peaks[h.def.defName]=h.Severity;}
  }
  if(snapshot)timeline.Add(new{elapsed,days=elapsed/60000.0,hediffs=pawn.health.hediffSet.hediffs.Where(h=>Starts.Contains(h.def.defName)||Jobs.Contains(h.def.defName)||h.def.defName=="Miho_Nojob"||h.def.defName.Contains("Child")&&h.def.defName.EndsWith("_High")).Select(h=>new{name=h.def.defName,severity=h.Severity}).ToArray()});
 }
 IEnumerator Case(string id,string family,int seed,string variant="single",int step=60,int limitDays=120){
  scenario=id;elapsed=0;Rand.PushState(seed);Reset();instrument=true;
  int start=100000000;Find.TickManager.DebugSetTicksGame(start);while(!Gen.IsHashIntervalTick(pawn,60))Find.TickManager.DebugSetTicksGame(++start);
  if(variant!="control")Pill(family);
  if(variant=="overcharged")pawn.health.AddHediff(HediffDef.Named("Miho_"+family+"_Start_Hediff")).Severity=1.5f;
  Rand.PopState();
  int secondAt=variant=="repeat"?6000:variant=="two"?600:int.MaxValue;bool secondDone=false;int firstCareer=-1;int lastNewJob=-1;int lastCount=0;var sw=Stopwatch.StartNew();
  for(elapsed=step;elapsed<=limitDays*60000;elapsed+=step){
   Rand.PushState(unchecked(seed+elapsed*31));
   Find.TickManager.DebugSetTicksGame(start+elapsed);
   if(!secondDone&&elapsed>=secondAt){Pill(variant=="repeat"?family:"Shamaness");secondDone=true;}
   foreach(var c in work)c.CompTick();
   pawn.health.HealthTickInterval(step);
   Observe(elapsed%6000==0);
   Rand.PopState();
   if(acquired.Count!=lastCount){lastCount=acquired.Count;lastNewJob=elapsed;}
   if(firstCareer<0&&acquired.Count>0){firstCareer=elapsed;if(variant=="after")secondAt=elapsed+1200;}
   if(pawn.Dead){ProbeMod.Write(new{type="fixture_death",scenario,elapsed,hediffs=pawn.health.hediffSet.hediffs.Select(h=>new{name=h.def.defName,severity=h.Severity,part=h.Part?.def.defName}).ToArray()});throw new Exception("Fixture died: "+id);}
   if(variant=="control"&&elapsed>=2*60000)break;
   bool anyStart=pawn.health.hediffSet.hediffs.Any(h=>Starts.Contains(h.def.defName));
   if(firstCareer>=0&&elapsed>lastNewJob+6000){
    if(variant!="after"&&variant!="two")break;
    if(secondDone&&!anyStart&&elapsed>secondAt+3*6000)break;
    if(acquired.Keys.Select(n=>Families.First(f=>n.StartsWith("Miho_"+f+"_"))).Distinct().Count()>1)break;
   }
   if(elapsed%(step==1?6000:60000)==0)yield return null;
  }
  sw.Stop();Observe(true);instrument=false;
  ProbeMod.Write(new{type="case",id,family,seed,variant,step,elapsed,days=elapsed/60000.0,firstClassDay=firstCareer<0?(double?)null:firstCareer/60000.0,acquired,peaks,starterAdjustCalls=calls,starterAdds,starterRemoves,finalJobs=pawn.health.hediffSet.hediffs.Where(h=>Jobs.Contains(h.def.defName)).Select(h=>h.def.defName).ToArray(),remainingStarters=pawn.health.hediffSet.hediffs.Where(h=>Starts.Contains(h.def.defName)).Select(h=>new{name=h.def.defName,severity=h.Severity}).ToArray(),nojob=Sev("Miho_Nojob"),wallMs=sw.Elapsed.TotalMilliseconds,timeline});
 }
 IEnumerator Benchmark(){
  instrument=false;Reset();var temp=new List<Hediff>();
  var def=new HediffDef{defName="LocalCareerBenchmarkFiller",label="filler",hediffClass=typeof(Hediff)};
  while(pawn.health.hediffSet.hediffs.Count<64){var h=new Hediff{def=def,pawn=pawn};temp.Add(h);pawn.health.hediffSet.hediffs.Add(h);}
  foreach(string state in new[]{"idle","established","unlocking"}){
   foreach(var h in pawn.health.hediffSet.hediffs.Where(h=>Jobs.Contains(h.def.defName)||Starts.Contains(h.def.defName)).ToList())pawn.health.RemoveHediff(h);
   if(state=="established")pawn.health.AddHediff(HediffDef.Named("Miho_Huntress_I_Hediff"));
   if(state=="unlocking")pawn.health.AddHediff(HediffDef.Named("Miho_Huntress_Start_Hediff")).Severity=0.6f;
   int tick=Find.TickManager.TicksGame;while(!Gen.IsHashIntervalTick(pawn,60))Find.TickManager.DebugSetTicksGame(++tick);
   foreach(var c in aggregators)Array.Fill(Timers(c),1000000000);
   // Only aggregator methods: the original ten stub comps are excluded in every mode.
   for(int j=0;j<100000;j++)foreach(var c in aggregators)c.CompTick();
   // Let tiered JIT/PGO settle even when this process skips the lifecycle cases.
   var warmup=Stopwatch.StartNew();
   while(warmup.ElapsedMilliseconds<ProbeMod.WarmupMs)
    for(int j=0;j<10000;j++)foreach(var c in aggregators)c.CompTick();
   var ns=new List<double>();var bytes=new List<double>();const int n=200000;
   for(int sample=0;sample<9;sample++){
    foreach(var c in aggregators)Array.Fill(Timers(c),1000000000);
    var sw=new Stopwatch();long alloc=GC.GetAllocatedBytesForCurrentThread();sw.Start();
    for(int j=0;j<n;j++)foreach(var c in aggregators)c.CompTick();
    sw.Stop();ns.Add(sw.Elapsed.TotalMilliseconds*1000000/n);bytes.Add((GC.GetAllocatedBytesForCurrentThread()-alloc)/(double)n);yield return null;
   }
   ProbeMod.Write(new{type="benchmark",state,warmupMs=ProbeMod.WarmupMs,hediffs=pawn.health.hediffSet.hediffs.Count,iterations=n,samples=9,ns=ns.OrderBy(x=>x).ElementAt(4),bytes=bytes.OrderBy(x=>x).ElementAt(4),timingSamples=ns});
  }
  foreach(var h in temp)pawn.health.hediffSet.hediffs.Remove(h);
 }
 IEnumerator Run(){
  Active=this;var harmony=new Harmony("local.miho.lifecycle.counts");
  harmony.Patch(AccessTools.PropertyGetter(typeof(Thing),"AmbientTemperature"),postfix:new HarmonyMethod(typeof(ProbeGame),nameof(Ambient)));
  harmony.Patch(AccessTools.Method(typeof(HealthUtility),nameof(HealthUtility.AdjustSeverity)),prefix:new HarmonyMethod(typeof(ProbeGame),nameof(Adjust)));
  harmony.Patch(AccessTools.Method(typeof(Pawn_HealthTracker),nameof(Pawn_HealthTracker.AddHediff),new[]{typeof(Hediff),typeof(BodyPartRecord),typeof(DamageInfo?),typeof(DamageWorker.DamageResult)}),postfix:new HarmonyMethod(typeof(ProbeGame),nameof(Added)));
  harmony.Patch(AccessTools.Method(typeof(Pawn_HealthTracker),nameof(Pawn_HealthTracker.RemoveHediff)),prefix:new HarmonyMethod(typeof(ProbeGame),nameof(Removed)));
  pawn=PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDef.Named("Miho_PlayerColonistNonMagic"),Faction.OfPlayer,forceGenerateNewPawn:true,forceNoIdeo:true,fixedBiologicalAge:10,fixedChronologicalAge:10,developmentalStages:DevelopmentalStage.Child));
  GenSpawn.Spawn(pawn,Find.CurrentMap.Center+new IntVec3(9,0,0),Find.CurrentMap);
  rules=DefDatabase<CheckHediffMakeHediffDef>.AllDefsListForReading.Where(r=>r.targetRace==pawn.def).ToList();aggregators=pawn.AllComps.Where(IsAgg).ToArray();work=pawn.AllComps.Where(c=>IsAgg(c)||c is ThingComp_CheckHediffMakeHediff).ToArray();
  ProbeMod.Check(aggregators.Length==1,"aggregator_count");
  if(ProbeMod.Mode!="upstream")ProbeMod.Check(Hash(aggregators[0].GetType().Assembly.Location)==Environment.GetEnvironmentVariable("MIHO_EXPECTED_HASH"),"expected_mod_dll_hash");
  ProbeMod.Write(new{type="fixture",age=pawn.ageTracker.AgeBiologicalYears,development=pawn.DevelopmentalStage.ToString(),work=work.Select(c=>c.GetType().FullName).ToArray(),assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="MihoOptimization"||a.GetName().Name=="MihoHediffFix").Select(a=>new{name=a.GetName().Name,hash=Hash(a.Location)}).ToArray()});
  if(ProbeMod.Phase!="repeat") {
  foreach(var family in Families){var e=Case("single_"+family,family,2107);while(e.MoveNext())yield return null;}
  foreach(int seed in new[]{3121,4099}){var e=Case("single_Huntress_"+seed,"Huntress",seed);while(e.MoveNext())yield return null;}
  foreach(string variant in new[]{"repeat","two","after","overcharged","control"})foreach(int seed in new[]{2107,3121,4099}){
   var e=Case(variant+"_"+seed,"Huntress",seed,variant);while(e.MoveNext())yield return null;
  }
  // Fine-step validation of the accelerated interval simulation.
  var fine=Case("single_Huntress_step1","Huntress",2107,"single",1);while(fine.MoveNext())yield return null;
  }
  var benchmark=Benchmark();while(benchmark.MoveNext())yield return null;
 }
 public override void GameComponentUpdate(){
  if(done||Current.ProgramState!=ProgramState.Playing||Find.CurrentMap==null||LongEventHandler.ShouldWaitForEvent||++frames<20)return;
  Find.TickManager.Pause();try{
   if(suite==null){DebugSettings.enableStoryteller=false;suite=Run();}
   if(suite.MoveNext())return;done=true;ProbeMod.Write(new{type="complete",errors=ProbeMod.Errors});
  }catch(Exception e){done=true;ProbeMod.Write(new{type="fatal",error=e.ToString()});}
 }
}
