// Updates on their way from this phone (David, 25 Nov 2026). Everything added to an update is kept on the phone until it
// has been sent, goes up in pieces while the update is written, and carries on after a dropped signal, on any page of the
// app and the next time it's opened. .NET shows it (UpdateOutbox.cs); the files never pass through .NET.

const HEADER = 'X-Paddockside-Request';
const KEEP_DAYS = 14;
const TYPES = {
    '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.png': 'image/png', '.webp': 'image/webp', '.gif': 'image/gif', '.heic': 'image/heic', '.heif': 'image/heic',
    '.mp4': 'video/mp4', '.m4v': 'video/mp4', '.mov': 'video/quicktime', '.webm': 'video/webm',
    '.mp3': 'audio/mpeg', '.m4a': 'audio/mp4', '.aac': 'audio/aac', '.wav': 'audio/wav', '.ogg': 'audio/ogg'
};
const MAX_BYTES = 250 * 1024 * 1024, MAX_FILES = 10;

let owner = null;             // who these belong to: a phone can have more than one member of staff
let outbox = null;            // the .NET side
let updates = new Map();      // id → update
const blobs = new Map();      // file key → Blob, for this visit
const urls = new Map();       // file key → object URL
let database = null, pumping = false, again = false, stopped = null, backoff = 0, timer = null, sending = null;

// ---- kept on the phone ---------------------------------------------------------------------------------------------

