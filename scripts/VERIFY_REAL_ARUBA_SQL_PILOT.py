#!/usr/bin/env python3
"""JCF real-lab acceptance client: real local Identity + SQL-backed app + Aruba GET probes.

No device configuration is changed. Authenticates to the deployed production-mode
application via HTTPS, registers an approved pilot location/device, invokes native
connectors inside the application and verifies the resulting SQL-persisted evidence.
No third-party Python libraries. Never embeds passwords or disables TLS validation.
"""
from __future__ import annotations
import argparse
import getpass
import http.cookiejar
from html.parser import HTMLParser
import json
from pathlib import Path
import sys
import time
from urllib import parse, request, error

class TokenParser(HTMLParser):
    def __init__(self):
        super().__init__(); self.token = None
    def handle_starttag(self, tag, attrs):
        if tag != 'input': return
        a = dict(attrs)
        if a.get('name') == '__RequestVerificationToken': self.token = a.get('value')

class Client:
    def __init__(self, base):
        parsed = parse.urlsplit(base)
        if parsed.scheme != 'https' or not parsed.netloc or parsed.username or parsed.password or parsed.query:
            raise ValueError('A trusted HTTPS application URL is mandatory; credentials must never travel over HTTP.')
        self.base = base.rstrip('/')
        self.origin = (parsed.scheme, parsed.netloc)
        self.cookies = http.cookiejar.CookieJar()
        client = self
        class RedirectGuard(request.HTTPRedirectHandler):
            def redirect_request(self, req, fp, code, msg, headers, newurl):
                destination = parse.urlsplit(parse.urljoin(req.full_url, newurl))
                if (destination.scheme, destination.netloc) != client.origin:
                    raise RuntimeError('Unexpected cross-origin redirect refused.')
                return super().redirect_request(req, fp, code, msg, headers, newurl)
        # Direct local connection only; do not forward JCF management cookies via a corporate proxy.
        self.opener = request.build_opener(request.ProxyHandler({}), request.HTTPCookieProcessor(self.cookies), RedirectGuard())
        self.csrf = None
    def call(self, path, method='GET', value=None, form=False):
        url = self.base + (path if path.startswith('/') else '/' + path)
        if not url.startswith(self.base + '/'):
            raise ValueError('API path is outside the configured application origin.')
        payload = None
        headers = {'Accept':'application/json'}
        if value is not None:
            if form:
                payload = parse.urlencode(value).encode('utf-8')
                headers['Content-Type'] = 'application/x-www-form-urlencoded'
            else:
                payload = json.dumps(value, separators=(',',':')).encode('utf-8')
                headers['Content-Type'] = 'application/json'
        if method != 'GET' and not form:
            if not self.csrf: raise RuntimeError('No CSRF token available for authenticated API write.')
            headers['X-CSRF-TOKEN'] = self.csrf
        req = request.Request(url, data=payload, headers=headers, method=method)
        try:
            with self.opener.open(req, timeout=70) as res:
                raw = res.read(2_000_000).decode('utf-8')
                mime = res.headers.get('Content-Type', '')
                return json.loads(raw) if 'json' in mime else raw
        except error.HTTPError as exc:
            # Response bodies can contain confidential infrastructure details. Do not print or persist them.
            raise RuntimeError(f'HTTP {exc.code} from {path}; inspect the application audit/server log.') from None
    def login(self, username, password):
        html = self.call('/account/login')
        parser = TokenParser(); parser.feed(html)
        if not parser.token: raise RuntimeError('Login page did not contain its antiforgery token.')
        self.call('/account/login', 'POST', {
            '__RequestVerificationToken': parser.token, 'username': username,
            'password': password, 'returnUrl': '/'}, form=True)
        auth = self.call('/api/v1/security/csrf')
        if not isinstance(auth, dict) or not auth.get('token'):
            raise RuntimeError('Local sign-in did not establish an authenticated API session.')
        self.csrf = auth['token']

def check(condition, detail):
    print(f"  {'PASS' if condition else 'FAIL'} | {detail}", flush=True)
    return bool(condition)

