from pathlib import Path
import argparse,subprocess,os,time,json,hashlib,shutil,xml.etree.ElementTree as ET
REPO=Path(__file__).resolve().parents[2]
HARNESS=Path(__file__).resolve().parent
WORKSHOP=Path.home()/'Library/Application Support/Steam/steamapps/workshop/content/294100'
p=argparse.ArgumentParser();p.add_argument('version',choices=['1.6','1.7']);p.add_argument('mode',choices=['upstream','original','candidate']);p.add_argument('--warmup-ms',type=int,default=2000);p.add_argument('--reference-dll',type=Path);p.add_argument('--baseline',type=Path,required=True);p.add_argument('--repeat',action='store_true');p.add_argument('--tag');p.add_argument('--output',type=Path,required=True);args=p.parse_args()
ROOT=args.output.resolve();ROOT.mkdir(parents=True,exist_ok=True)
game=(Path.home()/'Games/RimWorld-ModTesting')/('Game-1.6' if args.version=='1.6' else 'Game')
tag=args.tag or args.version+'-'+args.mode
result=ROOT/'results'/tag;result.mkdir(parents=True,exist_ok=True)
profile=result/'profile';(profile/'Config').mkdir(parents=True,exist_ok=True)
phase='repeat' if args.repeat else 'prepare'
report=result/(phase+'.jsonl')
if report.exists():raise RuntimeError('Report exists: '+str(report))
def xml(path,tag,values):
 r=ET.Element(tag)
 for key,value in values.items():
  e=ET.SubElement(r,key)
  if isinstance(value,list):
   for v in value:ET.SubElement(e,'li').text=v
  else:e.text=str(value)
 ET.indent(r);ET.ElementTree(r).write(path,encoding='utf-8',xml_declaration=True)
probe=ROOT/('probe16' if args.version=='1.6' else 'probe17')
(probe/'About').mkdir(parents=True,exist_ok=True)
xml(probe/'About/About.xml','ModMetaData',{'name':'Miho runtime tests','author':'Local tests','packageId':'local.miho.runtimeprobe','supportedVersions':['1.6','1.7'],'description':'Disposable in-game validation.'})
mod=result/'mod'
assembly=(args.reference_dll if args.mode=='original' else REPO/'Assemblies/MihoHediffFix.dll')
if args.mode=='original' and (assembly is None or not assembly.is_file()):raise RuntimeError('--reference-dll must point to the original DLL')
if not mod.exists():
 (mod/'About').mkdir(parents=True);(mod/'Assemblies').mkdir()
 shutil.copy2(REPO/'About/About.xml',mod/'About/About.xml')
 shutil.copy2(REPO/'LoadFolders.xml',mod/'LoadFolders.xml')
 shutil.copy2(assembly,mod/'Assemblies/MihoHediffFix.dll')
assert (mod/'Assemblies/MihoHediffFix.dll').read_bytes()==assembly.read_bytes(), 'Use a fresh --tag for a changed DLL'
source_hash=hashlib.sha256((mod/'Assemblies/MihoHediffFix.dll').read_bytes()).hexdigest()
mods=['brrainz.harmony','ludeon.rimworld','ludeon.rimworld.royalty','ludeon.rimworld.ideology','ludeon.rimworld.biotech','ludeon.rimworld.anomaly','ludeon.rimworld.odyssey','erdelf.humanoidalienraces','miho.fortifiedoutremer']
if args.mode!='upstream':mods.append('zades.mihohedifffix')
mods.append('local.miho.runtimeprobe')
xml(profile/'Config/ModsConfig.xml','ModsConfigData',{'version':(game/'Version.txt').read_text().split()[0],'activeMods':mods,'knownExpansions':mods[2:7]})
xml(profile/'Config/Prefs.xml','PrefsData',{'screenWidth':1280,'screenHeight':800,'fullscreen':'False','runInBackground':'True','devMode':'True','pauseOnError':'False','pauseOnLoad':'True','openLogOnWarnings':'False','volumeGame':0,'volumeMusic':0,'volumeUI':0,'volumeAmbient':0})
managed=game/('RimWorldMac.app/Contents/Resources/Data/Managed' if args.version=='1.6' else 'RimWorld.app/Contents/MacOS')
if not managed.exists():
 managed=next(game.glob('*.app/Contents/**/Assembly-CSharp.dll')).parent
