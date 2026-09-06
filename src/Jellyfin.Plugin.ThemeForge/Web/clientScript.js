/*
 * ThemeForge client script.
 *
 * Adds a "Set theme song" action to movie and series detail pages for administrators.
 *
 * This script deliberately contains no playback or volume code. Jellyfin plays theme songs
 * through the same player as everything else and reloads its saved global volume whenever it
 * creates a media element, so a volume set from here is overwritten moments later and, worse,
 * leaks into the user's volume for normal playback. ThemeForge solves loudness by normalising
 * every theme when it is encoded, which works on every client rather than only in the browser.
 * Whether themes play at all remains Jellyfin's own per-user display setting.
 */
(function () {
    'use strict';

    var BUTTON_CLASS = 'themeforge-detail-button';
    var STYLE_ID = 'themeforge-styles';

    function ready() {
        return typeof ApiClient !== 'undefined' && ApiClient && typeof ApiClient.getUrl === 'function';
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text === null || text === undefined ? '' : String(text);
        return div.innerHTML;
    }

    /** Reads the item id out of the detail page URL. */
    function currentItemId() {
        var hash = window.location.hash || '';
        var match = hash.match(/[?&]id=([0-9a-fA-F-]{32,36})/);
        return match ? match[1] : null;
    }

    function request(method, path, body) {
        var options = {
            method: method,
            headers: {
                'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"'
            }
        };

        if (body !== undefined) {
            options.headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(body);
        }

        return fetch(ApiClient.getUrl('ThemeForge/' + path), options).then(function (response) {
            if (!response.ok) {
                throw new Error('ThemeForge request failed with status ' + response.status);
            }
            return response.json();
        });
    }

    function injectStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent =
            '.themeforge-overlay{position:fixed;inset:0;background:rgba(0,0,0,.8);z-index:10000;' +
            'display:flex;align-items:center;justify-content:center;}' +
            '.themeforge-dialog{background:#1e1e1e;color:#fff;border-radius:10px;padding:24px;' +
            'width:min(520px,92vw);box-shadow:0 8px 32px rgba(0,0,0,.5);}' +
            '.themeforge-dialog h3{margin:0 0 4px;}' +
            '.themeforge-dialog p{margin:0 0 16px;color:#aaa;font-size:.9em;}' +
            '.themeforge-dialog input{width:100%;padding:10px;border-radius:6px;border:1px solid #444;' +
            'background:#111;color:#fff;box-sizing:border-box;}' +
            '.themeforge-actions{display:flex;gap:8px;justify-content:flex-end;margin-top:16px;}' +
            '.themeforge-actions button{padding:8px 16px;border-radius:6px;border:0;cursor:pointer;}' +
            '.themeforge-actions .primary{background:#00a4dc;color:#fff;}' +
            '.themeforge-actions .secondary{background:#333;color:#ddd;}' +
            '.themeforge-status{margin-top:12px;font-size:.9em;min-height:1.2em;}';

        document.head.appendChild(style);
    }

    function openDialog(itemId, itemName) {
        injectStyles();

        var overlay = document.createElement('div');
        overlay.className = 'themeforge-overlay';
        overlay.innerHTML =
            '<div class="themeforge-dialog">' +
                '<h3>Set theme song</h3>' +
                '<p>' + escapeHtml(itemName || 'this item') + '</p>' +
                '<input type="url" class="themeforge-url" placeholder="https://www.youtube.com/watch?v=..." />' +
                '<div class="themeforge-status"></div>' +
                '<div class="themeforge-actions">' +
                    '<button type="button" class="secondary themeforge-cancel">Cancel</button>' +
                    '<button type="button" class="primary themeforge-save">Download and set</button>' +
                '</div>' +
            '</div>';

        function close() {
            if (overlay.parentNode) {
                overlay.parentNode.removeChild(overlay);
            }
        }

        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                close();
            }
        });
        overlay.querySelector('.themeforge-cancel').addEventListener('click', close);

        var status = overlay.querySelector('.themeforge-status');
        var save = overlay.querySelector('.themeforge-save');

        save.addEventListener('click', function () {
            var url = overlay.querySelector('.themeforge-url').value.trim();
            if (!url) {
                status.textContent = 'Enter a URL first.';
                return;
            }

            save.disabled = true;
            status.textContent = 'Downloading and normalising. This can take a minute.';

            request('POST', 'Items/' + itemId + '/Assign', { Url: url }).then(function (result) {
                status.textContent = result.Message;
                save.disabled = false;
                if (result.Success) {
                    setTimeout(close, 1500);
                }
            }, function (error) {
                status.textContent = error.message;
                save.disabled = false;
            });
        });

        document.body.appendChild(overlay);
        overlay.querySelector('.themeforge-url').focus();
    }

    /** Adds the button to a detail page's action row, at most once per page. */
    function tryAddButton() {
        var container = document.querySelector('.mainDetailButtons:not([data-themeforge])');
        if (!container) {
            return;
        }

        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        container.setAttribute('data-themeforge', '1');

        var button = document.createElement('button');
        button.className = 'button-flat detailButton emby-button ' + BUTTON_CLASS;
        button.type = 'button';
        button.title = 'Set theme song with ThemeForge';
        button.innerHTML =
            '<div class="detailButton-content">' +
            '<span class="material-icons detailButton-icon" aria-hidden="true">music_note</span>' +
            '</div>';

        button.addEventListener('click', function () {
            var heading = document.querySelector('.itemName.infoText, .pageTitle, h1.itemName');
            openDialog(itemId, heading ? heading.textContent.trim() : null);
        });

        container.appendChild(button);
    }

    function start() {
        // Only administrators can change anything, so nobody else is shown the control. The API
        // enforces this independently; hiding the button is a courtesy, not the security boundary.
        ApiClient.getCurrentUser().then(function (user) {
            if (!user || !user.Policy || !user.Policy.IsAdministrator) {
                return;
            }

            // Jellyfin's web UI is a single-page app that rebuilds the detail view on every
            // navigation, so the button has to be re-added rather than attached once.
            var observer = new MutationObserver(tryAddButton);
            observer.observe(document.body, { childList: true, subtree: true });
            tryAddButton();
        }).catch(function () {
            // Not signed in yet; the next page load runs this again.
        });
    }

    function waitForApiClient(attemptsLeft) {
        if (ready()) {
            start();
            return;
        }

        if (attemptsLeft > 0) {
            setTimeout(function () { waitForApiClient(attemptsLeft - 1); }, 500);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { waitForApiClient(60); });
    } else {
        waitForApiClient(60);
    }
})();
