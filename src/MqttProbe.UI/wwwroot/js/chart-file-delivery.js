const DEFAULT_MIME_TYPE = 'application/octet-stream';

function safeFileName(fileName) {
    const raw = typeof fileName === 'string' ? fileName : '';
    const leaf = raw.split(/[\\/]/).pop() ?? '';

    return leaf
        .replace(/[\u0000-\u001F\u007F]/g, '')
        .replace(/[<>:"|?*]/g, '_')
        .replace(/^[\s.]+|[\s.]+$/g, '') || 'download';
}

function toBlob(content, mimeType) {
    if (typeof Blob !== 'function') {
        throw new Error('file downloads require Blob support');
    }

    const type = typeof mimeType === 'string' && mimeType.length > 0
        ? mimeType
        : DEFAULT_MIME_TYPE;

    if (typeof content === 'string') {
        return new Blob([content], { type });
    }

    if (content instanceof Blob) {
        return content;
    }

    if (content instanceof ArrayBuffer || ArrayBuffer.isView(content)) {
        return new Blob([content], { type });
    }

    throw new TypeError('file content must be a string, Blob, ArrayBuffer or typed array');
}

export async function downloadFile(fileName, content, mimeType) {
    const document = globalThis.document;
    const urls = globalThis.URL;
    if (!document
        || typeof document.createElement !== 'function'
        || !document.body
        || typeof urls?.createObjectURL !== 'function'
        || typeof urls?.revokeObjectURL !== 'function') {
        throw new Error('file downloads require a browser document');
    }

    const blob = toBlob(content, mimeType);
    const url = urls.createObjectURL(blob);
    let anchor;

    try {
        anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = safeFileName(fileName);
        anchor.hidden = true;
        document.body.appendChild(anchor);
        anchor.click();
    } finally {
        try {
            if (typeof anchor?.remove === 'function') {
                anchor.remove();
            } else if (anchor?.parentNode) {
                anchor.parentNode.removeChild(anchor);
            }
        } finally {
            urls.revokeObjectURL(url);
        }
    }
}
