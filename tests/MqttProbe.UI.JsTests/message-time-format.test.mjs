import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const modulePath = new URL('../../src/MqttProbe.UI/wwwroot/js/message-time-format.js', import.meta.url);
const source = await readFile(modulePath, 'utf8');
const timeFormat = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const { formatInstants, installTimeBridge } = timeFormat;

const newYork = 'America/New_York';

function utc(iso, clockFormat = '24h') {
    return formatInstants(
        [{ key: 'one', epochMs: Date.parse(iso), detail: true }],
        { zone: 'utc', clockFormat }).items[0];
}

function localResultFor(iso, clockFormat = '24h', timeZone = newYork) {
    return formatInstants(
        [{ key: 'one', epochMs: Date.parse(iso), detail: true }],
        { zone: 'local', clockFormat, timeZone });
}

function local(iso, clockFormat = '24h', timeZone = newYork) {
    return localResultFor(iso, clockFormat, timeZone).items[0];
}

function withDeviceZone(timeZone, run) {
    const nativeDateTimeFormat = Intl.DateTimeFormat;
    Intl.DateTimeFormat = function (locale, options) {
        const withZone = options && options.timeZone === undefined
            ? { ...options, timeZone }
            : options;
        const formatter = new nativeDateTimeFormat(locale, withZone);
        const resolvedOptions = formatter.resolvedOptions.bind(formatter);
        formatter.resolvedOptions = () => ({ ...resolvedOptions(), timeZone });
        return formatter;
    };
    Intl.DateTimeFormat.prototype = nativeDateTimeFormat.prototype;
    try {
        return run();
    } finally {
        Intl.DateTimeFormat = nativeDateTimeFormat;
    }
}

test('UTC and 24-hour clock preserve the stored instant and milliseconds', () => {
    assert.equal(utc('2026-10-29T22:41:07.123Z').timeText, '22:41:07.123');
    assert.equal(utc('2026-10-29T22:41:07.123Z').dateText, '2026-10-29');
    assert.equal(utc('2026-10-29T00:00:00.000Z').timeText, '00:00:00.000', 'midnight is 00, not 24');
    assert.equal(utc('2026-10-29T09:07:09.070Z').timeText, '09:07:09.070', 'trailing millisecond zeros survive');
});

test('12-hour clock always carries AM or PM', () => {
    assert.equal(utc('2026-10-29T13:05:09.007Z', '12h').timeText, '01:05:09.007 PM');
    assert.equal(utc('2026-10-29T00:00:00.000Z', '12h').timeText, '12:00:00.000 AM');
    assert.equal(utc('2026-10-29T12:00:00.000Z', '12h').timeText, '12:00:00.000 PM');
    assert.equal(utc('2026-10-29T09:07:09.070Z', '12h').timeText, '09:07:09.070 AM');
});

test('date rollover moves the date and keeps both instants ordered', () => {
    const before = utc('2026-12-31T23:59:59.999Z');
    const after = utc('2027-01-01T00:00:00.000Z');

    assert.equal(before.dateText, '2026-12-31');
    assert.equal(after.dateText, '2027-01-01');
    assert.equal(after.timeText, '00:00:00.000');
});

test('spring daylight saving transition uses each instant offset and name', () => {
    const before = local('2026-03-08T06:59:59.999Z');
    const after = local('2026-03-08T07:00:00.000Z');

    assert.equal(before.timeText, '01:59:59.999');
    assert.equal(after.timeText, '03:00:00.000', 'the skipped 02:00 hour never appears');
    assert.equal(before.offsetLabel, 'UTC-05:00');
    assert.equal(after.offsetLabel, 'UTC-04:00');
    assert.equal(before.zoneAbbreviation, 'EST');
    assert.equal(after.zoneAbbreviation, 'EDT');
});

test('fall daylight saving transition repeats the hour with the right offset', () => {
    const before = local('2026-11-01T05:59:59.999Z');
    const after = local('2026-11-01T06:00:00.000Z');

    assert.equal(before.timeText, '01:59:59.999');
    assert.equal(after.timeText, '01:00:00.000');
    assert.equal(before.offsetLabel, 'UTC-04:00');
    assert.equal(after.offsetLabel, 'UTC-05:00');
    assert.equal(before.zoneName, 'Eastern Daylight Time');
    assert.equal(after.zoneName, 'Eastern Standard Time');
});

test('local display names the viewing zone and keeps 12-hour milliseconds', () => {
    const result = localResultFor('2026-10-29T22:41:07.123Z', '12h');

    assert.equal(result.zoneLabel, newYork);
    assert.equal(result.zoneName, 'Eastern Daylight Time');
    assert.equal(result.items[0].timeText, '06:41:07.123 PM');
    assert.equal(result.items[0].fullText, '2026-10-29 06:41:07.123 PM EDT');
});

