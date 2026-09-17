/*
 * ThemeForge client script.
 *
 * Adds a "Set theme song" action to movie and series detail pages for administrators.
 *
 * This script deliberately contains no playback or volume code. Jellyfin plays theme songs
 * through the same player as everything else and reloads its saved global volume whenever it
 * creates a media element, so a volume set from here is overwritten moments later and, worse,
 * leaks into the user's volume for normal playback. Anything ThemeForge does to a theme's
 * level is done in the file itself, and only when a setting asks for it. Whether themes play
 * at all remains Jellyfin's own per-user display setting.
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

    /**
     * escapeHtml handles element content; an attribute also needs quotes escaped, and search
     * results carry raw YouTube titles in which a double quote is an ordinary thing.
     */
    function attr(text) {
        return escapeHtml(text).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function minutes(seconds) {
        if (seconds === null || seconds === undefined) {
            return '';
        }
        var whole = Math.round(seconds);
        return Math.floor(whole / 60) + ':' + ('0' + (whole % 60)).slice(-2);
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
                // A bare status number in front of somebody is not a message. The two that
                // actually happen are a proxy giving up on a long search and the server being
                // restarted mid-request.
                throw new Error(response.status === 504 || response.status === 502
                    ? 'The server took too long to answer. Try again, or paste a link instead.'
                    : 'ThemeForge could not be reached (' + response.status + ').');
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
            'width:min(720px,94vw);max-height:88vh;overflow:auto;box-shadow:0 8px 32px rgba(0,0,0,.5);}' +
            '.themeforge-dialog h3{margin:0 0 4px;}' +
            '.themeforge-dialog p{margin:0 0 16px;color:#aaa;font-size:.9em;}' +
            '.themeforge-dialog input{width:100%;padding:10px;border-radius:6px;border:1px solid #444;' +
            'background:#111;color:#fff;box-sizing:border-box;}' +
            '.themeforge-actions{display:flex;gap:8px;justify-content:flex-end;margin-top:16px;}' +
            '.themeforge-actions button{padding:8px 16px;border-radius:6px;border:0;cursor:pointer;}' +
            '.themeforge-actions .primary{background:#00a4dc;color:#fff;}' +
            '.themeforge-actions .secondary{background:#333;color:#ddd;}' +
            '.themeforge-status{margin-top:12px;font-size:.9em;min-height:1.2em;}' +
            '.themeforge-row{display:flex;gap:8px;align-items:center;}' +
            '.themeforge-row button{padding:8px 14px;border-radius:6px;border:0;cursor:pointer;' +
            'background:#333;color:#ddd;white-space:nowrap;}' +
            '.themeforge-results{margin-top:12px;max-height:44vh;overflow:auto;}' +
            '.themeforge-result{padding:8px 0 8px 10px;border-left:2px solid #3a3a3a;margin-bottom:8px;}' +
            '.themeforge-result.current{border-left-color:#00a4dc;}' +
            '.themeforge-result.rejected{border-left-color:#5a3a3a;}' +
            '.themeforge-result a{color:#fff;}' +
            '.themeforge-result .facts{color:#888;font-size:.85em;}' +
            '.themeforge-result .why{color:#777;font-size:.85em;}' +
            '.themeforge-result button{margin-top:6px;padding:4px 10px;border-radius:6px;border:0;' +
            'cursor:pointer;background:#00a4dc;color:#fff;font-size:.85em;}' +
            '.themeforge-or{margin:16px 0 6px;color:#888;font-size:.85em;}';

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
                '<div class="themeforge-row">' +
                    '<input type="text" class="themeforge-query" placeholder="What to search for" />' +
                    '<button type="button" class="themeforge-search">Search</button>' +
                '</div>' +
                '<div class="themeforge-results"></div>' +
                '<div class="themeforge-or">Or use a link you already have:</div>' +
                '<input type="url" class="themeforge-url" placeholder="https://www.youtube.com/watch?v=..." />' +
                '<div class="themeforge-status"></div>' +
                '<div class="themeforge-actions">' +
                    '<button type="button" class="secondary themeforge-cancel">Close</button>' +
                    '<button type="button" class="primary themeforge-save">Download and set</button>' +
                '</div>' +
            '</div>';

        function close() {
            document.removeEventListener('keydown', onKey);
            if (overlay.parentNode) {
                overlay.parentNode.removeChild(overlay);
            }
        }

        function onKey(event) {
            if (event.key === 'Escape') {
                close();
            }
        }

        document.addEventListener('keydown', onKey);

        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) {
                close();
            }
        });
        overlay.querySelector('.themeforge-cancel').addEventListener('click', close);

        var status = overlay.querySelector('.themeforge-status');
        var save = overlay.querySelector('.themeforge-save');
        var queryBox = overlay.querySelector('.themeforge-query');
        var results = overlay.querySelector('.themeforge-results');

        /** Downloads one video and makes it this item's theme. */
        function use(url, button) {
            if (!url) {
                status.textContent = 'Enter a URL first.';
                return;
            }

            if (button) {
                button.disabled = true;
            }

            save.disabled = true;
            status.textContent = 'Downloading. This can take a minute.';

            request('POST', 'Items/' + itemId + '/Assign', { Url: url }).then(function (result) {
                status.textContent = result.Message;
                save.disabled = false;
                if (button) {
                    button.disabled = false;
                }
                if (result.Success) {
                    setTimeout(close, 1500);
                }
            }, function (error) {
                status.textContent = error.message;
                save.disabled = false;
                if (button) {
                    button.disabled = false;
                }
            });
        }

        function render(found) {
            if (!found.Success || !found.Results.length) {
                results.textContent = found.Message || 'Nothing found for that search.';
                return;
            }

            var current = found.CurrentUrl
                ? '<div class="themeforge-or" style="margin-top:0;">Using now: ' +
                  '<a href="' + attr(found.CurrentUrl) + '" target="_blank" rel="noopener">' +
                  escapeHtml(found.CurrentTitle || found.CurrentUrl) + '</a></div>'
                : '';

            results.innerHTML = current + found.Results.map(function (row) {
                var facts = [row.Channel, minutes(row.DurationSeconds)].filter(function (part) {
                    return part;
                }).map(escapeHtml).join(' &middot; ');

                return '<div class="themeforge-result' +
                        (row.IsCurrent ? ' current' : (row.IsVetoed ? ' rejected' : '')) + '">' +
                    '<div><a href="' + attr(row.Url) + '" target="_blank" rel="noopener">' +
                        escapeHtml(row.Title) + '</a>' +
                        (row.IsCurrent ? ' &mdash; in use now' : '') + '</div>' +
                    (facts ? '<div class="facts">' + facts + '</div>' : '') +
                    '<div class="why">' +
                        (row.IsVetoed ? 'ThemeForge would not use this: ' : Math.round(row.Score) + '/100 &mdash; ') +
                        escapeHtml(row.Reason) +
                        (row.IsInspected ? '' : ' (not looked at closely)') +
                    '</div>' +
                    '<button type="button" class="themeforge-use" data-url="' + attr(row.Url) + '">Use this</button>' +
                    '</div>';
            }).join('');
        }

        function search() {
            results.textContent = 'Searching. This takes a few seconds.';
            request('POST', 'Items/' + itemId + '/Search', { Query: queryBox.value.trim() })
                .then(function (found) {
                    if (found.Query) {
                        queryBox.value = found.Query;
                    }
                    render(found);
                }, function (error) {
                    results.textContent = error.message;
                });
        }

        overlay.querySelector('.themeforge-search').addEventListener('click', search);

        queryBox.addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                search();
            }
        });

        results.addEventListener('click', function (event) {
            var button = event.target.closest('.themeforge-use');
            if (button) {
                use(button.getAttribute('data-url'), button);
            }
        });

        save.addEventListener('click', function () {
            use(overlay.querySelector('.themeforge-url').value.trim(), null);
        });

        document.body.appendChild(overlay);
        queryBox.focus();

        // Opened to be used, so it searches at once: most of the time the answer is in the list
        // and nothing has to be typed at all.
        search();
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
