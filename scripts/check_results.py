#!/usr/bin/env python3
import csv, gzip, json, math, sys
from collections import defaultdict
from pathlib import Path

root = Path(sys.argv[1] if len(sys.argv) > 1 else 'results/reference')
data = json.loads((root / 'report.json').read_text(encoding='utf-8-sig'))
r = data['report']; runs = r['parameters']['runs']; project = r['project']
samples = root / 'samples.csv'
if samples.exists():
    rows = list(csv.DictReader(samples.open(encoding='utf-8-sig')))
else:
    with gzip.open(root / 'samples.csv.gz', 'rt', encoding='utf-8-sig') as stream:
        rows = list(csv.DictReader(stream))

def check_stats(values, summary):
    values.sort(); n = len(values)
    assert summary['samples'] == n
    if not n:
        assert all(summary[x] is None for x in ['meanMs','medianMs','p95Ms','maxMs']); return
    expected = {'meanMs':sum(values)/n, 'medianMs':(values[n//2-1]+values[n//2])/2 if n%2==0 else values[n//2],
                'p95Ms':values[math.ceil(n*.95)-1], 'maxMs':values[-1]}
    for field, value in expected.items():
        assert math.isclose(summary[field], value, rel_tol=1e-10, abs_tol=1e-10), (field, summary[field], value)

if project == 'DigitalTwinFreshness':
    assert len(r['functional']) == 60 and len(rows) == 9*runs
    for row in r['functional']:
        assert row['runs'] == runs
        if row['mode'] == 'ActiveFull': assert row['falseConfirmations'] == 0
    groups = defaultdict(list)
    for row in rows: groups[(float(row['interval_ms']),float(row['exchange_loss_probability']))].append(row)
    for summary in r['latency']:
        group = groups[(summary['intervalMs'],summary['exchangeLossProbability'])]
        assert len(group) == runs and len({x['trial'] for x in group}) == runs
        values = [float(x['detection_delay_ms']) for x in group if x['detection_delay_ms']]
        check_stats(values, summary['detectionDelay'])
        assert summary['communicationLoss'] == sum(x['final_state']=='CommunicationLoss' for x in group)
        assert len(values)+summary['communicationLoss'] == runs
        if summary['exchangeLossProbability']==0:
            assert all(0 <= v <= summary['intervalMs']+r['parameters']['maxRttMs'] for v in values)
else:
    assert project == 'DigitalTwinRecovery' and len(r['functional']) == 36
    assert len(rows) == 8*r['parameters']['benchmarkRepeats']
    for row in r['functional']:
        assert row['runs'] == runs and row['accepted']+row['rejected']==runs
        assert row['statePreservedOnRejection']==row['rejected']
        if row['mode']=='Full':
            assert row['belowKnownFloor']==row['wrongSessionAccepted']==0
            assert row['correctAfterAccepted']==row['accepted']
    groups = defaultdict(list)
    for row in rows: groups[(row['store'],int(row['state_bytes']))].append(row)
    for summary in r['benchmark']:
        group = groups[(summary['store'],summary['stateBytes'])]
        check_stats([float(x['snapshot_creation_ms']) for x in group],summary['snapshotCreation'])
        check_stats([float(x['verification_and_commit_ms']) for x in group],summary['verificationAndCommit'])
print(project + ': PASS exported totals, protocol result invariants and recomputed timing summaries')
