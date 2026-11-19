import { getChartExportSnapshot } from './chart-renderer.js?v=8';
import { formatChartCsv } from './chart-csv.js';
import { downloadFile } from './chart-file-delivery.js';

const ECHARTS_MODULE_PATH = './echarts/6.1.0/echarts.esm.min.js';
const FORMAT_MIME_TYPES = {
    csv: 'text/csv;charset=utf-8',
    png: 'image/png',
    svg: 'image/svg+xml;charset=utf-8',
};
// Mirrors the live card surface so exported text stays readable outside the app theme.
const EXPORT_BACKGROUNDS = {
    light: '#F1F5F9',
    dark: '#1E293B',
};

const snapshots = new Map();
let snapshotSequence = 0;
let echartsModule;

async function loadEcharts() {
    if (!echartsModule) {
        echartsModule = await import(ECHARTS_MODULE_PATH);
    }

    return echartsModule;
}

// Copy plain data and arrays while keeping formatter functions by reference.
function cloneValue(value) {
    if (Array.isArray(value)) {
        return value.map(cloneValue);
    }

    if (value && typeof value === 'object') {
        const copy = {};
        for (const [key, entry] of Object.entries(value)) {
            copy[key] = cloneValue(entry);
        }

        return copy;
    }

    return value;
}

function browserTimeZone() {
    try {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        return typeof zone === 'string' && zone.length > 0 ? zone : 'UTC';
    } catch {
        return 'UTC';
    }
}

function legendOption(option, ids) {
    const entries = Array.isArray(option.legend)
        ? option.legend
        : option.legend ? [option.legend] : [];
    if (entries.length === 0) {
        return { show: false };
    }

    const selected = {};
    for (const id of ids) {
        selected[id] = true;
    }

    return entries.map(entry => ({ ...entry, data: [...ids], selected }));
}

function imageOption(snapshot, ids) {
    const option = cloneValue(snapshot.option);
    option.animation = false;
    option.backgroundColor = snapshot.spec?.dark === true
        ? EXPORT_BACKGROUNDS.dark
        : EXPORT_BACKGROUNDS.light;
    option.tooltip = { show: false };
    option.legend = legendOption(option, ids);
    option.series = (option.series ?? []).filter(entry => ids.includes(entry.id));
    return option;
}

function hasDrawableSize(width, height) {
    return Number.isFinite(width) && width > 0
        && Number.isFinite(height) && height > 0;
}

function csvSnapshot(snapshot) {
    return {
        zone: snapshot.spec.zone === 'utc' ? 'utc' : 'local',
        clockFormat: snapshot.spec.clockFormat === '12h' ? '12h' : '24h',
        timeZone: snapshot.timeZone,
        series: snapshot.spec.series ?? [],
    };
}

async function renderSvg(option, snapshot, fileName) {
    const echarts = await loadEcharts();
    let chart;
    try {
        chart = echarts.init(null, null, {
            renderer: 'svg',
            ssr: true,
            width: snapshot.width,
            height: snapshot.height,
        });
        chart.setOption(option, { notMerge: true, lazyUpdate: false });
        await downloadFile(fileName, chart.renderToSVGString(), FORMAT_MIME_TYPES.svg);
    } finally {
        chart?.dispose();
    }
}

async function renderPng(option, snapshot, fileName) {
    const echarts = await loadEcharts();
    const element = document.createElement('div');
    let chart;
    try {
        chart = echarts.init(element, null, {
            renderer: 'canvas',
            width: snapshot.width,
            height: snapshot.height,
        });
        chart.setOption(option, { notMerge: true, lazyUpdate: false });
        const response = await fetch(chart.getDataURL({ type: 'png' }));
        await downloadFile(fileName, await response.blob(), FORMAT_MIME_TYPES.png);
    } finally {
        chart?.dispose();
        element.remove();
    }
}

export function captureSnapshot(chartId) {
    const captured = getChartExportSnapshot(chartId);
    if (!captured || !hasDrawableSize(captured.width, captured.height)) {
        return null;
    }

    const id = `chart-export-${++snapshotSequence}`;
    const spec = cloneValue(captured.spec) ?? { series: [] };
    snapshots.set(id, {
        spec,
        option: cloneValue(captured.option) ?? {},
        width: captured.width,
        height: captured.height,
        timeZone: browserTimeZone(),
    });

    return {
        id,
        zone: spec.zone === 'utc' ? 'utc' : 'local',
        clockFormat: spec.clockFormat === '12h' ? '12h' : '24h',
        series: (spec.series ?? []).map(entry => ({
            id: entry.id,
            name: entry.name,
            color: entry.color,
            pointCount: (entry.points ?? []).length,
        })),
    };
}

export async function exportSnapshot(snapshotId, selectedSeriesIds, format, fileName) {
    const snapshot = snapshots.get(snapshotId);
    if (!snapshot) {
        throw new Error(`the chart export snapshot ${snapshotId} is no longer available`);
    }

    if (!(format in FORMAT_MIME_TYPES)) {
        throw new Error(`the ${format} export format is not supported`);
    }

    const requested = new Set(selectedSeriesIds ?? []);
    const capturedIds = new Set((snapshot.spec.series ?? []).map(entry => entry.id));
    const unknown = [...requested].filter(id => !capturedIds.has(id));
    if (unknown.length > 0) {
        throw new Error(
            `the chart export selection contains an unknown selected series: ${unknown.join(', ')}`);
    }

    const series = (snapshot.spec.series ?? []).filter(entry => requested.has(entry.id));
    if (series.length === 0) {
        throw new Error('select at least one series to export');
    }

    const ids = series.map(entry => entry.id);
    if (format === 'csv') {
        await downloadFile(fileName, formatChartCsv(csvSnapshot(snapshot), ids),
            FORMAT_MIME_TYPES.csv);
        return;
    }

    const option = imageOption(snapshot, ids);
    if (format === 'svg') {
        await renderSvg(option, snapshot, fileName);
        return;
    }

    await renderPng(option, snapshot, fileName);
}

export function releaseSnapshot(snapshotId) {
    snapshots.delete(snapshotId);
}