def main():
    ap = argparse.ArgumentParser(description='Real JCF application + SQL + Aruba read-only acceptance')
    ap.add_argument('--base-url', required=True, help='Trusted HTTPS production-mode pilot app URL')
    ap.add_argument('--username', required=True, help='JCF local PlatformAdmin username')
    ap.add_argument('--location', default='JCF API Pilot Lab', help='Existing or new lab Station location')
    ap.add_argument('--ap-ip', default='10.250.2.71', help='Approved Instant VC/standalone AP IP')
    ap.add_argument('--ap-model', choices=['Ap505','Ap535'], default='Ap505')
    ap.add_argument('--ap-secret-ref', default='PILOT_AP', help='Reference to server-side device credential secret')
    ap.add_argument('--switch-ip', help='Optional approved CX switch IP')
    ap.add_argument('--switch-model', choices=['Cx6100','Cx6200'], default='Cx6100')
    ap.add_argument('--switch-secret-ref', default='PILOT_CX')
    ap.add_argument('--verify-run-id', help='Independently check previously persisted run (after app restart)')
    ap.add_argument('--output-dir', default='pilot_evidence')
    args = ap.parse_args()
    report = {'tool':'JCF Live Aruba + SQL acceptance', 'version':'0.8',
              'startedUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
              'mode':'REAL_HARDWARE_READ_ONLY', 'checks':[], 'devices':[], 'configurationWritesAttempted':0}
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    client = Client(args.base_url)
    success = True
    def record(label, passed, evidence=None):
        nonlocal success
        success &= check(passed, label)
        entry = {'name':label,'passed':bool(passed)}
        if evidence is not None: entry['evidence'] = evidence
        report['checks'].append(entry)
    try:
        password = getpass.getpass('JCF platform password (not saved): ')
        client.login(args.username, password)
        password = ''
        print('Authenticated to production-mode app using local Identity.', flush=True)
        health = client.call('/healthz')
        record('Application reports production mode', isinstance(health,dict) and health.get('demo') is False)
        readiness = client.call('/api/v1/pilot/readiness')
        record('Database provider is SQL Server', readiness.get('sqlServerProvider') is True)
        record('SQL Server reachable', readiness.get('sqlReachable') is True)
        record('At least one committed migration applied', readiness.get('schemaManagedByMigrations') is True)
        record('Pilot hardware acceptance explicitly enabled', readiness.get('pilotEnabled') is True)
        record('Explicit pilot IP allowlist configured', readiness.get('approvedIpCount',0)>0)
        if not success:
            raise RuntimeError('Readiness gate failed. No AP was contacted or enrolled.')
        if args.verify_run_id:
            previous = client.call('/api/v1/pilot/acceptance-runs/'+args.verify_run_id)
            record('Previous hardware evidence survived application restart',
                   previous.get('id','').lower() == args.verify_run_id.lower() and
                   previous.get('sqlServerVerified') is True)
            report['priorRunId'] = args.verify_run_id
        places = client.call('/api/v1/locations')
        matches = [x for x in places if x.get('name') == args.location and x.get('type') == 'Station' and x.get('parentId') is None]
        if len(matches)>1: raise RuntimeError('Duplicate lab locations; select a unique lab location name.')
        location = matches[0] if matches else client.call('/api/v1/locations','POST',{'name':args.location,'type':'Station','parentId':None,'address':None})
        location_id = location['id']
        record('Lab physical location persisted', any(x['id']==location_id for x in client.call('/api/v1/locations')))
        config = [('AP',args.ap_model,args.ap_ip,args.ap_secret_ref)]
        if args.switch_ip: config.append(('CX',args.switch_model,args.switch_ip,args.switch_secret_ref))
        for family,model,ip,ref in config:
            print(f'\nTesting {family} {model} at {ip}', flush=True)
            registered = client.call('/api/v1/devices')
            matches = [x for x in registered if x.get('managementIp') == ip]
            if len(matches)>1: raise RuntimeError('Duplicate management IP present in application inventory.')
            if matches:
                device = matches[0]
                if device.get('model') != model:
                    raise RuntimeError(f'Equipment at {ip} is registered with a different model.')
                if str(device.get('locationId','')).lower() != location_id.lower():
                    raise RuntimeError('Existing device has a different physical location; correct it through the approved device-placement workflow.')
            else:
                device = client.call('/api/v1/devices','POST',{
                    'name':f'Pilot-{model}-{ip}', 'kind':model,'managementIp':ip,
                    'secretRef':ref,'locationId':location_id})
            device_id = device['id']
            record(f'{family} inventory is persisted in the database',
                   any(x['id']==device_id for x in client.call('/api/v1/devices')))
            placement_rows=client.call('/api/v1/placements')
            active=[row for row in placement_rows if row.get('deviceId')==device_id and row.get('throughUtc') is None]
            if not active:
                # Legacy registrations could have LocationId but no effective-dated placement.
                # This only modifies the app inventory record, never the network device.
                client.call(f'/api/v1/devices/{device_id}/placement','POST',{'locationId':location_id})
                placement_rows=client.call('/api/v1/placements')
                active=[row for row in placement_rows if row.get('deviceId')==device_id and row.get('throughUtc') is None]
            record(f'{family} effective-dated physical placement persisted',
                   len(active)==1 and active[0].get('locationId')==location_id)
            if len(active)!=1 or active[0].get('locationId')!=location_id:
                raise RuntimeError('Device placement could not be verified in the SQL-backed application.')
            # This POST changes only application records. The connector uses device login + GET only.
            evidence = client.call(f'/api/v1/pilot/devices/{device_id}/read-acceptance','POST',{})
            evidence_id = evidence['id']
            persisted = client.call('/api/v1/pilot/acceptance-runs/'+evidence_id)
            snapshot = client.call(f'/api/v1/pilot/devices/{device_id}/last-sample')
            verified = (evidence.get('verdict')=='ReadPathVerified' and
                        persisted.get('id')==evidence_id and
                        persisted.get('freshSnapshotPersisted') is True and
                        snapshot.get('deviceId')==device_id and snapshot.get('apiConnected') is True)
            record(f'{family} real API read -> SQL telemetry -> independent SQL read', verified,
                   {'verdict':evidence.get('verdict'), 'runId':evidence_id,
                    'clusterMembers':evidence.get('storedClusterMembers'),
                    'portSamples':evidence.get('storedPortSamples'),
                    'firmware':evidence.get('firmware'),
                    'telemetryAtUtc':snapshot.get('atUtc')})
            if family=='AP':
                members=client.call(f'/api/v1/devices/{device_id}/members')
                record('Instant cluster member inventory persisted',
                       isinstance(members,list) and len(members)>0 and
                       len(members)==evidence.get('storedClusterMembers'))
            else:
                ports=client.call(f'/api/v1/devices/{device_id}/ports')
                record('CX interface inventory persisted',isinstance(ports,list) and len(ports)>0)
            report['devices'].append({'kind':model,'managementIp':ip,'deviceId':device_id,
                                      'runId':evidence_id,'verdict':evidence.get('verdict')})
        # Analysis only reads persisted data and creates human-review recommendations.
        analysis = client.call('/api/v1/analysis-runs', 'POST', {})
        record('Analyze Now completed with SQL-backed analysis run', bool(analysis.get('id')))
        runs = client.call('/api/v1/analysis-runs')
        record('Analysis run re-read from SQL',any(x.get('id')==analysis.get('id') for x in runs))
        report['analysisRunId'] = analysis.get('id')
    except Exception as ex:
        success = False
        # Never serialize the exception type's full request, user password, cookies or response body.
        report['failure'] = str(ex)[:240]
        print('FAIL | ' + str(ex)[:240], file=sys.stderr, flush=True)
    report['finishedUtc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
    report['result'] = 'READ_PATH_VERIFIED_ON_LIVE_HARDWARE' if success else 'NOT_VERIFIED'
    dest = output_dir / f"JCF_SQL_ARUBA_LIVE_PILOT_{time.strftime('%Y%m%d_%H%M%S',time.gmtime())}.json"
    dest.write_text(json.dumps(report,indent=2,sort_keys=True),encoding='utf-8')
    print(f'\nFINAL: {report["result"]}\nEvidence: {dest}',flush=True)
    if success: print('IMPORTANT: No device configuration writes were tested or certified.',flush=True)
    return 0 if success else 1

if __name__ == '__main__':
    raise SystemExit(main())