subprocess.run(['dotnet','build',str(HARNESS/'Source/Probe.csproj'),'-c','Release',
 '-p:ProbeTargetFramework='+('netstandard2.1' if args.version=='1.6' else 'net10.0'),
 '-p:GameManaged='+str(managed),
 '-p:HarmonyAssembly='+str(WORKSHOP/'2009463077'/args.version/'Assemblies/0Harmony.dll'),
 '-p:MihoAssembly='+str(WORKSHOP/'2816826107/1.6/Assemblies/CheckHediffMakeHediff.dll'),
 '-p:OutputPath='+str(probe/'Assemblies')+'/'],check=True)
links=[];held=[]
sources=[mod,probe,WORKSHOP/'2009463077',WORKSHOP/'839005762',WORKSHOP/'2816826107']
try:
 # Avoid another installed copy of the mod taking priority over the test copy.
 for path in (game/'Mods').iterdir():
  try:package=ET.parse(path/'About/About.xml').getroot().findtext('packageId','').lower()
  except Exception:continue
  if package=='zades.mihohedifffix':
   if not path.is_symlink():raise RuntimeError('Duplicate non-link mod: '+str(path))
   dest=result/(path.name+'.disabled');path.rename(dest);held.append((path,dest))
 for i,source in enumerate(sources):
  assert source.exists(),source
  if any(q.resolve()==source.resolve() for q in (game/'Mods').iterdir()):continue
  link=game/'Mods'/('Miho-runtime-test-'+str(i));assert not link.exists() and not link.is_symlink();link.symlink_to(source,target_is_directory=True);links.append(link)
 (profile/'Saves').mkdir(exist_ok=True)
 baseline=args.baseline
 tree=ET.parse(baseline)
 components=tree.getroot().find('game/components')
 for e in list(components):
  if e.attrib.get('Class')=='MihoRuntimeValidation.ProbeGame':components.remove(e)
 for key in ['modIds','modNames','modSteamIds']:
  node=tree.getroot().find('meta/'+key)
  node.clear()
  for value in (mods if key!='modSteamIds' else ['0']*len(mods)):ET.SubElement(node,'li').text=value
 tree.write(profile/'Saves/autostart.rws',encoding='utf-8',xml_declaration=True)
 cmd=['python3',str(Path.home()/'codex_workspace/rimworld-standalone/launch.py'),'--game-version',args.version,f'-savedatafolder={profile}','-logFile',str(result/(phase+'-game.log'))]

 (result/(phase+'-manifest.json')).write_text(json.dumps({'command':cmd,'expectedDllHash':source_hash,'modCopy':str(mod),'activeMods':mods},indent=2)+'\n')
 env=dict(os.environ,MIHO_TEST_REPORT=str(report),MIHO_TEST_PHASE=phase,MIHO_EXPECTED_HASH=source_hash,MIHO_MODE=args.mode,MIHO_WARMUP_MS=str(args.warmup_ms),MIHO_REPEAT="1" if args.repeat else "0")
 with (result/(phase+'-launcher.log')).open('w') as log:
  process=subprocess.Popen(cmd,env=env,stdout=log,stderr=subprocess.STDOUT)
  deadline=time.monotonic()+1200;seen=0
  try:
   while time.monotonic()<deadline:
    rows=[json.loads(l) for l in report.read_text().splitlines()] if report.exists() else []
    for row in rows[seen:]:print(json.dumps(row),flush=True)
    seen=len(rows)
    if any(r['type'] in ['complete','fatal'] for r in rows):break
    if process.poll() is not None:break
    time.sleep(1)
  finally:
   if process.poll() is None:
    process.terminate()
    try:process.wait(timeout=10)
    except subprocess.TimeoutExpired:process.kill();process.wait()
 rows=[json.loads(l) for l in report.read_text().splitlines()] if report.exists() else []
 logs=(result/(phase+'-game.log')).read_text(errors='replace') if (result/(phase+'-game.log')).exists() else ''
 logs+='\n'+(result/(phase+'-launcher.log')).read_text(errors='replace')
 summary={'tag':tag,'phase':phase,'passes':sum(r['type']=='pass' for r in rows),'failures':[r for r in rows if r['type'] in ['fail','fatal','worker_failure']],'errors':[r for r in rows if r['type']=='game_error'],'complete':any(r['type']=='complete' for r in rows),'steamDisabled':'SteamAPI.Init() failed' in logs or 'SteamAPI initialization failed' in logs,'report':str(report)}
 (result/(phase+'-summary.json')).write_text(json.dumps(summary,indent=2)+'\n');print(json.dumps(summary),flush=True)
 if not summary['complete'] or summary['failures'] or not summary['steamDisabled']:raise RuntimeError('Runtime validation did not pass; see '+str(result/(phase+'-summary.json')))
finally:
 for link in links:
  if link.is_symlink():link.unlink()
 for original,dest in held:
  if dest.is_symlink():dest.rename(original)