test('batch items keep request order and only details resolve zone metadata', () => {
    const result = formatInstants(
        [
            { key: 'first', epochMs: Date.parse('2026-10-29T22:41:07.123Z'), detail: false },
            { key: 'second', epochMs: Date.parse('2026-10-29T22:41:08.123Z'), detail: true }
        ],
        { zone: 'utc', clockFormat: '24h' });

    assert.deepEqual(result.items.map(item => item.key), ['first', 'second']);
    assert.deepEqual(result.items.map(item => item.timeText), ['22:41:07.123', '22:41:08.123']);
    assert.equal(result.items[0].zoneName, '');
    assert.equal(result.items[0].offsetLabel, '');
    assert.equal(result.items[1].zoneName, 'Coordinated Universal Time');
    assert.equal(result.items[1].offsetLabel, 'UTC+00:00');
});

test('missing options fall back to the device zone and a 24-hour clock', () => {
    withDeviceZone(newYork, () => {
        const result = formatInstants([{ key: 'one', epochMs: Date.parse('2026-10-29T22:41:07.123Z') }]);

        assert.equal(result.zoneLabel, newYork);
        assert.equal(result.items[0].timeText, '18:41:07.123');
    });
});

test('local follows the viewing device zone without an override', () => {
    withDeviceZone(newYork, () => {
        const result = formatInstants(
            [{ key: 'one', epochMs: Date.parse('2026-10-29T22:41:07.123Z') }],
            { zone: 'local', clockFormat: '24h' });

        assert.equal(result.zoneLabel, newYork);
        assert.equal(result.items[0].timeText, '18:41:07.123');
    });
});

test('the global bridge installs once and formats with the same policy as batches', () => {
    const target = {};
    const first = installTimeBridge(target);
    const second = installTimeBridge(target);

    assert.equal(first, second, 'reinstalling returns the existing bridge');
    assert.equal(target.mqttProbeTime, first);
    assert.equal(typeof first.formatAxis, 'function');
    assert.equal(typeof first.formatTooltip, 'function');
    assert.equal(typeof first.zoneLabel, 'function');
    assert.equal(typeof first.zoneName, 'function');
    assert.equal(typeof first.offsetLabel, 'function');

    const epochMs = Date.parse('2026-10-29T22:41:07.123Z');
    const options = { zone: 'local', clockFormat: '12h', timeZone: newYork };
    const batchItem = formatInstants([{ key: 'one', epochMs, detail: true }], options).items[0];

    assert.equal(first.formatAxis(epochMs, options), batchItem.timeText);
    assert.equal(first.formatTooltip(epochMs, options), batchItem.fullText);
    assert.equal(first.zoneLabel(options), newYork);
    assert.equal(first.zoneName(epochMs, options), batchItem.zoneName);
    assert.equal(first.offsetLabel(epochMs, options), batchItem.offsetLabel);
});

test('bridge calls take explicit preferences and never leak them between calls', () => {
    const bridge = installTimeBridge({});
    const epochMs = Date.parse('2026-10-29T22:41:07.123Z');

    assert.equal(
        bridge.formatAxis(epochMs, { zone: 'utc', clockFormat: '24h' }),
        '22:41:07.123');
    assert.equal(
        bridge.formatTooltip(epochMs, { zone: 'utc', clockFormat: '24h' }),
        '2026-10-29 22:41:07.123 UTC');
    assert.equal(
        bridge.formatAxis(epochMs, { zone: 'local', clockFormat: '12h', timeZone: newYork }),
        '06:41:07.123 PM');
    assert.equal(
        bridge.formatAxis(epochMs, { zone: 'utc', clockFormat: '24h' }),
        '22:41:07.123',
        'a later call is unaffected by the previous local selection');
});

test('bridge zone metadata resolves per instant across a transition', () => {
    const bridge = installTimeBridge({});
    const options = { zone: 'local', clockFormat: '24h', timeZone: newYork };

    assert.equal(bridge.offsetLabel(Date.parse('2026-11-01T05:59:59.999Z'), options), 'UTC-04:00');
    assert.equal(bridge.offsetLabel(Date.parse('2026-11-01T06:00:00.000Z'), options), 'UTC-05:00');
    assert.equal(bridge.zoneName(Date.parse('2026-11-01T05:59:59.999Z'), options), 'Eastern Daylight Time');
    assert.equal(bridge.zoneName(Date.parse('2026-11-01T06:00:00.000Z'), options), 'Eastern Standard Time');
});
