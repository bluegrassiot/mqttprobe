const formatters = new Map();

function pick(parts, type) {
    return parts.find(part => part.type === type)?.value ?? "";
}

function deviceTimeZone() {
    return new Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
}

function requestZone(zone, explicitTimeZone) {
    if (zone !== "local")
        return "UTC";
    return explicitTimeZone || deviceTimeZone();
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

function formatInstant(value, zone, clockFormat) {
    const date = new Date(value);
    const parts = clockFormatter(zone, clockFormat).formatToParts(date);
    const fraction = pick(parts, "fractionalSecond") || String(date.getMilliseconds()).padStart(3, "0");
    const hour = pick(parts, "hour").padStart(2, "0");
    const minute = pick(parts, "minute");
    const second = pick(parts, "second");
    const dayPeriod = pick(parts, "dayPeriod");
    const dateText = `${pick(parts, "year")}-${pick(parts, "month")}-${pick(parts, "day")}`;
    const timeText = `${hour}:${minute}:${second}.${fraction}${dayPeriod ? ` ${dayPeriod}` : ""}`;

    return {
        dateText,
        timeText,
        zoneAbbreviation: pick(parts, "timeZoneName")
    };
}

export function formatMessages(items, options) {
    const opts = options || {};
    const zone = opts.zone === "local" ? "local" : "utc";
    const clockFormat = opts.clockFormat === "12h" ? "12h" : "24h";
    const timeZone = requestZone(zone, opts.timeZone);
    const probe = items && items.length > 0 ? items[0].epochMs : Date.now();

    return {
        zoneLabel: zone === "local" ? timeZone : "UTC",
        zoneName: zoneName(timeZone, probe),
        items: (items || []).map(item => {
            const instant = formatInstant(item.epochMs, timeZone, clockFormat);
            const result = {
                key: item.key,
                dateText: instant.dateText,
                timeText: instant.timeText,
                fullText: `${instant.dateText} ${instant.timeText} ${instant.zoneAbbreviation}`.trim(),
                zoneAbbreviation: instant.zoneAbbreviation,
                zoneName: "",
                offsetLabel: ""
            };

            if (item.detail) {
                result.zoneName = zoneName(timeZone, item.epochMs);
                result.offsetLabel = offsetLabel(timeZone, item.epochMs);
            }

            return result;
        })
    };
}