function open() {
    return database ??= new Promise((resolve, reject) => {
        const request = indexedDB.open('paddockside-updates', 1);
        request.onupgradeneeded = () => { request.result.createObjectStore('updates', { keyPath: 'id' }); request.result.createObjectStore('files'); };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function store(name, mode, work) {
    const db = await open();
    return await new Promise((resolve, reject) => {
        const transaction = db.transaction(name, mode);
        const request = work(transaction.objectStore(name));
        transaction.oncomplete = () => resolve(request?.result);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error);
    });
}

// A phone in private browsing, or full, may refuse: the update still works, it just isn't there after the page closes.
async function keep(update) {
    update.touched = Date.now();
    try { await store('updates', 'readwrite', s => s.put(update)); } catch { update.unkept = true; }
}

async function keepBlob(file) {
    try { await store('files', 'readwrite', s => s.put(blobs.get(file.key), file.key)); file.kept = true; } catch { file.kept = false; }
}

async function blobOf(file) {
    if (!blobs.has(file.key)) {
        try { const blob = await store('files', 'readonly', s => s.get(file.key)); if (blob) blobs.set(file.key, blob); } catch { /* not kept */ }
    }
    return blobs.get(file.key);
}

async function forget(update) {
    updates.delete(update.id);
    for (const file of update.files) await forgetFile(file);
    try { await store('updates', 'readwrite', s => s.delete(update.id)); } catch { /* it was never kept */ }
}

async function forgetFile(file) {
    if (urls.has(file.key)) { URL.revokeObjectURL(urls.get(file.key)); urls.delete(file.key); }
    blobs.delete(file.key);
    try { await store('files', 'readwrite', s => s.delete(file.key)); } catch { /* it was never kept */ }
}

// ---- telling .NET --------------------------------------------------------------------------------------------------

let told = 0, tell = null;

function changed(now) {
    if (!outbox) return;
    const wait = now ? 0 : Math.max(0, 300 - (Date.now() - told));
    if (tell && !now) return;
    clearTimeout(tell);
    tell = setTimeout(() => {
        tell = null;
        told = Date.now();
        outbox.invokeMethodAsync('UpdatesChanged', state()).catch(() => { });
    }, wait);
}

function state() {
    return {
        online: navigator.onLine && !backoff, needsSignIn: stopped === 'signin',
        updates: [...updates.values()].sort((a, b) => a.created - b.created).map(u => ({
            id: u.id, horseId: u.horseId, horse: u.horse, state: u.state, body: u.body, eventId: u.eventId, options: u.options, error: u.error ?? null,
            recipients: u.recipients ?? 0, sentEventId: u.sentEventId ?? null, unkept: !!u.unkept,
            files: u.files.map(f => ({
                key: f.key, name: f.name, kind: f.kind, size: f.size, seconds: f.seconds ?? null, state: f.state, error: f.error ?? null,
                percent: f.state === 'done' ? 100 : Math.min(99, Math.floor(100 * (f.sent ?? 0) / f.size)), url: urls.get(f.key) ?? null
            }))
        }))
    };
}

// ---- starting ------------------------------------------------------------------------------------------------------

/** Whoever is signed in. Theirs are loaded and carry on; anyone's left for a fortnight go. */
export async function start(dotnet, who) {
    outbox = dotnet;
    if (owner !== who) {
        owner = who;
        updates = new Map();
        let kept = [];
        try { kept = await store('updates', 'readonly', s => s.getAll()) ?? []; } catch { /* nothing kept */ }
        for (const update of kept) {
            if (Date.now() - update.touched > KEEP_DAYS * 864e5) { await forget(update); continue; }
            if (update.owner !== who) continue;
            for (const file of update.files) {
                const blob = await blobOf(file);
                if (blob) urls.set(file.key, URL.createObjectURL(blob));
                else if (file.state !== 'done') { file.state = 'failed'; file.error = 'This is no longer on the phone. Take it off and add it again.'; }
            }
            updates.set(update.id, update);
        }
    }
    if (stopped === 'signin') stopped = null;
    for (const update of updates.values()) if (update.state === 'sending') for (const f of update.files) if (f.state === 'failed' && f.kept) { f.state = 'waiting'; f.error = null; }
    pump();
    return state();
}

/** The update being written for this horse, or a new one. */
export async function draft(horseId, horse) {
    let update = [...updates.values()].find(u => u.horseId === horseId && (u.state === 'draft' || u.state === 'failed'));
    if (!update) {
        update = { id: crypto.randomUUID(), key: crypto.randomUUID(), owner, horseId, horse, state: 'draft', body: '', eventId: null, options: null, files: [], created: Date.now(), touched: Date.now() };
        updates.set(update.id, update);
    }
    return { id: update.id, state: state() };
}

export async function save(id, body, eventId, options) {
    const update = updates.get(id);
    if (!update || (update.state !== 'draft' && update.state !== 'failed')) return;
    Object.assign(update, { body, eventId, options });
    if (body || update.files.length) await keep(update);
}

export async function discard(id) {
    const update = updates.get(id);
    if (!update) return;
    for (const file of update.files) abandon(update, file);
    await forget(update);
    changed(true);
}

// ---- adding and taking off -----------------------------------------------------------------------------------------

/** The phone's own camera or files: a plain file input, so it opens exactly as the phone does it. */
export function bind(input, id) {
    input.onchange = async () => {
        const chosen = [...input.files];
        input.value = '';
        for (const file of chosen) await add(id, file, file.name, null);
    };
}

async function add(id, blob, name, seconds) {
    const update = updates.get(id);
    if (!update) return;
    let type = (blob.type || '').split(';')[0].toLowerCase();
    if (!type || type === 'application/octet-stream' || type === 'image/heif') type = TYPES[name.slice(name.lastIndexOf('.')).toLowerCase()] ?? type;
    const kind = type.startsWith('image/') ? 'Photo' : type.startsWith('video/') ? 'Video' : type.startsWith('audio/') ? 'Audio' : null;
    update.error = null;
    if (!kind) { update.error = `${name} isn't a photo, video or voice note.`; changed(true); return; }
    if (update.files.length >= MAX_FILES) { update.error = `Add up to ${MAX_FILES} at a time.`; changed(true); return; }
    if (blob.size > MAX_BYTES) { update.error = `${name} is too big: the most is 250 MB. A shorter video, or a lower quality in the phone's camera settings, will fit.`; changed(true); return; }
    if (!blob.size) { update.error = `${name} is empty.`; changed(true); return; }
    if (type === 'image/heic') [blob, name, type] = await jpeg(blob, name); // only Apple's own apps show these

    const file = { key: crypto.randomUUID(), name, type, kind, size: blob.size, seconds, state: 'waiting', sent: 0 };
    blobs.set(file.key, blob);
    urls.set(file.key, URL.createObjectURL(blob));
    update.files.push(file);
    changed(true);
    await keepBlob(file);
    await keep(update);
    pump();
}

async function jpeg(blob, name) {
    try {
        const picture = await createImageBitmap(blob);
        const canvas = document.createElement('canvas');
        [canvas.width, canvas.height] = [picture.width, picture.height];
        canvas.getContext('2d').drawImage(picture, 0, 0);
        const made = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.9));
        if (made) return [made, name.replace(/\.hei[cf]$/i, '.jpg'), 'image/jpeg'];
    } catch { /* this phone can't: it goes as it is */ }
    return [blob, name, 'image/heic'];
}

