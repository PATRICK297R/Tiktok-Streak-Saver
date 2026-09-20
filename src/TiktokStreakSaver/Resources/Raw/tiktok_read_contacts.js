// Contact scanner for the "Import from TikTok chats" feature.
//
// How it identifies a chat: it opens the conversation and reads the header, which is
// exactly what tiktok_automation.js does when sending. Reading the list rows instead
// does not work. A row shows the display name and a preview of the last message in the
// same text block, so the preview gets mistaken for a name ("sent you a sticker"), and
// a direct message row carries no profile link either, so every row looked like a group.
//
// The header is authoritative: a profile link in it means a person with a real
// username, and no profile link means a group, which the app matches by its chat name.
// Those are the two things the sending flow needs in order to find a chat again.
//
// Results are pushed to the app through SaverScan.report on every step, so the app
// sees progress rather than waiting blind, and a scanner that stopped can be told
// apart from one that never ran at all.
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

    // How long to wait after opening a chat for its header to show the new one.
    var OPEN_WAIT_MS = 900;

    // A chat can fail to open; this many in a row means the walk is stuck.
    var MAX_CONSECUTIVE_MISSES = 6;

    // Ceiling on chats visited, so a huge inbox cannot run forever. Each chat costs about two
    // seconds, so this is kept under what the deadline below allows.
    var MAX_CHATS = 110;

    // A ceiling on the whole scan, so the app is never left waiting.
    var HARD_DEADLINE_MS = 300000;

    // How long to keep waiting for the list to render at the start.
    var WAIT_FOR_LIST_MS = 45000;

    var startedAt = Date.now();
    var chatIndex = 0;
    var consecutiveMisses = 0;
    var emptyScrolls = 0;
    var seen = {};

    var push = function () {
        try {
            scan.url = location.href;
            scan.ua = navigator.userAgent;

            if (typeof SaverScan !== 'undefined' && SaverScan && SaverScan.report) {
                SaverScan.report(JSON.stringify(scan));
            }
        } catch (e) {
            // A page that is mid-navigation can reject the call; the next step tries again.
        }
    };

    var log = function (msg) {
        scan.log.push(msg);
        if (scan.log.length > 60) scan.log.shift();
        push();
    };

    // Reported before anything else is touched, so that "the script is running" is established
    // immediately. If it never arrives, the problem is the injection itself and not the page.
    log('Scan started');

    var text = function (el) {
        if (!el) return '';
        return (el.textContent || '').replace(/\s+/g, ' ').trim();
    };

    var findItems = function () {
        var items = document.querySelectorAll("[data-e2e*='dm-new-conversation-item']");
        if (items.length > 0) return items;

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
        return null;
    };

    var findHeader = function () {
        return document.querySelector('[data-e2e="chat-header"]') ||
            document.querySelector('[class*="ChatHeader"]') ||
            document.querySelector('[class*="chatHeader"]') ||
            document.querySelector('[class*="DivChatHeader"]');
    };


    // Records a chat if it is new. Returns 'new', 'duplicate' or 'unreadable' so the caller can
    // tell a chat it has already seen apart from one it could not open - only the latter is a
    // reason to give up.
    var record = function (entry) {
        if (!entry) return 'unreadable';

        var key = keyOf(entry);
        if (seen[key]) return 'duplicate';

        seen[key] = true;
        scan.items.push(entry);
        return 'new';
    };

    // Keeps the app informed while nothing else is happening.
    //
    // Without this the waiting phase is completely silent: no report is sent, so the app cannot
    // tell a scanner that is waiting for the list apart from one that has stopped running, and it
    // sits on a stale status forever. Throttled, because the wait loop runs many times a second.
    var HEARTBEAT_MS = 4000;
    var lastHeartbeatAt = 0;

    var heartbeat = function () {
        var now = Date.now();
        if (now - lastHeartbeatAt < HEARTBEAT_MS) return;

        lastHeartbeatAt = now;
        log('Working: ' + scan.attempts + ' opened, ' + scan.items.length + ' found, '
            + Math.round((now - startedAt) / 1000) + 's elapsed');
    };

    var schedule = function () {
        if (scan.done) return;

        if (Date.now() - startedAt > HARD_DEADLINE_MS) { finish('time limit reached'); return; }
        if (scan.attempts >= MAX_CHATS) { finish('visit limit reached'); return; }
        if (consecutiveMisses >= MAX_CONSECUTIVE_MISSES) { finish('several chats could not be opened'); return; }

        heartbeat();
        setTimeout(step, 120);
    };

    var step = function () {
        try {
            runStep();
        } catch (e) {
            log('Step failed: ' + (e && e.message ? e.message : e));
            schedule();
        }
    };

    var runStep = function () {
        var items = findItems();

        if (items.length === 0) {
            // Nothing rendered yet. The inbox builds its list lazily, so this is normal at first
            // and must not be read as an empty inbox.
            if (Date.now() - startedAt > WAIT_FOR_LIST_MS) { finish('no conversation rows appeared'); return; }
            schedule();
            return;
        }

        if (chatIndex >= items.length) {
            // The visible rows are all visited; try to reveal more.
            if (scrollForMore(items)) return;
            finish(emptyScrolls > 0 ? 'end of list' : 'list fits on screen');
            return;
        }

        var item = items[chatIndex];
        if (!item) { chatIndex++; schedule(); return; }

        // Counted here rather than after opening, so a chat that never opens still counts as an
        // attempt and cannot loop forever.
        scan.attempts++;
        item.click();
        setTimeout(afterOpen, OPEN_WAIT_MS);
    };

    var afterOpen = function () {
        var entry = identifyOpenChat();

        // A header that still shows the previous chat means the click had not taken effect yet.
        // One retry is enough; a second failure is treated as an unreadable chat.
        if (entry && keyOf(entry) === lastIdentifiedKey) {
            setTimeout(function () {
                var retry = identifyOpenChat();
                settle(retry && keyOf(retry) === lastIdentifiedKey ? null : retry);
            }, OPEN_WAIT_MS);
            return;
        }

        settle(entry);
    };

    var lastIdentifiedKey = null;

    var settle = function (entry) {
        var outcome = record(entry);

        if (outcome === 'new') {
            lastIdentifiedKey = keyOf(entry);
            consecutiveMisses = 0;
            emptyScrolls = 0;
            log('Chat ' + scan.attempts + ': ' + (entry.isGroup ? 'group "' : '@') + entry.displayName
                + (entry.isGroup ? '"' : '') + ' (' + scan.items.length + ' total)');
        } else if (outcome === 'duplicate') {
            // Reached again through a scroll overlap, which is expected and not a failure.
            log('Chat ' + scan.attempts + ': already seen');
        } else {
            consecutiveMisses++;
            log('Chat ' + scan.attempts + ': could not be identified');
        }

        chatIndex++;
        schedule();
    };

    // Reveals the next screen of chats. Returns true when it has arranged for the walk to continue,
    // and false when the list has nothing more to show.
    var scrollForMore = function (items) {
        var container = findScrollContainer(items);
        if (!container) return false;

        emptyScrolls++;
        if (emptyScrolls > 6) return false;

        var before = container.scrollTop;
        var previousCount = items.length;
        // One viewport at a time, never scrollHeight: TikTok's list recycles its DOM nodes and
        // jumping to the bottom skips conversations.
        container.scrollTop += container.clientHeight * 2;

        if (container.scrollTop <= before) return false;

        setTimeout(function () {
            var refreshed = findItems();
            // Resume a little before the end of what was already visible, because the recycled list
            // overlaps between screens.
            var resumeFrom = Math.max(0, previousCount - 10);
            chatIndex = Math.min(resumeFrom, Math.max(0, refreshed.length - 1));
            log('Scrolled; continuing from chat ' + (chatIndex + 1) + ' of ' + refreshed.length);
            schedule();
        }, 1500);

        return true;
    };

    // The first step waits for the WebView to lay the page out before looking for rows.
    setTimeout(step, 1500);
})();

    // The username of the open chat, or empty when the header has no profile link - which is
    // what a group looks like. Mirrors tiktok_automation.js, including its last-resort search,
    // because that is the logic that already finds chats correctly when sending.
    var headerUsername = function () {
        var header = findHeader();
        if (!header) return '';

        var link = header.querySelector('a[href*="/@"]');
        if (link) {
            var href = link.getAttribute('href') || '';
            var match = href.match(/\/@([^\/?]+)/);
            if (match && match[1]) {
                try {
                    return decodeURIComponent(match[1]).trim();
                } catch (e) {
                    return match[1].trim();
                }
            }
        }

        // Last resort: a profile link in the conversation pane but not in the nav or the list.
        var links = document.querySelectorAll('a[href*="/@"]');
        for (var i = 0; i < links.length; i++) {
            var candidate = links[i];
            if (candidate.closest("[data-e2e*='conversation-item']") ||
                candidate.closest("[data-e2e*='chat-list']") ||
                candidate.closest("[data-e2e*='dm-new-conversation']")) continue;
            if (candidate.closest("[data-e2e*='nav-']") ||
                candidate.closest('[data-e2e="profile-icon"]') ||
                candidate.closest('nav')) continue;

            var other = candidate.getAttribute('href') || '';
            var otherMatch = other.match(/\/@([^\/?]+)/);
            if (otherMatch && otherMatch[1]) {
                try {
                    return decodeURIComponent(otherMatch[1]).trim();
                } catch (e) {
                    return otherMatch[1].trim();
                }
            }
        }

        return '';
    };

    // The header's own text: the person's display name, or the group's chat name.
    var headerName = function () {
        var header = findHeader();
        if (!header) return '';

        var lines = (header.innerText || '').split('\n');
        for (var i = 0; i < lines.length; i++) {
            var line = (lines[i] || '').replace(/\s+/g, ' ').trim();
            if (line) return line;
        }

        return '';
    };

    // Identifies the chat that is currently open. Null means the header is not readable yet.
    var identifyOpenChat = function () {
        var username = headerUsername();
        if (username) {
            var name = headerName();
            return { username: username, displayName: name || username, isGroup: false };
        }

        // No profile link in the header, so this is a group, which the app matches by name.
        var groupName = headerName();
        if (!groupName) return null;

        return { username: '', displayName: groupName, isGroup: true };
    };

    var keyOf = function (entry) {
        return entry.isGroup
            ? 'group:' + entry.displayName.toLowerCase()
            : 'user:' + entry.username.toLowerCase();
    };

    var finish = function (reason) {
        scan.done = true;
        log('Finished: ' + scan.items.length + ' chats (' + reason + ')');
    };
