import json,os,pathlib,secrets,subprocess,time
out=pathlib.Path('startup-diagnostic');out.mkdir()
private=pathlib.Path('/tmp/cdc-startup-private');private.mkdir(mode=0o700)
image=os.environ['CDC_SQL_IMAGE']
expected='mcr.microsoft.com/mssql/server:2025-latest@sha256:4bab24f36c1ecd48e85f7d37df26e6bf301641d84c3fe652f9a0dcc947d512e1'
assert image==expected, 'Diagnostic must retain the qualification image pin'
password='Dms1!'+secrets.token_hex(20)
print('::add-mask::'+password,flush=True)
env=os.environ.copy();env['MSSQL_SA_PASSWORD']=password
network='cdc-startup-diagnostic-'+os.environ['GITHUB_RUN_ID']
name=''
summary={'image':image,'sha':os.environ['GITHUB_SHA'],'kernel':subprocess.check_output(['uname','-r'],text=True).strip(),'architecture':subprocess.check_output(['uname','-m'],text=True).strip(),'attempts':[]}
def call(args,**kwargs):
 try:return subprocess.run(args,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,timeout=kwargs.pop('timeout',30),**kwargs)
 except subprocess.TimeoutExpired as e:return subprocess.CompletedProcess(args,124,e.output or b'')
def encrypt(path):
 subprocess.run(['openssl','cms','-encrypt','-binary','-aes256','-in',str(path),'-outform','DER','-out',str(out/(path.name+'.p7m')),'eng/ci/startup-diagnostic-recipient.pem'],check=True,capture_output=True)
 path.unlink()
try:
 subprocess.run(['docker','pull',image],check=True,stdout=subprocess.DEVNULL)
 subprocess.run(['docker','network','create',network],check=True,stdout=subprocess.DEVNULL)
 for attempt in range(1,41):
  name=network+'-'+str(attempt)
  began=time.monotonic()
  launched=call(['docker','run','--detach','--name',name,'--network',network,'-p','127.0.0.1::1433','-e','ACCEPT_EULA=Y','-e','MSSQL_SA_PASSWORD','-e','MSSQL_AGENT_ENABLED=true',image],env=env)
  record={'attempt':attempt,'launchExit':launched.returncode,'ready':False,'state':{}}
  until=time.monotonic()+90
  ready_at=None
  while launched.returncode==0 and time.monotonic()<until:
   inspected=call(['docker','inspect','--format','{{json .State}}',name])
   if inspected.returncode:
    record['inspectFailed']=True;break
   state=json.loads(inspected.stdout)
   record['state']={k:state.get(k) for k in ['Status','ExitCode','OOMKilled']}
   if state.get('Status') in ['exited','dead']:break
   query=call(['docker','exec',name,'sh','-c','/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -Q "SET NOCOUNT ON; SELECT 1;"'],timeout=12)
   if query.returncode==0:
    if ready_at is None:ready_at=time.monotonic()
    if time.monotonic()-ready_at>=15:
     record['ready']=True;break
   time.sleep(1)
  record['seconds']=round(time.monotonic()-began,3)
  summary['attempts'].append(record)
  print(json.dumps(record),flush=True)
  if not record['ready']:
   logs=call(['docker','logs','--tail','800',name]);path=private/('attempt-'+str(attempt)+'.log');path.write_bytes(logs.stdout);path.chmod(0o600);encrypt(path)
   inspected=call(['docker','inspect','--format','{{json .State}}',name]);path=private/('attempt-'+str(attempt)+'-state.json');path.write_bytes(inspected.stdout);path.chmod(0o600);encrypt(path)
   break
  call(['docker','rm','--force','--volumes',name]);name=''
finally:
 if name:call(['docker','rm','--force','--volumes',name])
 call(['docker','network','rm',network])
 (out/'summary.json').write_text(json.dumps(summary,indent=2))
