import json,os,pathlib,subprocess

EXPECTED_SQL_IMAGE='mcr.microsoft.com/mssql/server:2025-latest@sha256:4bab24f36c1ecd48e85f7d37df26e6bf301641d84c3fe652f9a0dcc947d512e1'

def pack(private,out,suite,sha,code):
 out.mkdir(mode=0o700,exist_ok=True)
 paths=sorted(private.glob('sql-exit-*.json'));assert len(paths)<=128
 summary={'sha':sha,'suite':suite,'image':EXPECTED_SQL_IMAGE,'qualificationExit':code,'captureMode':'existing-log-read-after-exited-container','captures':[]}
 for sequence,path in enumerate(paths,1):
  raw=path.read_bytes();assert len(raw)<=1048576
  try:
   captured=json.loads(raw);truncated=captured['Truncated'];incomplete=False
  except (ValueError,KeyError):
   truncated=False;incomplete=True
  subprocess.run(['openssl','cms','-encrypt','-binary','-aes256','-in',str(path),'-outform','DER','-out',str(out/f'sql-exit-{sequence}.json.p7m'),'eng/ci/startup-diagnostic-recipient.pem'],check=True,capture_output=True)
  summary['captures'].append({'sequence':sequence,'bytes':len(raw),'truncated':truncated,'incomplete':incomplete,'fatalMarker':b'fatal error' in raw.lower()})
  path.unlink()
 (out/'summary.json').write_text(json.dumps(summary,indent=2))
 return summary

def main():
 os.umask(0o077)
 suite=os.environ['CDC_DIAGNOSTIC_SUITE'];assert suite in ['Admission','Lifecycle','Recovery','RecordSize','Telemetry']
 assert os.environ['CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE']==EXPECTED_SQL_IMAGE
 private=pathlib.Path(os.environ['RUNNER_TEMP'])/'cdc-sql-failure-only';private.mkdir(mode=0o700)
 out=pathlib.Path('startup-diagnostic');environment=os.environ.copy();environment['CDC_SQL_EXIT_CAPTURE_DIRECTORY']=str(private)
 code=1
 try:
  code=subprocess.call(['pwsh','-NoProfile','-File','./eng/ci/Invoke-CdcQualification.ps1','-Lane','Mssql','-Suite',suite,'-ResultsDirectory','TestResults/cdc-qualification','-PullImages'],env=environment)
 finally:
  pack(private,out,suite,os.environ['GITHUB_SHA'],code)
 return code

if __name__=='__main__':
 raise SystemExit(main())
