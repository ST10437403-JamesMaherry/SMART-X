#!/usr/bin/env python3
'''Record actual telemetry dispatch order from a quiet local Smart-X API.'''
import argparse
import html
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path


class LocalApi:
    def __init__(self, base_url):
        parsed = urllib.parse.urlparse(base_url)
        if parsed.scheme not in ('http', 'https') or parsed.hostname not in ('localhost', '127.0.0.1', '::1'):
            raise ValueError('Use a local Smart-X URL, such as http://localhost:8080.')
        self.base_url = base_url.rstrip('/')
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(self, path, method='GET', body=None, expected=200):
        data = None if body is None else json.dumps(body).encode('utf-8')
        request = urllib.request.Request(self.base_url + path, data=data, method=method,
                                         headers={'Content-Type': 'application/json'})
        try:
            with self.http.open(request, timeout=10) as response:
                if response.status != expected:
                    raise RuntimeError('Unexpected HTTP status for ' + path)
                return json.loads(response.read())
        except urllib.error.HTTPError as error:
            raise RuntimeError('{} {} returned HTTP {}: {}'.format(method, path, error.code,
                               error.read().decode('utf-8', errors='replace'))) from error

    def pause(self, paused):
        return self.request('/api/realtime/simulation/ordinary-dispatch', 'PUT', {'paused': paused})

    def state(self):
        state = self.request('/api/realtime/processing')
        if state['failure'] is not None or state['workerStatus'] in ('Faulted', 'Stopped'):
            raise RuntimeError('Worker is not available: ' + state['workerStatus'])
        require(state['acceptedCount'] == state['pendingCount'] + state['processedCount'] +
                state['outstandingDispatchedCount'], 'Accepted-work accounting did not balance.')
        return state

    def wait(self, condition):
        deadline = time.monotonic() + 30
        while True:
            state = self.state()
            if condition(state):
                return state
            if time.monotonic() >= deadline:
                raise RuntimeError('Timed out waiting for the expected processing state.')
            time.sleep(0.05)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def demo_records(state, sensor_id):
    # Preserve the server's actual dequeue order. Never sort by priority here.
    return [record for record in state['recentDispatches']
            if record['telemetry']['sensorProfileId'] == sensor_id]


def capture(api):
    baseline = api.state()
    require(baseline['pendingCount'] == 0 and baseline['outstandingDispatchedCount'] == 0,
            'Finish existing queued work before recording this demonstration.')
    original_pause = baseline['ordinaryDispatchPaused']
    try:
        api.pause(True)
        sensor = api.request('/api/sensors', 'POST', {
            'name': 'Critical bypass evidence',
            'deviceIdentifier': 'priority-evidence-' + uuid.uuid4().hex,
            'identifierKind': 'UniqueIdentifier',
            'deploymentLocation': {'facility': 'Evidence Demo', 'zone': 'Priority', 'nodeId': 'ABC'},
            'category': 'Environmental', 'telemetryDataType': 'Integer'}, expected=201)
        sensor_id = sensor['id']
        captured = datetime.now(timezone.utc) - timedelta(seconds=3)
        readings = []
        for index, value in enumerate((10, 11)):
            readings.append(api.request('/api/sensors/' + sensor_id + '/telemetry', 'POST', {
                'capturedAtUtc': (captured + timedelta(seconds=index)).isoformat(),
                'integerValue': value}, expected=201))
        pending = api.state()
        require(pending['ordinaryPendingCount'] == 2 and pending['criticalPendingCount'] == 0,
                'A/B are not both pending; run this on a quiet local instance.')
        require(pending['dispatchedCount'] == baseline['dispatchedCount'],
                'A/B dispatched before C arrived.')
        readings.append(api.request('/api/sensors/' + sensor_id + '/telemetry', 'POST', {
            'capturedAtUtc': (captured + timedelta(seconds=2)).isoformat(),
            'integerValue': 100}, expected=201))
        bypass = api.wait(lambda state: len(demo_records(state, sensor_id)) == 1 and
                          demo_records(state, sensor_id)[0]['outcome'] == 'Processed')
        first = demo_records(bypass, sensor_id)[0]
        require(first['lane'] == 'Critical' and first['telemetry']['integerValue'] == 100,
                'Critical C did not dispatch first.')
        require(bypass['ordinaryDispatchPaused'] and bypass['ordinaryPendingCount'] == 2,
                'A/B must remain pending when critical C completes.')
        api.pause(False)
        completed = api.wait(lambda state: len(demo_records(state, sensor_id)) == 3 and
                             all(record['outcome'] == 'Processed' for record in demo_records(state, sensor_id)))
        records = demo_records(completed, sensor_id)
        arrival = baseline['acceptedCount']
        require([record['enqueueSequence'] for record in records] == [arrival + 3, arrival + 1, arrival + 2],
                'Expected actual dispatch order C, A, B.')
        require([record['dequeueSequence'] for record in records] == list(range(baseline['dispatchedCount'] + 1,
                                                                            baseline['dispatchedCount'] + 4)),
                'Other work dispatched during the capture; use a quiet instance.')
        require(completed['acceptedCount'] == baseline['acceptedCount'] + 3 and
                completed['processedCount'] == baseline['processedCount'] + 3 and
                completed['pendingCount'] == 0 and completed['outstandingDispatchedCount'] == 0,
                'Expected all three demo packets to be accounted for.')
        snapshot = api.request('/api/realtime/snapshot')
        device = next(device for device in snapshot['devices'] if device['id'] == sensor_id)
        require(device['latestProcessedTelemetry'] == readings[2],
                'Older A/B processing replaced the newer observation C.')
        history = api.request('/api/sensors/' + sensor_id + '/telemetry?limit=100')
        require(history == readings, 'Part 1 recent-history order changed.')
        return {'recordedAtUtc': datetime.now(timezone.utc).isoformat(), 'apiBaseUrl': api.base_url,
                'sensorProfileId': sensor_id, 'baseline': baseline, 'ordinaryPendingBeforeC': pending,
                'criticalCompletedWhilePaused': bypass, 'completed': completed,
                'acceptedReadings': readings, 'actualDispatches': records,
                'deviceAfterProcessing': device, 'recentHistory': history}
    finally:
        # This global simulation setting must return to the caller's prior value,
        # including when an assertion or request fails.
        api.pause(original_pause)


