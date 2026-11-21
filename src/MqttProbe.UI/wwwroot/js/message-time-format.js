const formatters = new Map();

const bridgeName = "mqttProbeTime";
const bridgeVersion = 1;

function pick(parts, type) {
    return parts.find(part => part.type === type)?.value ?? "";
}

function deviceTimeZone() {
    return new Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
}

function resolveOptions(options) {
    const opts = options || {};
    return {
        zone: opts.zone === "utc" ? "utc" : "local",
        clockFormat: opts.clockFormat === "12h" ? "12h" : "24h",
        timeZone: opts.timeZone
    };
}

function resolveZone(options) {
    const resolved = resolveOptions(options);
    return {
        ...resolved,
        timeZone: resolved.zone === "local" ? resolved.timeZone || deviceTimeZone() : "UTC"
    };
}

function clockFormatter(timeZone, clockFormat) {
    const key = `${timeZone}|${clockFormat}`;
    let formatter = formatters.get(key);
    if (formatter)
        return formatter;

    formatter = new Intl.DateTimeFormat("en-US", {
        timeZone,
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        fractionalSecondDigits: 3,
        hourCycle: clockFormat === "12h" ? "h12" : "h23",
        timeZoneName: "short"
    });
    formatters.set(key, formatter);
    return formatter;
}

function zoneNameFormatter(timeZone) {
    const key = `name|${timeZone}`;
    let formatter = formatters.get(key);
    if (formatter)
        return formatter;

    formatter = new Intl.DateTimeFormat("en-US", { timeZone, timeZoneName: "long" });
    formatters.set(key, formatter);
    return formatter;
}

function offsetFormatter(timeZone) {
    const key = `offset|${timeZone}`;
    let formatter = formatters.get(key);
    if (formatter)
        return formatter;

    formatter = new Intl.DateTimeFormat("en-US", { timeZone, timeZoneName: "longOffset" });
    formatters.set(key, formatter);
    return formatter;
}

function zoneName(timeZone, epochMs) {
    return pick(zoneNameFormatter(timeZone).formatToParts(new Date(epochMs)), "timeZoneName") || "";
}

function offsetLabel(timeZone, epochMs) {
    const raw = pick(offsetFormatter(timeZone).formatToParts(new Date(epochMs)), "timeZoneName");
    if (!raw || raw === "GMT" || raw === "UTC")
        return "UTC+00:00";

    const match = /^(?:GMT|UTC)([+-])(\d{1,2})(?::?(\d{2}))?$/.exec(raw);
    if (!match)
        return raw;

    return `UTC${match[1]}${match[2].padStart(2, "0")}:${match[3] ?? "00"}`;
}

function formatInstant(epochMs, zone) {
    const date = new Date(epochMs);
    const parts = clockFormatter(zone.timeZone, zone.clockFormat).formatToParts(date);
    const fraction = pick(parts, "fractionalSecond") || String(date.getMilliseconds()).padStart(3, "0");
    const hour = pick(parts, "hour").padStart(2, "0");
    const minute = pick(parts, "minute");
    const second = pick(parts, "second");
    const dayPeriod = pick(parts, "dayPeriod");
    const dateText = `${pick(parts, "year")}-${pick(parts, "month")}-${pick(parts, "day")}`;
    const timeText = `${hour}:${minute}:${second}.${fraction}${dayPeriod ? ` ${dayPeriod}` : ""}`;
    const zoneAbbreviation = pick(parts, "timeZoneName");

    return {
        dateText,
        timeText,
        zoneAbbreviation,
        fullText: `${dateText} ${timeText} ${zoneAbbreviation}`.trim()
    };
}

function formatZoneLabel(zone) {
    return zone.zone === "local" ? zone.timeZone : "UTC";
}

export function formatInstants(items, options) {
    const zone = resolveZone(options);
    const list = items || [];
    const probe = list.length > 0 ? list[0].epochMs : Date.now();

    return {
        zoneLabel: formatZoneLabel(zone),
        zoneName: zoneName(zone.timeZone, probe),
        items: list.map(item => {
            const instant = formatInstant(item.epochMs, zone);
            const result = {
                key: item.key,
                dateText: instant.dateText,
                timeText: instant.timeText,
                fullText: instant.fullText,
                zoneAbbreviation: instant.zoneAbbreviation,
                zoneName: "",
                offsetLabel: ""
            };

            if (item.detail) {
                result.zoneName = zoneName(zone.timeZone, item.epochMs);
                result.offsetLabel = offsetLabel(zone.timeZone, item.epochMs);
            }

            return result;
        })
    };
}

function bridgeApi() {
    return Object.freeze({
        version: bridgeVersion,
        formatAxis(epochMs, options) {
            return formatInstant(epochMs, resolveZone(options)).timeText;
        },
        formatTooltip(epochMs, options) {
            return formatInstant(epochMs, resolveZone(options)).fullText;
        },
        zoneLabel(options) {
            return formatZoneLabel(resolveZone(options));
        },
        zoneName(epochMs, options) {
            return zoneName(resolveZone(options).timeZone, epochMs);
        },
        offsetLabel(epochMs, options) {
            return offsetLabel(resolveZone(options).timeZone, epochMs);
        }
    });
}

export function installTimeBridge(target = globalThis) {
    const existing = target[bridgeName];
    if (existing && existing.version === bridgeVersion)
        return existing;

    const bridge = bridgeApi();
    target[bridgeName] = bridge;
    return bridge;
}