export async function remove(id, key) {
    const update = updates.get(id);
    const file = update?.files.find(f => f.key === key);
    if (!file) return;
    update.files = update.files.filter(f => f !== file);
    update.error = null;
    abandon(update, file);
    await forgetFile(file);
    await keep(update);
    changed(true);
}

function abandon(update, file) {
    file.gone = true;
    if (sending?.file === file) sending.request.abort();
    if (file.uploadId) post(`/api/uploads/${file.uploadId}/remove`, {}).catch(() => { });
}

/** Sent as soon as everything on it is up, whatever page they're on by then. */
export async function send(id, payload) {
    const update = updates.get(id);
    if (!update) return;
    Object.assign(update, { state: 'sending', payload, error: null });
    for (const file of update.files) if (file.state === 'failed') { file.state = 'waiting'; file.error = null; }
    await keep(update);
    changed(true);
    pump();
}

/** Back to writing it, when it was refused or hasn't gone yet. */
export async function edit(id) {
    const update = updates.get(id);
    if (!update || update.posting) return false;
    update.state = 'draft';
    await keep(update);
    changed(true);
    return true;
}

/** Seen: it was sent, and there's nothing more to show about it. */
export async function seen(id) {
    const update = updates.get(id);
    if (update?.state === 'sent') { updates.delete(id); changed(true); }
}

export function retry() { backoff = 0; clearTimeout(timer); if (stopped !== 'signin') stopped = null; pump(); }

// ---- going up ------------------------------------------------------------------------------------------------------

class Refused extends Error { constructor(status, title) { super(title); this.status = status; } }
class NoSignal extends Error { }

async function answer(response) {
    if (response.ok) return response.status === 204 ? null : await response.json();
    let title = null;
    try { title = (await response.json()).title; } catch { /* not one of ours */ }
    if (response.status === 401) throw new Refused(401, 'Sign in again.');
    if (response.status >= 500 || response.status === 429) throw new NoSignal(); // the server's trouble: wait and try again
    throw new Refused(response.status, title ?? 'That was refused.');
}

async function post(path, body) {
    let response;
    try {
        response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', [HEADER]: '1' }, body: JSON.stringify(body) });
    } catch { throw new NoSignal(); }
    return await answer(response);
}

// XMLHttpRequest, not fetch: it says how far a piece has got.
function put(path, piece, file, before) {
    return new Promise((resolve, reject) => {
        const request = new XMLHttpRequest();
        sending = { file, request };
        request.open('PUT', path);
        request.setRequestHeader(HEADER, '1');
        request.setRequestHeader('Content-Type', 'application/octet-stream');
        request.timeout = 120000;
        request.upload.onprogress = e => { file.sent = before + e.loaded; changed(); };
        request.onload = () => {
            if (request.status >= 200 && request.status < 300) return resolve();
            if (request.status >= 500 || request.status === 429) return reject(new NoSignal());
            let title = null;
            try { title = JSON.parse(request.responseText).title; } catch { /* not one of ours */ }
            reject(new Refused(request.status, title ?? 'That was refused.'));
        };
        request.onerror = request.ontimeout = request.onabort = () => reject(new NoSignal());
        request.send(piece);
    }).finally(() => { sending = null; });
}

async function upload(update, file) {
    const blob = await blobOf(file);
    if (!blob) throw new Refused(0, 'This is no longer on the phone. Take it off and add it again.');
    file.state = 'uploading';
    for (let attempt = 0; ; attempt++) {
        try {
            if (!file.uploadId) {
                const started = await post(`/api/horses/${update.horseId}/uploads`, { fileName: file.name, contentType: file.type, length: file.size });
                Object.assign(file, { uploadId: started.id, partBytes: started.partBytes, parts: started.parts, part: 0 });
                await keep(update);
            }
            while (file.part < file.parts) {
                if (file.gone) return;
                const from = file.part * file.partBytes;
                await put(`/api/uploads/${file.uploadId}/parts/${file.part}`, blob.slice(from, Math.min(file.size, from + file.partBytes)), file, from);
                file.part++;
                file.sent = Math.min(file.size, file.part * file.partBytes);
                backoff = 0;
                await keep(update);
            }
            if (file.gone) return;
            await post(`/api/uploads/${file.uploadId}/complete`, {});
            file.state = 'done';
            backoff = 0;
            await keep(update);
            changed(true);
            return;
        } catch (error) {
            if (file.gone) return;
            // Pieces missing at the end, or the upload itself is gone: once more from the start.
            if (error instanceof Refused && attempt < 1 && (error.status === 409 || error.status === 404)) {
                if (error.status === 404) file.uploadId = null;
                file.part = 0;
                continue;
            }
            throw error;
        }
    }
}

