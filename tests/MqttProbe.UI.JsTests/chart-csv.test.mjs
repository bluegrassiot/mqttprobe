import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/chart-csv.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
const { formatChartCsv } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const newYork = 'America/New_York';

function series(id, name, points) {
    return { id, name, color: '#F97316', points };
}

function snapshot(overrides = {}) {
    return {
        zone: 'utc',
        clockFormat: '24h',
        timeZone: 'UTC',
        series: [],
        ...overrides,
    };
}

function csvLines(csv) {
    assert.equal(csv.charCodeAt(0), 0xfeff, 'file starts with a UTF-8 BOM');
    assert.ok(csv.endsWith('\r\n'), 'file ends with a CRLF record');
    const records = csv.slice(1).split('\r\n');
    records.pop();
    return records;
}

test('wide CSV keeps configuration column order and ignores selection order', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('s1', 'Alpha', [[1000, 1]]),
                series('s2', 'Beta', [[1000, 2]]),
                series('s3', 'Gamma', [[1000, 3]]),
            ],
        }),
        ['s3', 's1', 's3']);

    assert.deepEqual(csvLines(csv), [
        'timestamp,utc_offset,Alpha,Gamma',
        '1970-01-01 00:00:01.000,+00:00,1,3',
    ]);
});

test('rows sort by instant and missing series values stay blank', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('a', 'A', [[2000, 1], [1000, 10]]),
                series('b', 'B', [[1500, 5]]),
            ],
        }),
        ['a', 'b']);

    assert.deepEqual(csvLines(csv), [
        'timestamp,utc_offset,A,B',
        '1970-01-01 00:00:01.000,+00:00,10,',
        '1970-01-01 00:00:01.500,+00:00,,5',
        '1970-01-01 00:00:02.000,+00:00,1,',
    ]);
});

test('duplicate instants align first occurrences, then second occurrences, keeping every point', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('a', 'A', [[1000, 1], [2000, 3], [1000, 2]]),
                series('b', 'B', [[3000, 30], [1000, 10]]),
            ],
        }),
        ['a', 'b']);

    assert.deepEqual(csvLines(csv), [
        'timestamp,utc_offset,A,B',
        '1970-01-01 00:00:01.000,+00:00,1,10',
        '1970-01-01 00:00:01.000,+00:00,2,',
        '1970-01-01 00:00:02.000,+00:00,3,',
        '1970-01-01 00:00:03.000,+00:00,,30',
    ]);
});

test('captured local clock settings produce the documented twelve-hour timestamp', () => {
    const epochMs = Date.parse('2026-11-15T00:39:59.652Z');
    const csv = formatChartCsv(
        snapshot({
            zone: 'local',
            clockFormat: '12h',
            timeZone: newYork,
            series: [series('sensor-1', 'Temperature', [[epochMs, 21.5]])],
        }),
        ['sensor-1']);

    assert.deepEqual(csvLines(csv), [
        'timestamp,utc_offset,Temperature',
        '2026-11-14 07:39:59.652 PM,-05:00,21.5',
    ]);
});

test('UTC clock settings keep the instant and a neutral offset in both clock formats', () => {
    const epochMs = Date.parse('2026-11-14T19:39:59.652Z');
    const source = snapshot({
        timeZone: 'Asia/Kolkata',
        series: [series('s1', 'Value', [[epochMs, 1]])],
    });

    const twentyFour = formatChartCsv(source, ['s1']);
    const twelve = formatChartCsv({ ...source, clockFormat: '12h' }, ['s1']);

    assert.equal(csvLines(twentyFour)[1], '2026-11-14 19:39:59.652,+00:00,1');
    assert.equal(csvLines(twelve)[1], '2026-11-14 07:39:59.652 PM,+00:00,1');
});

