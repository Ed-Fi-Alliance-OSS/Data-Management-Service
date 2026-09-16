import json,os,pathlib,signal,subprocess,threading,time
out=pathlib.Path('startup-diagnostic');out.mkdir(exist_ok=True)
private=pathlib.Path('/tmp/cdc-stack-private');private.mkdir(mode=0o700,exist_ok=True)
image=os.environ['CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE']
expected='mcr.microsoft.com/mssql/server:2025-latest@sha256:4bab24f36c1ecd48e85f7d37df26e6bf301641d84c3fe652f9a0dcc947d512e1'
assert image==expected
suite=os.environ['CDC_DIAGNOSTIC_SUITE']
assert suite in ['Admission','Lifecycle','Recovery','RecordSize','Telemetry']
records={};workers=[];lock=threading.Lock();stopping=threading.Event()
def record(cid):
 try:
  inspected=subprocess.run(['docker','inspect','--format','{{.Config.Image}}',cid],capture_output=True,text=True,timeout=5)
  if inspected.returncode or inspected.stdout.strip()!=image:return
  with lock:
   if cid in records:return
   data={'bytes':bytearray(),'process':None,'truncated':False};records[cid]=data
  process=subprocess.Popen(['docker','logs','--follow','--tail','800',cid],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
  data['process']=process
  while chunk:=process.stdout.read(4096):
   data['bytes'].extend(chunk)
   if len(data['bytes'])>131072:
    del data['bytes'][:-131072];data['truncated']=True
  process.wait()
 except Exception:
  pass

def events():
 for line in event_process.stdout:
  if stopping.is_set():break
  cid=line.strip()
  if len(cid)==64 and all(c in '0123456789abcdef' for c in cid):
   worker=threading.Thread(target=record,args=(cid,),daemon=True);workers.append(worker);worker.start()

event_process=subprocess.Popen(['docker','events','--filter','type=container','--filter','event=start','--format','{{.ID}}'],stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True)
listener=threading.Thread(target=events,daemon=True);listener.start()
code=1
try:
 code=subprocess.call(['pwsh','-NoProfile','-File','./eng/ci/Invoke-CdcQualification.ps1','-Lane','Mssql','-Suite',suite,'-ResultsDirectory','TestResults/cdc-qualification','-PullImages'])
finally:
 stopping.set();event_process.terminate();event_process.wait(timeout=5);listener.join(timeout=5)
 for data in list(records.values()):
  process=data['process']
  if process is not None and process.poll() is None:process.terminate()
 for worker in workers:worker.join(timeout=5)
 summary={'sha':os.environ['GITHUB_SHA'],'image':image,'qualificationExit':code,'suite':suite,'containers':[]}
 for i,(cid,data) in enumerate(records.items(),1):
  raw=bytes(data['bytes']);name='sql-container-'+str(i)+'.log';path=private/name;path.write_bytes(raw);path.chmod(0o600)
  subprocess.run(['openssl','cms','-encrypt','-binary','-aes256','-in',str(path),'-outform','DER','-out',str(out/(name+'.p7m')),'eng/ci/startup-diagnostic-recipient.pem'],check=True,capture_output=True)
  path.unlink()
  summary['containers'].append({'sequence':i,'bytes':len(raw),'truncated':data['truncated'],'fatalMarker':b'fatal error' in raw.lower()})
 (out/'summary.json').write_text(json.dumps(summary,indent=2))
raise SystemExit(code)
