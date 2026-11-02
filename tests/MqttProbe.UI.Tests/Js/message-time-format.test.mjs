import { readFileSync } from "node:fs";
import assert from "node:assert/strict";

const source = readFileSync(
    new URL("../../../src/MqttProbe.UI/wwwroot/js/message-time-format.js", import.meta.url),
    "utf8");
const { formatMessages } = await import(
    `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

const newYork = "America/New_York";

function utc(iso, clockFormat = "24h") {
    return formatMessages(
        [{ key: "one", epochMs: Date.parse(iso), detail: true }],
        { zone: "utc", clockFormat }).items[0];
}

function localResultFor(iso, clockFormat = "24h", timeZone = newYork) {
    return formatMessages(
        [{ key: "one", epochMs: Date.parse(iso), detail: true }],
        { zone: "local", clockFormat, timeZone });
}

function local(iso, clockFormat = "24h", timeZone = newYork) {
    return localResultFor(iso, clockFormat, timeZone).items[0];
}

assert.equal(utc("2026-10-29T22:41:07.123Z").timeText, "22:41:07.123", "UTC 24-hour time");
assert.equal(utc("2026-10-29T22:41:07.123Z").dateText, "2026-10-29", "UTC date");
assert.equal(utc("2026-10-29T00:00:00.000Z").timeText, "00:00:00.000", "UTC midnight is 00, not 24");
assert.equal(utc("2026-10-29T13:05:09.007Z", "12h").timeText, "01:05:09.007 PM", "12-hour afternoon");
assert.equal(utc("2026-10-29T00:00:00.000Z", "12h").timeText, "12:00:00.000 AM", "12-hour midnight");
assert.equal(utc("2026-10-29T12:00:00.000Z", "12h").timeText, "12:00:00.000 PM", "12-hour noon");
assert.equal(utc("2026-10-29T09:07:09.070Z", "12h").timeText, "09:07:09.070 AM", "milliseconds keep trailing zeros");

const rolloverStart = utc("2026-12-31T23:59:59.999Z");
const rolloverEnd = utc("2027-01-01T00:00:00.000Z");
assert.equal(rolloverStart.dateText, "2026-12-31", "date before rollover");
assert.equal(rolloverEnd.dateText, "2027-01-01", "date after rollover");
assert.equal(rolloverEnd.timeText, "00:00:00.000", "time after rollover");

const springBefore = local("2026-03-08T06:59:59.999Z");
const springAfter = local("2026-03-08T07:00:00.000Z");
assert.equal(springBefore.timeText, "01:59:59.999", "spring transition before local time");
assert.equal(springAfter.timeText, "03:00:00.000", "spring transition skips 02:00");
assert.equal(springBefore.offsetLabel, "UTC-05:00", "spring transition before offset");
assert.equal(springAfter.offsetLabel, "UTC-04:00", "spring transition after offset");
assert.equal(springBefore.zoneAbbreviation, "EST", "spring transition before abbreviation");
assert.equal(springAfter.zoneAbbreviation, "EDT", "spring transition after abbreviation");

const fallBefore = local("2026-11-01T05:59:59.999Z");
const fallAfter = local("2026-11-01T06:00:00.000Z");
assert.equal(fallBefore.timeText, "01:59:59.999", "fall transition before local time");
assert.equal(fallAfter.timeText, "01:00:00.000", "fall transition repeats the hour");
assert.equal(fallBefore.offsetLabel, "UTC-04:00", "fall transition before offset");
assert.equal(fallAfter.offsetLabel, "UTC-05:00", "fall transition after offset");
assert.equal(fallBefore.zoneName, "Eastern Daylight Time", "fall transition before zone name");
assert.equal(fallAfter.zoneName, "Eastern Standard Time", "fall transition after zone name");

const localDisplay = localResultFor("2026-10-29T22:41:07.123Z", "12h");
assert.equal(localDisplay.zoneLabel, newYork, "local label follows the viewing zone");
assert.equal(localDisplay.zoneName, "Eastern Daylight Time", "local zone name");
assert.equal(localDisplay.items[0].timeText, "06:41:07.123 PM", "local 12-hour time");

const deviceResult = formatMessages(
    [{ key: "one", epochMs: Date.parse("2026-10-29T22:41:07.123Z") }],
    { zone: "local", clockFormat: "24h" });
assert.equal(
    deviceResult.zoneLabel,
    new Intl.DateTimeFormat().resolvedOptions().timeZone,
    "without an override, local resolves to the viewing device zone");

const utcResult = formatMessages(
    [{ key: "one", epochMs: Date.parse("2026-10-29T22:41:07.123Z") }],
    { zone: "utc", clockFormat: "24h" });
assert.equal(utcResult.zoneLabel, "UTC", "UTC label");
assert.equal(utcResult.zoneName, "Coordinated Universal Time", "UTC zone name");
assert.equal(utcResult.items[0].fullText, "2026-10-29 22:41:07.123 UTC", "UTC full instant");

const nativeDateTimeFormat = Intl.DateTimeFormat;
let simulatedDeviceZone = "America/New_York";

Intl.DateTimeFormat = function (locale, options) {
    const withZone = options && options.timeZone === undefined
        ? { ...options, timeZone: simulatedDeviceZone }
        : options;
    const formatter = new nativeDateTimeFormat(locale, withZone);
    const resolvedOptions = formatter.resolvedOptions.bind(formatter);
    formatter.resolvedOptions = () => ({ ...resolvedOptions(), timeZone: simulatedDeviceZone });
    return formatter;
};
Intl.DateTimeFormat.prototype = nativeDateTimeFormat.prototype;

const newYorkDevice = formatMessages(
    [{ key: "one", epochMs: Date.parse("2026-10-29T22:41:07.123Z"), detail: true }],
    { zone: "local", clockFormat: "24h" });
assert.equal(newYorkDevice.zoneLabel, "America/New_York", "device zone label before change");
assert.equal(newYorkDevice.items[0].timeText, "18:41:07.123", "device zone value before change");
assert.equal(newYorkDevice.items[0].offsetLabel, "UTC-04:00", "device zone offset before change");

simulatedDeviceZone = "Europe/Berlin";
const berlinDevice = formatMessages(
    [{ key: "one", epochMs: Date.parse("2026-10-29T22:41:07.123Z"), detail: true }],
    { zone: "local", clockFormat: "24h" });
assert.equal(berlinDevice.zoneLabel, "Europe/Berlin", "device zone label after change");
assert.equal(berlinDevice.items[0].timeText, "23:41:07.123", "device zone value after change");
assert.equal(berlinDevice.items[0].offsetLabel, "UTC+01:00", "device zone offset after change");

Intl.DateTimeFormat = nativeDateTimeFormat;

console.log("message-time-format: all checks passed");