test('daylight saving transitions use the offset of each instant', () => {
    const spring = formatChartCsv(
        snapshot({
            zone: 'local',
            timeZone: newYork,
            series: [series('s1', 'Value', [
                [Date.parse('2026-03-08T07:00:00.000Z'), 2],
                [Date.parse('2026-03-08T06:59:59.999Z'), 1],
            ])],
        }),
        ['s1']);
    const fall = formatChartCsv(
        snapshot({
            zone: 'local',
            timeZone: newYork,
            series: [series('s1', 'Value', [
                [Date.parse('2026-11-01T06:00:00.000Z'), 4],
                [Date.parse('2026-11-01T05:59:59.999Z'), 3],
            ])],
        }),
        ['s1']);

    assert.deepEqual(csvLines(spring).slice(1), [
        '2026-03-08 01:59:59.999,-05:00,1',
        '2026-03-08 03:00:00.000,-04:00,2',
    ]);
    assert.deepEqual(csvLines(fall).slice(1), [
        '2026-11-01 01:59:59.999,-04:00,3',
        '2026-11-01 01:00:00.000,-05:00,4',
    ]);
});

test('a fall-back transition crossing midnight keeps each wall clock and offset', () => {
    const csv = formatChartCsv(
        snapshot({
            zone: 'local',
            timeZone: 'America/Santiago',
            series: [series('s1', 'Value', [
                [Date.parse('2026-04-05T04:00:00.000Z'), 3],
                [Date.parse('2026-04-05T03:00:00.000Z'), 2],
                [Date.parse('2026-04-05T02:59:59.999Z'), 1],
            ])],
        }),
        ['s1']);

    assert.deepEqual(csvLines(csv).slice(1), [
        '2026-04-04 23:59:59.999,-03:00,1',
        '2026-04-04 23:00:00.000,-04:00,2',
        '2026-04-05 00:00:00.000,-04:00,3',
    ]);
});

test('a captured half-hour zone offset is written as a separate value', () => {
    const csv = formatChartCsv(
        snapshot({
            zone: 'local',
            timeZone: 'Asia/Kolkata',
            series: [series('s1', 'Value', [[Date.parse('2026-11-14T02:09:59.652Z'), 7]])],
        }),
        ['s1']);

    assert.equal(csvLines(csv)[1], '2026-11-14 07:39:59.652,+05:30,7');
});

test('headers use display names, distinguish duplicates, and guard formulas', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('s1', '=SUM(A1:A2)', [[1000, 1]]),
                series('s2', '@cmd', [[1000, 2]]),
                series('s3', 'Dup', [[1000, 3]]),
                series('s4', 'Dup', [[1000, 4]]),
                series('s5', '+1', [[1000, 5]]),
                series('s6', '-1', [[1000, 6]]),
            ],
        }),
        ['s1', 's2', 's3', 's4', 's5', 's6']);

    assert.equal(
        csvLines(csv)[0],
        "timestamp,utc_offset,'=SUM(A1:A2),'@cmd,Dup,Dup (2),'+1,'-1");
});

test('formula guards apply before leading whitespace or a byte order mark', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('s1', ' =SUM(A1:A2)', [[1000, 1]]),
                series('s2', '\t=SUM(A1:A2)', [[1000, 2]]),
                series('s3', '\n=SUM(A1:A2)', [[1000, 3]]),
                series('s4', '\uFEFF=SUM(A1:A2)', [[1000, 4]]),
                series('s5', ' +1', [[1000, 5]]),
                series('s6', ' -1', [[1000, 6]]),
                series('s7', ' @cmd', [[1000, 7]]),
            ],
        }),
        ['s1', 's2', 's3', 's4', 's5', 's6', 's7']);

    const expectedHeader = [
        'timestamp',
        'utc_offset',
        "' =SUM(A1:A2)",
        "'\t=SUM(A1:A2)",
        "\"'\n=SUM(A1:A2)\"",
        "'\uFEFF=SUM(A1:A2)",
        "' +1",
        "' -1",
        "' @cmd",
    ].join(',') + '\r\n';

    assert.ok(csv.startsWith(`\uFEFF${expectedHeader}`), csv);
});

