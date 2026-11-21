const BOM = '\uFEFF';
const RECORD_SEPARATOR = '\r\n';
const FORMULA_LEADER = /^\s*[=+\-@]/;

const clockFormatters = new Map();
const offsetFormatters = new Map();

function pick(parts, type) {
    return parts.find(part => part.type === type)?.value ?? '';
}

function clockFormatter(timeZone, clockFormat) {
    const key = `${timeZone}|${clockFormat}`;
    let formatter = clockFormatters.get(key);
    if (!formatter) {
        formatter = new Intl.DateTimeFormat('en-US', {
            timeZone,
            year: 'numeric',
            month: '2-digit',
            day: '2-digit',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit',
            fractionalSecondDigits: 3,
            hourCycle: clockFormat === '12h' ? 'h12' : 'h23',
        });
        clockFormatters.set(key, formatter);
    }

    return formatter;
}

function offsetFormatter(timeZone) {
    const key = `offset|${timeZone}`;
    let formatter = offsetFormatters.get(key);
    if (!formatter) {
        formatter = new Intl.DateTimeFormat('en-US', { timeZone, timeZoneName: 'longOffset' });
        offsetFormatters.set(key, formatter);
    }

    return formatter;
}

function timestampText(epochMs, timeZone, clockFormat) {
    const date = new Date(epochMs);
    const parts = clockFormatter(timeZone, clockFormat).formatToParts(date);
    const fraction = pick(parts, 'fractionalSecond')
        || String(date.getUTCMilliseconds()).padStart(3, '0');
    const dayPeriod = pick(parts, 'dayPeriod');
    const hour = pick(parts, 'hour').padStart(2, '0');
    const time = `${hour}:${pick(parts, 'minute')}:${pick(parts, 'second')}.${fraction}`;
    const dateText = `${pick(parts, 'year')}-${pick(parts, 'month')}-${pick(parts, 'day')}`;

    return `${dateText} ${time}${dayPeriod ? ` ${dayPeriod}` : ''}`;
}

function offsetText(timeZone, epochMs) {
    if (timeZone === 'UTC') {
        return '+00:00';
    }

    const name = pick(offsetFormatter(timeZone).formatToParts(new Date(epochMs)), 'timeZoneName');
    if (name === 'GMT' || name === 'UTC') {
        return '+00:00';
    }

    const match = /^(?:GMT|UTC)([+-])(\d{1,2})(?::?(\d{2}))?$/.exec(name);
    if (!match) {
        throw new Error(`unsupported time zone offset: ${name}`);
    }

    return `${match[1]}${match[2].padStart(2, '0')}:${match[3] ?? '00'}`;
}