def report(data):
    rows = []
    for record in data['actualDispatches']:
        label = {10: 'A', 11: 'B', 100: 'C'}[record['telemetry']['integerValue']]
        values = [str(record['dequeueSequence']), label, str(record['telemetry']['integerValue']),
                  str(record['enqueueSequence']), record['lane'], record['severity'], record['outcome'],
                  'Yes' if record['updatedLatestObservation'] else 'No']
        css = 'critical' if label == 'C' else ''
        rows.append('<tr class="' + css + '">' + ''.join('<td>' + html.escape(value) + '</td>' for value in values) + '</tr>')
    complete = data['completed']
    baseline = data['baseline']
    context = html.escape(data['apiBaseUrl'] + ' | ' + data['recordedAtUtc'])
    sensor = html.escape(data['sensorProfileId'])
    return '''<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Smart-X critical bypass evidence</title>
<style>
*{box-sizing:border-box}body{margin:0;background:#0c1422;color:#e8edf5;font:16px system-ui,sans-serif}
main{max-width:1260px;margin:40px auto;padding:32px;border:1px solid #2e3b50;border-radius:18px;background:#142033}
.kicker{color:#76d7cd;letter-spacing:.1em;font-size:13px}h1{font-size:32px;margin:10px 0}p{color:#bdcada;line-height:1.55}
.cards{display:grid;grid-template-columns:repeat(3,1fr);gap:16px;margin:24px 0}.card{background:#0c1727;padding:20px;border-radius:12px}
.card b{font-size:28px;display:block;margin-top:6px;color:#f5f8fc}.card span{color:#bdcada}
table{border-collapse:collapse;width:100%;font-size:15px}th,td{text-align:left;padding:17px 12px;border-bottom:1px solid #334158}
th{color:#a6b7cc;font-weight:600}tr.critical{background:#26364c}tr.critical td{color:#ffce7b}
.note{padding:18px 20px;background:#0c1727;border-left:3px solid #76d7cd;margin-top:24px}.meta{font-size:12px;overflow-wrap:anywhere}
</style></head><body><main><div class="kicker">SMART-X · P2-03 · RECORDED API DISPATCH EVIDENCE</div>
<h1>Critical C bypassed pending ordinary A and B</h1>
<p>A = 10 and B = 11 were accepted while ordinary dispatch was paused. C = 100 arrived later and completed through the critical lane.
Resuming ordinary dispatch then processed A followed by B.</p>
<div class="cards"><div class="card"><span>Actual dispatch order</span><b>C → A → B</b></div>
<div class="card"><span>Demo packets accepted / processed</span><b>3 / 3</b></div>
<div class="card"><span>Latest device observation after A/B</span><b>100 (C)</b></div></div>
<table><thead><tr><th>Dequeue #</th><th>Packet</th><th>Value</th><th>Enqueue #</th><th>Lane</th><th>Severity</th><th>Outcome</th><th>Updated latest?</th></tr></thead>
<tbody>''' + ''.join(rows) + '''</tbody></table>
<div class="note">Rows preserve the actual server dequeue order returned by <code>GET /api/realtime/processing</code>.
The adjacent JSON stores the before, paused and completed API snapshots. This is a report of a recorded API run.</div>
<p class="meta">''' + context + '<br>Sensor: ' + sensor + '<br>Lifetime accepted / processed: ' + str(complete['acceptedCount']) + ' / ' + str(complete['processedCount']) + ' (before demo: ' + str(baseline['acceptedCount']) + ' / ' + str(baseline['processedCount']) + ''').<br>Final pending: 0 · Outstanding dispatched: 0 · Part 1 recent-history order: A, B, C.</p>
</main></body></html>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default='http://localhost:8080')
    parser.add_argument('--output-dir', default='docs/evidence/part2')
    args = parser.parse_args()
    data = capture(LocalApi(args.base_url))
    destination = Path(args.output_dir)
    destination.mkdir(parents=True, exist_ok=True)
    stem = destination / '03-priority-alert-bypass'
    stem.with_suffix('.json').write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    stem.with_suffix('.html').write_text(report(data), encoding='utf-8')
    print('Verified actual dispatch C -> A -> B; all 3 packets processed; latest observation remains C.')
    print('Saved {} and {}'.format(stem.with_suffix('.json'), stem.with_suffix('.html')))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, RuntimeError, KeyError, StopIteration) as error:
        print('Evidence capture failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