test('duplicate, numbered, reserved and empty display names get deterministic headers without ids', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('n1', 'Temperature', [[1000, 1]]),
                series('n2', 'Humidity', [[1000, 2]]),
                series('d1', 'Temp', [[1000, 3]]),
                series('d2', 'Temp', [[1000, 4]]),
                series('d3', 'Temp', [[1000, 5]]),
                series('e1', 'Temp (2)', [[1000, 6]]),
                series('r1', 'timestamp', [[1000, 7]]),
                series('r2', 'utc_offset', [[1000, 8]]),
                series('b1', '', [[1000, 9]]),
                series('b2', '', [[1000, 10]]),
                series('s1', 'Series', [[1000, 11]]),
                series('f1', ' =SUM(A1:A2)', [[1000, 12]]),
                series('f2', ' =SUM(A1:A2)', [[1000, 13]]),
            ],
        }),
        ['n1', 'n2', 'd1', 'd2', 'd3', 'e1', 'r1', 'r2', 'b1', 'b2', 's1', 'f1', 'f2']);

    assert.equal(
        csvLines(csv)[0],
        "timestamp,utc_offset,Temperature,Humidity,Temp,Temp (3),Temp (4),Temp (2),"
        + "timestamp (2),utc_offset (2),Series (2),Series (3),Series,"
        + "' =SUM(A1:A2),' =SUM(A1:A2) (2)");
});

test('formula-guarded and apostrophe-prefixed display names get distinct safe headers', () => {
    const csv = formatChartCsv(
        snapshot({
            series: [
                series('a1', '=SUM(A1)', [[1000, 1]]),
                series('a2', "'=SUM(A1)", [[1000, 2]]),
            ],
        }),
        ['a1', 'a2']);

    assert.equal(csvLines(csv)[0], "timestamp,utc_offset,'=SUM(A1),'=SUM(A1) (2)");
});

test('standard CSV escaping protects commas, quotes and line breaks in a header', () => {
    const csv = formatChartCsv(
        snapshot({ series: [series('s1', 'He said "hi", ok', [[1000, 1]])] }),
        ['s1']);

    assert.ok(csv.startsWith(
        '\uFEFFtimestamp,utc_offset,"He said ""hi"", ok"\r\n'),
        csv);
});

test('numeric values keep invariant round-trip precision', () => {
    const values = [0.1, 1 / 3, 0.30000000000000004, 1e-7, 1e21, -42.5, 0];
    const csv = formatChartCsv(
        snapshot({
            series: [series('s1', 'Value', values.map((value, index) => [1000 + index, value]))],
        }),
        ['s1']);

    assert.deepEqual(
        csvLines(csv).slice(1).map(line => line.split(',')[2]),
        values.map(value => String(value)));
});

test('a selected series without points still contributes its header', () => {
    const csv = formatChartCsv(
        snapshot({ series: [series('s1', 'Empty', []), series('s2', 'Hidden', [[1000, 1]])] }),
        ['s1']);

    assert.deepEqual(csvLines(csv), ['timestamp,utc_offset,Empty']);
});

test('point objects with epochMs and value are accepted alongside spec pairs', () => {
    const csv = formatChartCsv(
        snapshot({ series: [series('s1', 'Value', [{ epochMs: 1000, value: 4.5 }])] }),
        ['s1']);

    assert.equal(csvLines(csv)[1], '1970-01-01 00:00:01.000,+00:00,4.5');
});

test('invalid or non-finite input rejects instead of exporting partial data', () => {
    const valid = series('s1', 'Value', [[1000, 1]]);
    const reject = (snap, selected, pattern) => {
        assert.throws(() => formatChartCsv(snap, selected), pattern);
    };

    reject(null, ['s1'], /snapshot/i);
    reject(snapshot({ series: 'nope' }), ['s1'], /series/i);
    reject(snapshot({ series: [valid], zone: 'gmt' }), ['s1'], /zone/i);
    reject(snapshot({ series: [valid], clockFormat: '13h' }), ['s1'], /clock/i);
    reject(snapshot({ series: [valid], zone: 'local', timeZone: '' }), ['s1'], /time zone/i);
    reject(snapshot({ series: [valid] }), [], /at least one/i);
    reject(snapshot({ series: [valid] }), 's1', /selection/i);
    reject(snapshot({ series: [valid] }), ['missing'], /unknown selected series/i);
    reject(
        snapshot({ series: [series('s1', 'A', []), series('s1', 'B', [])] }),
        ['s1'],
        /duplicate series/i);
    reject(
        snapshot({ series: [series('s1', 'Value', [[Number.NaN, 1]])] }),
        ['s1'],
        /finite epoch/i);
    reject(
        snapshot({ series: [series('s1', 'Value', [[1000, Number.POSITIVE_INFINITY]])] }),
        ['s1'],
        /finite value/i);
    reject(
        snapshot({ series: [series('s1', 'Value', [[1000, '1']])] }),
        ['s1'],
        /finite value/i);
});