function csvField(value) {
    const text = String(value);
    return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

function seriesHeaders(series) {
    const bases = series.map(entry => {
        const base = entry.name || 'Series';
        return FORMULA_LEADER.test(base) ? `'${base}` : base;
    });
    const explicit = new Set(bases.filter((base, index) => series[index].name.length > 0));
    const used = new Set(['timestamp', 'utc_offset']);

    return bases.map((base, index) => {
        const generic = series[index].name.length === 0;
        let header = base;

        if (used.has(header) || (generic && explicit.has('Series'))) {
            let suffix = 2;
            while (used.has(`${base} (${suffix})`) || explicit.has(`${base} (${suffix})`)) {
                suffix += 1;
            }

            header = `${base} (${suffix})`;
        }

        used.add(header);
        return header;
    });
}

function readPoint(seriesId, point) {
    const pair = Array.isArray(point);
    const epochMs = pair ? point[0] : point?.epochMs;
    const value = pair ? point[1] : point?.value;

    if (typeof epochMs !== 'number' || !Number.isFinite(epochMs)) {
        throw new Error(`series ${seriesId} has a point without a finite epoch`);
    }

    if (typeof value !== 'number' || !Number.isFinite(value)) {
        throw new Error(`series ${seriesId} has a point without a finite value`);
    }

    return { epochMs, value };
}

function readSnapshot(snapshot, selectedSeriesIds) {
    if (!snapshot || typeof snapshot !== 'object') {
        throw new Error('chart CSV export requires a captured snapshot');
    }

    if (snapshot.zone !== 'utc' && snapshot.zone !== 'local') {
        throw new Error(`chart CSV export requires a supported zone: ${snapshot.zone}`);
    }

    if (snapshot.clockFormat !== '12h' && snapshot.clockFormat !== '24h') {
        throw new Error(`chart CSV export requires a supported clock format: ${snapshot.clockFormat}`);
    }

    const timeZone = snapshot.zone === 'utc' ? 'UTC' : snapshot.timeZone;
    if (typeof timeZone !== 'string' || timeZone.length === 0) {
        throw new Error('chart CSV export requires a captured IANA time zone');
    }

    if (!Array.isArray(snapshot.series)) {
        throw new Error('chart CSV export requires a captured series list');
    }

    if (!Array.isArray(selectedSeriesIds) || selectedSeriesIds.length === 0) {
        throw new Error('chart CSV export requires a selection of at least one series');
    }

    const seriesById = new Map();
    for (const entry of snapshot.series) {
        if (!entry || typeof entry !== 'object' || typeof entry.id !== 'string' || entry.id.length === 0) {
            throw new Error('chart CSV export requires every series to have a stable id');
        }

        if (typeof entry.name !== 'string') {
            throw new Error(`series ${entry.id} requires a display name`);
        }

        if (!Array.isArray(entry.points)) {
            throw new Error(`series ${entry.id} requires a point list`);
        }

        if (seriesById.has(entry.id)) {
            throw new Error(`duplicate series id: ${entry.id}`);
        }

        seriesById.set(entry.id, entry);
    }

    const selectedIds = new Set();
    for (const id of selectedSeriesIds) {
        if (typeof id !== 'string') {
            throw new Error('chart CSV export selection requires stable series ids');
        }

        selectedIds.add(id);
    }

    for (const id of selectedIds) {
        if (!seriesById.has(id)) {
            throw new Error(`unknown selected series: ${id}`);
        }
    }

    return {
        clockFormat: snapshot.clockFormat,
        timeZone,
        series: snapshot.series.filter(entry => selectedIds.has(entry.id)),
    };
}

export function formatChartCsv(snapshot, selectedSeriesIds) {
    const captured = readSnapshot(snapshot, selectedSeriesIds);
    const rows = new Map();

    for (const entry of captured.series) {
        const occurrences = new Map();
        for (const point of entry.points) {
            const { epochMs, value } = readPoint(entry.id, point);
            const occurrence = occurrences.get(epochMs) ?? 0;
            occurrences.set(epochMs, occurrence + 1);

            let instances = rows.get(epochMs);
            if (!instances) {
                instances = [];
                rows.set(epochMs, instances);
            }

            let row = instances[occurrence];
            if (!row) {
                row = new Map();
                instances[occurrence] = row;
            }

            row.set(entry.id, value);
        }
    }

    const headers = seriesHeaders(captured.series);
    const lines = [[
        'timestamp',
        'utc_offset',
        ...headers,
    ].map(csvField).join(',')];

    const instants = [...rows.keys()].sort((left, right) => left - right);
    for (const epochMs of instants) {
        const timestamp = csvField(timestampText(epochMs, captured.timeZone, captured.clockFormat));
        const offset = csvField(offsetText(captured.timeZone, epochMs));

        for (const row of rows.get(epochMs)) {
            const values = captured.series.map(entry => row.has(entry.id) ? String(row.get(entry.id)) : '');
            lines.push([timestamp, offset, ...values].join(','));
        }
    }

    return `${BOM}${lines.join(RECORD_SEPARATOR)}${RECORD_SEPARATOR}`;
}