async function deliver(update) {
    update.posting = true;
    try {
        const sent = await post(`/api/horses/${update.horseId}/updates`, { ...update.payload, key: update.key, uploadIds: update.files.map(f => f.uploadId) });
        Object.assign(update, { state: 'sent', recipients: sent.recipients, sentEventId: sent.eventId, posting: false });
        backoff = 0;
        const shown = { ...update, files: [] };
        await forget(update);
        updates.set(shown.id, shown); // until it's been seen
    } catch (error) {
        update.posting = false;
        if (!(error instanceof Refused) || error.status === 401) throw error;
        Object.assign(update, { state: 'failed', error: error.message });
        await keep(update);
    }
    changed(true);
}

async function pump() {
    if (pumping) { again = true; return; }
    pumping = true;
    clearTimeout(timer);
    try {
        while (!stopped) {
            again = false;
            // What's been sent first, then whatever is still being written.
            const order = [...updates.values()].sort((a, b) => (a.state === 'sending' ? 0 : 1) - (b.state === 'sending' ? 0 : 1) || a.created - b.created);
            const ready = order.find(u => u.state === 'sending' && u.files.every(f => f.state === 'done'));
            if (ready) { await deliver(ready); continue; }
            let next = null;
            for (const u of order) {
                if (u.state !== 'draft' && u.state !== 'sending') continue;
                const file = u.files.find(f => f.state === 'waiting' || f.state === 'uploading');
                if (file) { next = { update: u, file }; break; }
            }
            if (!next) { if (again) continue; break; }
            try { await upload(next.update, next.file); }
            catch (error) {
                if (error instanceof Refused && error.status !== 401) {
                    Object.assign(next.file, { state: 'failed', error: error.message });
                    if (next.update.state === 'sending') Object.assign(next.update, { state: 'failed', error: `${next.file.name}: ${error.message}` });
                    await keep(next.update);
                    changed(true);
                    continue;
                }
                throw error;
            }
        }
    } catch (error) {
        if (error instanceof Refused && error.status === 401) stopped = 'signin';
        else {
            // No signal: straight away when it's back, and in a little while anyway.
            backoff = Math.min(60, (backoff || 2) * 2);
            timer = setTimeout(() => { timer = null; pump(); }, backoff * 1000);
        }
        changed(true);
    } finally {
        pumping = false;
        if (again && !stopped && !timer) pump();
    }
    awake();
}

addEventListener('online', () => { backoff = 0; clearTimeout(timer); timer = null; changed(true); pump(); });
addEventListener('offline', () => changed(true));
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') { clearTimeout(timer); timer = null; pump(); } });

// The screen stays on while a voice note is being recorded or files are going up, if the phone allows it.
let lock = null;
async function awake() {
    const busy = recorder || [...updates.values()].some(u => u.state === 'sending' || u.files.some(f => f.state === 'uploading'));
    try {
        if (busy && !lock) { lock = await navigator.wakeLock?.request('screen'); lock?.addEventListener('release', () => { lock = null; }); }
        else if (!busy && lock) await lock.release();
    } catch { /* not allowed just now */ }
}

// ---- voice ---------------------------------------------------------------------------------------------------------

// The most widely playable the phone can record: AAC in MP4 wherever it can be had, else what it has.
const VOICE = [['audio/mp4;codecs=mp4a.40.2', 'm4a'], ['audio/mp4', 'm4a'], ['audio/webm;codecs=opus', 'webm'], ['audio/ogg;codecs=opus', 'ogg'], ['', 'm4a']];
let recorder = null;

