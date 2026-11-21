(() => {
    'use strict';
    if (window.__subtitleDownloadPlugin) return;
    window.__subtitleDownloadPlugin = true;

    const ACTION = 'download-all-subtitles';
    const ITEM_ID = /^[a-f\d]{32}$|^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i;
    const pending = new Map();
    let origin = null;
    let generation = 0;
    let scheduled = false;

    function detailsId() {
        const hash = window.location.hash;
        if (!/^#\/?details(?:\?|$)/.test(hash)) return null;
        return new URLSearchParams(hash.split('?')[1]).get('id');
    }

    function remember(event) {
        const target = event.target instanceof Element ? event.target : null;
        if (!target || target.closest('.actionSheet')) return;
        const holder = target.closest('.card[data-id], .listItem[data-id], [data-type][data-id]');
        const trigger = target.closest('[data-action="menu"], .btnMoreCommands, .itemAction-more, [data-action="more"]');
        const isContext = event.type === 'contextmenu';
        const isLongPress = event.type === 'pointerdown' && holder;
        if (!trigger && !isContext && !isLongPress) return;
        const id = holder?.dataset.id || (trigger?.matches('.btnMoreCommands') ? detailsId() : null);
        generation++;
        origin = id && ITEM_ID.test(id) ? { id, generation, time: Date.now() } : null;
    }

    function notice(message, abort) {
        let element = document.getElementById('subtitle-download-status');
        if (!element) {
            element = document.createElement('div');
            element.id = 'subtitle-download-status';
            element.setAttribute('role', 'status');
            element.setAttribute('aria-live', 'polite');
            Object.assign(element.style, {
                position: 'fixed', zIndex: '2147483647', bottom: '24px', left: '50%',
                transform: 'translateX(-50%)', background: '#202020', color: '#fff',
                padding: '14px 20px', borderRadius: '5px', boxShadow: '0 2px 14px #0008',
                maxWidth: 'min(560px, 90vw)', boxSizing: 'border-box', fontSize: '15px'
            });
            document.body.append(element);
        }
        clearTimeout(element.dismissTimer);
        element.textContent = message;
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = abort ? 'Cancel' : 'Dismiss';
        Object.assign(button.style, { marginLeft: '16px', color: '#fff', background: 'transparent', border: '1px solid #aaa', borderRadius: '3px', cursor: 'pointer', padding: '4px 8px' });
        button.addEventListener('click', () => { if (abort) abort(); element.remove(); });
        element.append(button);
        if (!abort) element.dismissTimer = setTimeout(() => element.remove(), 12000);
    }

    async function fetchApi(api, route, signal) {
        return fetch(api.getUrl(route), {
            headers: { Authorization: `MediaBrowser Token="${api.accessToken()}"` },
            credentials: 'same-origin', cache: 'no-store', signal
        });
    }

    async function download(api, itemId, filename, button) {
        if (pending.has(itemId)) return;
        const controller = new AbortController();
        pending.set(itemId, controller);
        button.disabled = true;
        button.setAttribute('aria-busy', 'true');
        notice('Preparing subtitles…', () => controller.abort());
        try {
            const response = await fetchApi(api, `SubtitleDownload/Items/${itemId}/Download`, controller.signal);
            if (!response.ok) {
                const error = await response.json().catch(() => null);
                throw new Error(error?.detail || error?.Detail || error?.title || error?.Title || `Download failed (HTTP ${response.status}).`);
            }
            const blob = await response.blob();
            if (controller.signal.aborted) return;
            const url = URL.createObjectURL(blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = filename;
            link.hidden = true;
            document.body.append(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(url), 60000);
            const count = Number(response.headers.get('X-Subtitle-Count'));
            const failed = Number(response.headers.get('X-Subtitle-Failed'));
            notice(failed ? `${count} subtitle tracks downloaded; ${failed} could not be exported. See SUBTITLE-EXPORT-ERRORS.txt in the ZIP.`
                : `${count} subtitle ${count === 1 ? 'track' : 'tracks'} downloaded.`);
        } catch (error) {
            notice(error.name === 'AbortError' ? 'Subtitle download cancelled.' : error.message);
        } finally {
            pending.delete(itemId);
            button.disabled = false;
            button.removeAttribute('aria-busy');
        }
    }

    async function augment(sheet, context) {
        const original = sheet.querySelector('.actionSheetMenuItem[data-id="download"]');
        if (!original || sheet.querySelector(`[data-id="${ACTION}"]`)) return;
        // Bind an item to this specific popup before any asynchronous metadata request.
        // In particular, a card opened on another movie's details page must use the card.
        sheet.dataset.subtitleDownloadBound = 'true';
        const api = window.ApiClient;
        if (!api || !context) return;
        try {
            const response = await fetchApi(api, `SubtitleDownload/Items/${context.id}/Info`);
            if (!response.ok || !sheet.isConnected || context.generation !== generation) return;
            const info = await response.json();
            const count = info.Count ?? info.count;
            const filename = info.FileName ?? info.fileName;
            const row = original.cloneNode(true);
            row.dataset.id = ACTION;
            row.dataset.itemId = context.id;
            row.removeAttribute('id');
            row.removeAttribute('href');
            row.querySelector('.actionSheetItemText').textContent = 'Download subtitles';
            const icon = row.querySelector('.material-icons');
            if (icon) {
                icon.className = 'actionsheetMenuItemIcon material-icons closed_caption';
                icon.textContent = '';
            }
            row.disabled = count === 0;
            row.title = count === 0 ? 'No subtitles detected' : 'Download all subtitle tracks';
            row.setAttribute('aria-label', 'Download subtitles');
            row.addEventListener('click', (event) => {
                event.preventDefault();
                event.stopImmediatePropagation();
                if (count > 0) {
                    void download(api, context.id, filename, row);
                    // Jellyfin records an open popup as a history entry. Pop only
                    // that entry so its own dialog helper performs the cleanup.
                    if (sheet.dataset.history === 'true' && window.history.state?.usr?.dialogs?.length) {
                        window.history.back();
                    } else {
                        sheet.querySelector('.btnCloseActionSheet')?.click();
                    }
                }
            });
            original.after(row);
            // Late insertion must stay inside the viewport on short/mobile displays.
            const rect = sheet.getBoundingClientRect();
            if (rect.bottom > window.innerHeight - 10 && sheet.style.position === 'fixed') {
                sheet.style.top = `${Math.max(10, rect.top - (rect.bottom - window.innerHeight + 10))}px`;
            }
        } catch (error) {
            console.warn('Subtitle Download: could not add the menu action.', error);
        }
    }

    function scan() {
        scheduled = false;
        const context = origin && Date.now() - origin.time < 15000 ? origin : null;
        if (!context) return;
        for (const sheet of document.querySelectorAll('.actionSheet:not([data-subtitle-download-bound])')) {
            if (sheet.querySelector('.actionSheetMenuItem[data-id="download"]')) {
                origin = null;
                void augment(sheet, context);
                break;
            }
        }
    }

    for (const type of ['pointerdown', 'click', 'contextmenu']) document.addEventListener(type, remember, true);
    document.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ' || event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
            remember({ target: event.target, type: event.key === 'ContextMenu' || event.shiftKey ? 'contextmenu' : 'click' });
        }
    }, true);
    window.addEventListener('hashchange', () => { origin = null; generation++; });
    new MutationObserver(() => {
        if (!scheduled) { scheduled = true; queueMicrotask(scan); }
    }).observe(document.body, { childList: true, subtree: true });
})();
