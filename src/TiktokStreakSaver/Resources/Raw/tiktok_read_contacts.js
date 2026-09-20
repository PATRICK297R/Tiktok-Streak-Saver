// ─────────────────────────────────────────────────────────────────────────────
// Contact scanner for the "Import from TikTok chats" feature.
//
// Deliberately a separate script from tiktok_automation.js. That one walks the chat list to send
// messages and mutates the page as it goes; this one only reads. Keeping them apart means a change
// to the sending flow cannot break the import, and nothing here can send a message by accident.
//
// Where it differs from tiktok_automation.js: results are accumulated into window.__saverScan and
// read back by the app, instead of calling into a JavaScript interface. Polling one JSON object is
// far less to go wrong than marshalling strings across the bridge, and it makes the scan
// inspectable from a remote debugger while it runs.
//
// The chat item selectors, the fallbacks and the "scroll by a viewport, never to the bottom" rule
// are copied from the automation script, which already had to solve exactly these problems.
// ─────────────────────────────────────────────────────────────────────────────
(function () {
    // Re-injection must not restart a scan that is already running.
    if (window.__saverScan && !window.__saverScan.done) return;

    var scan = window.__saverScan = {
        done: false,
        items: [],
        attempts: 0,
        log: [],
        url: location.href,
        ua: navigator.userAgent
    };

    // How many consecutive scrolls that revealed nothing new before giving up - only once rows have
    // been seen at all.
    var MAX_EMPTY_SCROLLS = 5;
    var MAX_SCROLLS = 40;
    var SCROLL_PAUSE_MS = 1500;

    // How long to keep waiting for the list to render. The inbox builds its rows lazily and can take
    // many seconds; treating the first empty look as "no chats" is what made the scan report an
    // empty inbox while the inbox was plainly on screen.
    var WAIT_FOR_LIST_MS = 45000;

    // A ceiling, so the scan always finishes and the app is never left polling a scan that will
    // never end.
    var HARD_DEADLINE_MS = 120000;

    var startedAt = Date.now();
    var emptyScrolls = 0;

    var log = function (msg) {
        scan.log.push(msg);
        if (scan.log.length > 60) scan.log.shift();
    };

    var text = function (el) {
        if (!el) return '';
        return (el.textContent || '').replace(/\s+/g, ' ').trim();
    };

    var findItems = function () {
        var items = document.querySelectorAll("[data-e2e*='dm-new-conversation-item']");
        if (items.length > 0) return items;

        // TikTok renames these periodically, so the same fallbacks the automation script uses.
        var fallbacks = ["[data-e2e*='chat-list-item']", "[data-e2e*='chat-item']"];
        for (var i = 0; i < fallbacks.length; i++) {
            try {
                items = document.querySelectorAll(fallbacks[i]);
                if (items.length > 0) return items;
            } catch (e) { }
        }

        return [];
    };

    var findScrollContainer = function (items) {
        if (items.length > 0) {
            var parent = items[0].parentElement;
            while (parent && parent !== document.body) {
                if (parent.scrollHeight > parent.clientHeight + 10) return parent;
                parent = parent.parentElement;
            }
        }

        var candidates = document.querySelectorAll('[class*="ChatList"], [class*="chatList"], [class*="conversation-list"]');
        for (var i = 0; i < candidates.length; i++) {
            if (candidates[i].scrollHeight > candidates[i].clientHeight + 10) return candidates[i];
        }

        return null;
    };

    // A chat item shows the name, a preview of the last message and a timestamp, all as separate
    // lines. Only the name is wanted, so name-bearing elements are tried first and the raw text is
    // a last resort with only its first line taken.
    var nameOf = function (item) {
        var preferred = item.querySelectorAll('[class*="name"], [class*="Name"], strong, h4, h3, span[dir="auto"]');
        for (var i = 0; i < preferred.length; i++) {
            var candidate = text(preferred[i]);
            // A name is short; anything longer is a message preview that a loose selector caught.
            if (candidate && candidate.length <= 64) return candidate;
        }

        var link = item.querySelector('a[href*="/@"]');
        if (link) {
            var linked = text(link);
            if (linked) return linked;
        }

        var lines = (item.innerText || '').split('\n');
        for (var j = 0; j < lines.length; j++) {
            var line = (lines[j] || '').replace(/\s+/g, ' ').trim();
            // Skip the blank rows and the timestamp row.
            if (!line || /^\d{1,2}:\d{2}/.test(line)) continue;
            if (line.length <= 64) return line;
        }

        return '';
    };

    var keyOf = function (entry) {
        return entry.isGroup
            ? 'group:' + entry.displayName.toLowerCase()
            : 'user:' + entry.username.toLowerCase();
    };

    var seen = {};

    var record = function (items) {
        var added = 0;

        for (var i = 0; i < items.length; i++) {
            var item = items[i];

            // A profile link is what makes a chat a person: a group has no @handle to link to. This
            // is the same DM-versus-group test the automation script already relies on.
            var link = item.querySelector('a[href*="/@"]');
            var username = '';
            if (link) {
                var href = link.getAttribute('href') || '';
                var match = href.match(/\/@([^\/?]+)/);
                if (match && match[1]) {
                    // A malformed percent-escape makes decodeURIComponent throw, and an exception
                    // here would kill the whole scan over one odd row.
                    try {
                        username = decodeURIComponent(match[1]);
                    } catch (e) {
                        username = match[1];
                    }
                }
            }

            var name = nameOf(item);
            var entry = {
                username: username,
                displayName: name,
                isGroup: !username
            };

            // Groups are matched by their chat header, so one with no readable name is dropped
            // rather than added as an entry that could never be sent to.
            if (entry.isGroup && !entry.displayName) continue;

            // Neither a handle nor a name means nothing usable was read from the row.
            if (!entry.username && !entry.displayName) continue;

            // A person with only a handle gets that handle as the display name.
            if (!entry.isGroup && !entry.displayName) entry.displayName = entry.username;

            var key = keyOf(entry);
            if (seen[key]) continue;

            seen[key] = true;
            scan.items.push(entry);
            added++;
        }

        return added;
    };

    var finish = function (reason) {
        scan.done = true;
        scan.url = location.href;
        log('Finished: ' + scan.items.length + ' chats (' + reason + ')');
    };

    // Reschedules the next pass, unless the scan is finished or has run out of time. Every path ends
    // through here or through finish(), so the scanner always reports a result eventually.
    var schedule = function () {
        if (scan.done) return;

        if (Date.now() - startedAt > HARD_DEADLINE_MS) {
            finish('time limit reached');
            return;
        }

        setTimeout(pass, SCROLL_PAUSE_MS);
    };

    // One pass can fail on something odd in the page. Without this the timer chain dies silently:
    // no result is ever published, and the app waits for a scan that has already stopped.
    var pass = function () {
        try {
            runPass();
        } catch (e) {
            log('Pass failed: ' + (e && e.message ? e.message : e));
            schedule();
        }
    };

    var runPass = function () {
        var before = scan.items.length;
        var visible = findItems();
        record(visible);

        var grew = scan.items.length > before;
        if (grew) emptyScrolls = 0;
        else emptyScrolls++;

        scan.attempts++;
        log('Pass ' + scan.attempts + ': ' + scan.items.length + ' chats'
            + (grew ? ' (+' + (scan.items.length - before) + ')' : ' (none new)'));

        if (scan.items.length === 0) {
            // Nothing has rendered yet, which is normal for the first few seconds while the inbox
            // builds its list. Keep waiting rather than concluding the inbox is empty.
            if (Date.now() - startedAt > WAIT_FOR_LIST_MS) {
                finish('no conversation rows appeared');
                return;
            }

            schedule();
            return;
        }

        if (emptyScrolls >= MAX_EMPTY_SCROLLS) {
            finish('end of list');
            return;
        }

        if (scan.attempts >= MAX_SCROLLS) {
            finish('step limit');
            return;
        }

        var container = findScrollContainer(visible);
        if (!container) {
            // Rows are on screen and nothing scrolls, so the whole list is already visible.
            finish('list fits on screen');
            return;
        }

        var previousTop = container.scrollTop;
        // One viewport at a time, never scrollHeight: TikTok's list recycles its DOM nodes and
        // jumping to the bottom skips conversations.
        container.scrollTop += container.clientHeight * 2;

        // A list that stopped moving while nothing new appeared is the end of it, which is the
        // normal way this finishes on a short list.
        if (container.scrollTop <= previousTop && emptyScrolls >= 2) {
            finish('list stopped scrolling');
            return;
        }

        schedule();
    };

    log('Scan started');
    // The first pass waits for the WebView to lay the page out before looking for rows.
    setTimeout(pass, 1500);
})();