/** Starts recording into an update. Null when it started; otherwise why it couldn't, in words. */
export async function startVoice(id, canvas, clock, dotnet) {
    if (recorder) await cancelVoice();
    if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) return "This phone's browser can't record here. Record it in the phone's own voice app, then use Choose.";
    let stream;
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } }); }
    catch (error) {
        return error?.name === 'NotFoundError' ? 'No microphone was found.'
            : "Paddockside isn't allowed to use the microphone. Allow it for this site in the phone's browser settings, then try again.";
    }
    const [mime, extension] = VOICE.find(([type]) => !type || MediaRecorder.isTypeSupported(type));
    let media;
    try { media = new MediaRecorder(stream, { ...(mime ? { mimeType: mime } : {}), audioBitsPerSecond: 96000 }); }
    catch { stream.getTracks().forEach(t => t.stop()); return "This phone's browser can't record here. Record it in the phone's own voice app, then use Choose."; }

    const r = recorder = { id, stream, media, extension, chunks: [], spoken: 0, since: performance.now(), dotnet, keepIt: true };
    media.ondataavailable = e => { if (e.data.size) r.chunks.push(e.data); };
    media.onstop = () => finish(r);
    media.start(1000); // a piece a second: a call or a crash part way loses at most that
    draw(r, canvas, clock);
    awake();
    return null;
}

function spoken(r) { return (r.spoken + (r.media.state === 'recording' ? performance.now() - r.since : 0)) / 1000; }

export function pauseVoice() {
    if (recorder?.media.state !== 'recording') return;
    recorder.spoken += performance.now() - recorder.since;
    recorder.media.pause();
}

export function resumeVoice() {
    if (recorder?.media.state !== 'paused') return;
    recorder.since = performance.now();
    recorder.media.resume();
}

export async function stopVoice() {
    const r = recorder;
    if (!r) return;
    r.seconds = Math.max(1, Math.round(spoken(r)));
    const done = new Promise(resolve => { r.done = resolve; });
    if (r.media.state === 'inactive') finish(r); else r.media.stop();
    await done;
}

export async function cancelVoice() {
    if (!recorder) return;
    recorder.keepIt = false;
    await stopVoice();
}

async function finish(r) {
    if (r.finished) return;
    r.finished = true;
    if (recorder === r) recorder = null;
    cancelAnimationFrame(r.frame);
    r.audio?.close().catch(() => { });
    r.stream.getTracks().forEach(t => t.stop());
    if (r.keepIt && r.chunks.length) {
        const type = (r.media.mimeType || r.chunks[0].type || 'audio/mp4').split(';')[0];
        const name = `Voice note ${new Date().toLocaleString('en-AU', { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' }).replace(/[:/\\]/g, '.')}.${type === 'audio/webm' ? 'webm' : r.extension}`;
        await add(r.id, new Blob(r.chunks, { type }), name, r.seconds ?? Math.max(1, Math.round(spoken(r))));
    }
    awake();
    if (r.done) r.done();
    else r.dotnet.invokeMethodAsync('VoiceEnded').catch(() => { }); // the phone stopped it (a call came in): what was said is kept
}

function draw(r, canvas, clock) {
    let analyser = null, levels = null;
    try {
        r.audio = new (window.AudioContext || window.webkitAudioContext)();
        r.audio.resume?.();
        analyser = r.audio.createAnalyser();
        analyser.fftSize = 256;
        r.audio.createMediaStreamSource(r.stream).connect(analyser);
        levels = new Uint8Array(analyser.fftSize);
    } catch { /* no picture of it, but it still records */ }
    const history = [], still = matchMedia('(prefers-reduced-motion: reduce)').matches;
    let last = 0;
    const step = now => {
        if (recorder !== r) return;
        r.frame = requestAnimationFrame(step);
        if (now - last < 80) return;
        last = now;
        const seconds = Math.floor(spoken(r));
        clock.textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
        if (!analyser || still || r.media.state !== 'recording') return;
        analyser.getByteTimeDomainData(levels);
        history.push(Math.min(1, Math.max(...levels.map(l => Math.abs(l - 128))) / 90));
        const bars = Math.floor(canvas.clientWidth / 7);
        while (history.length > bars) history.shift();
        [canvas.width, canvas.height] = [canvas.clientWidth * devicePixelRatio, canvas.clientHeight * devicePixelRatio];
        const c = canvas.getContext('2d');
        c.scale(devicePixelRatio, devicePixelRatio);
        c.fillStyle = getComputedStyle(canvas).color;
        history.forEach((level, n) => { const h = Math.max(4, level * canvas.clientHeight); c.fillRect(n * 7, (canvas.clientHeight - h) / 2, 4, h); });
    };
    r.frame = requestAnimationFrame(step);
}
