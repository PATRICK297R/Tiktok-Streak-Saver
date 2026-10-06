// Contact scanner for the "Import from TikTok chats" feature.
//
// How it identifies a chat: it opens the conversation and reads the header, which is
// exactly what tiktok_automation.js does when sending. Reading the list rows instead
// does not work. A row shows the display name and a preview of the last message in the
// same text block, so the preview gets mistaken for a name ("sent you a sticker"), and
// a direct message row carries no profile link either, so every row looked like a group.
//
// The header is authoritative, and only the header: a profile link in it means a person with a
// real username, and no link means a group, which the app matches by its chat name. Those are
// the two things the sending flow needs in order to find a chat again.
//
// Searching the rest of the page for a link when the header has none is wrong here, even though
// tiktok_automation.js does exactly that when sending: there the target's type is already known,
// while here it is the thing being decided. A group's message pane links to the members who
// wrote, so that search handed every group a member's username and imported it as a friend.
//
// Results are pushed to the app through SaverScan.report on every step, so the app
// sees progress rather than waiting blind, and a scanner that stopped can be told
// apart from one that never ran at all.
(function () {
    // A live scanner keeps its heartbeat fresh, and re-injection must not restart one. A scanner
    // that has gone quiet is treated as dead and replaced instead of blocking the re-injection:
    // returning unconditionally is what made a stalled scan impossible to revive, because the app
    // kept sending the script and the script kept ignoring it.
    var LIVE_WINDOW_MS = 10000;
    var previous = window.__saverScan;
    if (previous && !previous.done && (Date.now() - (previous.heartbeatAt || 0)) < LIVE_WINDOW_MS) return;

    var scan = window.__saverScan = {
        done: false,
        items: [],
        attempts: 0,
        log: [],
        url: location.href,
        ua: navigator.userAgent,
        heartbeatAt: Date.now(),
        listFound: false,
        diag: null
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

    // The row currently being opened, and the text it showed just before the click. Kept so the row can
    // be stamped once its chat has been read - and stamped with the text it had while it was still the
    // row that was clicked, because opening a chat can make the list move under it.
    var rowBeingRead = null;
    var rowBeingReadText = '';

    var push = function () {
        try {
            scan.url = location.href;
            scan.ua = navigator.userAgent;
            scan.heartbeatAt = Date.now();

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

    // Failures inside the page are reported rather than swallowed, and they carry the page shape
    // with them.
    //
    // A script that dies on an error leaves no trace otherwise, and the app cannot tell that apart
    // from a page that simply has no conversations in it - which is the confusion that made this
    // take several rounds to pin down. The shape is attached because an error is the moment its
    // viewport and selectors matter most.
    window.addEventListener('error', function (e) {
        try { reportPageShape('at the error'); } catch (ignore) { }
        log('page error: ' + (e && e.message ? e.message : 'unknown'));
    });

    window.addEventListener('unhandledrejection', function (e) {
        var reason = e && e.reason;
        log('page rejection: ' + (reason && reason.message ? reason.message : reason));
    });

    // Reported before anything else is touched, so that "the script is running" is established
    // immediately. If it never arrives, the problem is the injection itself and not the page.
    log('Scan started');

    var text = function (el) {
        if (!el) return '';
        return (el.textContent || '').replace(/\s+/g, ' ').trim();
    };

    // A row's own text, short enough to compare cheaply.
    var rowText = function (row) {
        var raw = (row && (row.innerText || row.textContent)) || '';
        return raw.replace(/\s+/g, ' ').trim().substring(0, 120);
    };

    // Where a row remembers the chat it was read as, and the text it showed at the time.
    var ROW_MARK = '__saverRead';

    // Whether this row is one already read, so it can be passed over without opening it again.
    //
    // The text has to still match. A list that recycles its nodes hands a row a different conversation
    // while the row keeps the mark of the old one, and trusting that mark would skip a chat that was
    // never read - the one mistake here that loses a contact. Anything that does not match is simply
    // read again, which costs one click and is covered by the dedupe.
    var rowIsRead = function (row) {
        var mark = row && row[ROW_MARK];
        if (!mark) return false;
        if (mark.text !== rowText(row)) return false;
        return !!mark.key && !!seen[mark.key];
    };

    var markRowRead = function (row, textWhenClicked, entry) {
        if (!row || !entry) return;

        try {
            row[ROW_MARK] = { key: keyOf(entry), text: textWhenClicked };
        } catch (e) {
            // A frozen node only means this row is read again; the dedupe still covers it.
        }
    };

    var getRootDoc = function () {
        try {
            var iframe = document.querySelector('iframe[src*="/messages"]') ||
                document.querySelector('iframe[data-testid*="message-index"]');
            if (iframe && iframe.contentDocument) {
                return iframe.contentDocument;
            }
        } catch (e) { }
        return document;
    };

    var getRootWin = function () {
        try {
            var iframe = document.querySelector('iframe[src*="/messages"]') ||
                document.querySelector('iframe[data-testid*="message-index"]');
            if (iframe && iframe.contentWindow) {
                return iframe.contentWindow;
            }
        } catch (e) { }
        return window;
    };

    var findItems = function () {
        var doc = getRootDoc();
        var items = doc.querySelectorAll("[data-e2e*='dm-new-conversation-item']");
        if (items.length > 0) return items;

        var fallbacks = ["[data-e2e*='chat-list-item']", "[data-e2e*='chat-item']"];
        for (var i = 0; i < fallbacks.length; i++) {
            try {
                items = doc.querySelectorAll(fallbacks[i]);
                if (items.length > 0) return items;
            } catch (e) { }
        }

        return [];
    };

    var findScrollContainer = function (items) {
        var doc = getRootDoc();
        if (items.length > 0) {
            var parent = items[0].parentElement;
            while (parent && parent !== doc.body && parent !== document.body) {
                if (parent.scrollHeight > parent.clientHeight + 10) return parent;
                parent = parent.parentElement;
            }
        }
        return null;
    };

    var findHeader = function () {
        var doc = getRootDoc();
        return doc.querySelector('[data-e2e="chat-header"]') ||
            doc.querySelector('[class*="ChatHeader"]') ||
            doc.querySelector('[class*="chatHeader"]') ||
            doc.querySelector('[class*="DivChatHeader"]');
    };

    // What the page actually contains, reported so a scan that finds nothing can say why.
    //
    // The viewport is the first thing to check: TikTok picks its layout from the viewport width, so
    // a narrow one means the mobile layout, whose conversation rows do not match these selectors.
    // Without this the only symptom is "0 found", which is the same for a wrong layout, a renamed
    // selector and a genuinely empty inbox.
    var countAll = function (selector) {
        try { return getRootDoc().querySelectorAll(selector).length; } catch (e) { return -1; }
    };

    var describePage = function () {
        var doc = getRootDoc();
        var win = getRootWin();
        var e2e = [];
        try {
            var nodes = doc.querySelectorAll('[data-e2e]');
            for (var i = 0; i < nodes.length && e2e.length < 8; i++) {
                var value = nodes[i].getAttribute('data-e2e');
                if (value && e2e.indexOf(value) === -1) e2e.push(value);
            }
        } catch (e) { }

        scan.diag = {
            viewport: win.innerWidth + 'x' + win.innerHeight,
            dpr: win.devicePixelRatio || window.devicePixelRatio,
            isIframe: doc !== document,
            dmItems: countAll("[data-e2e*='dm-new-conversation-item']"),
            chatListItems: countAll("[data-e2e*='chat-list-item']"),
            chatItems: countAll("[data-e2e*='chat-item']"),
            profileLinks: countAll('a[href*="/@"]'),
            editable: countAll('[contenteditable]'),
            e2e: e2e.join(', ')
        };

        return scan.diag;
    };

    // Reported with the moment it was taken, because the answer is only meaningful once the list has
    // had a chance to render. Taken too early it describes an empty page and reads like a page whose
    // selectors do not match - which is how a working viewport got blamed for a broken script.
    var reportPageShape = function (when) {
        var d = describePage();
        log('page shape ' + when + ': viewport=' + d.viewport + ' dpr=' + d.dpr
            + ' dmItems=' + d.dmItems + ' chatListItems=' + d.chatListItems
            + ' chatItems=' + d.chatItems + ' profileLinks=' + d.profileLinks);
        log('data-e2e ' + when + ': ' + (d.e2e || '(none)'));
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
            if (scan.attempts === 0 && !scan.diag) reportPageShape('before the list');

            if (Date.now() - startedAt > WAIT_FOR_LIST_MS) {
                reportPageShape('when giving up');
                finish('no conversation rows appeared');
                return;
            }

            schedule();
            return;
        }

        // Said once, as soon as the list is there. Without it a scan that works looks the same in the
        // log as one that never got past an empty page, because the only shape ever reported was the
        // one taken while the page was still blank.
        if (!scan.listFound) {
            scan.listFound = true;
            log('list ready: ' + items.length + ' rows');
            reportPageShape('with the list');
        }

        // Rows already read are passed over without opening them again. This is what keeps a scroll from
        // reading the same conversations a second time: after the list moves, the rows that overlap with
        // the previous screen are known, and only the ones below them are opened.
        var skipped = 0;
        while (chatIndex < items.length && rowIsRead(items[chatIndex])) {
            chatIndex++;
            skipped++;
        }

        if (skipped > 0)
            log('Skipped ' + skipped + ' rows already read');

        if (chatIndex >= items.length) {
            // The visible rows are all visited; try to reveal more.
            if (scrollForMore(items)) return;
            finish(emptyScrolls > 0 ? 'end of list' : 'list fits on screen');
            return;
        }

        var item = items[chatIndex];
        if (!item) { chatIndex++; schedule(); return; }

        rowBeingRead = item;
        rowBeingReadText = rowText(item);

        // Counted here rather than after opening, so a chat that never opens still counts as an
        // attempt and cannot loop forever.
        scan.attempts++;
        item.click();
        setTimeout(afterOpen, OPEN_WAIT_MS);
    };

    // How many times a header is read before its chat is given up on. The header fills in after the
    // click - for a moment it shows a bare "@" with no name at all - and an identity read then is
    // not an identity, which is how "@@" reached the contact list.
    var MAX_HEADER_READS = 4;

    // Runs from a timer, so it needs its own guard. step() has a try/catch, but that only covers the
    // synchronous part: an error thrown here escapes to window.onerror and leaves the timer chain
    // dead, so the scan stops after opening one chat and still looks like it is working. That is
    // exactly how a broken helper went unnoticed for four releases - the only trace was a stall.
    var afterOpen = function () {
        var indexBefore = chatIndex;

        try {
            readHeader(indexBefore, MAX_HEADER_READS);
        } catch (e) {
            missChat('Open failed', e, indexBefore);
        }
    };

    // Reads the header, looking again while it is still filling in, and treating a header that has
    // not moved on as a click that had not taken effect yet. Both are reasons to wait rather than to
    // record something: a chat read too early is a contact under the wrong name, and one read before
    // the click landed is the previous chat filed twice.
    var readHeader = function (indexBefore, remaining) {
        var entry = identifyOpenChat();
        var stale = !!entry && keyOf(entry) === lastIdentifiedKey;

        if (!entry || stale) {
            if (remaining > 0) {
                setTimeout(function () {
                    try {
                        readHeader(indexBefore, remaining - 1);
                    } catch (e) {
                        missChat('Read failed', e, indexBefore);
                    }
                }, OPEN_WAIT_MS);
                return;
            }

            // Out of looks. A header that never changed is the previous chat, so there is nothing to
            // file; one that never settled may still be a chat worth keeping.
            var fallback = stale ? null : namelessPerson();
            if (fallback) { settle(fallback); return; }

            missChat(stale ? 'Header never changed' : 'Header never settled', headerReport(), indexBefore);
            return;
        }

        settle(entry);
    };

    // A chat whose name never filled in, filed under its handle.
    //
    // Reached only after the header was read the full number of times, so a header that is merely slow
    // is never mistaken for one that is blank. Some accounts are named with runs of invisible filler,
    // and such a chat is a real chat that can be sent to: a contact under its handle is worth more than
    // one silently dropped - or, as happened on a device, one filed under eleven invisible characters.
    //
    // A group is refused here rather than guessed at, because a group is matched by its name and there
    // is no name to match; the other markers still apply, so a nameless chat with a member stack or a
    // participant count is left to be skipped instead of being filed as a person.
    var namelessPerson = function () {
        var header = findHeader();
        if (!header) return null;
        if (groupReason(header, distinctProfileLinks(header))) return null;

        var username = usernameFrom(headerProfileLink(header));
        if (!username) {
            var uniqueElem = header.querySelector('[data-e2e="chat-uniqueid"]');
            if (uniqueElem) {
                var uniqueText = (uniqueElem.textContent || '').trim();
                if (uniqueText.startsWith('@')) {
                    uniqueText = uniqueText.substring(1);
                }
                if (isUsableUsername(uniqueText)) {
                    username = uniqueText;
                }
            }
        }
        if (!isUsableUsername(username)) return null;

        return {
            username: username,
            displayName: username,
            isGroup: false,
            reason: 'named by its handle'
        };
    };

    // The header's own numbers, printed for every chat rather than only for the ones that fail.
    //
    // Whether a chat is a person or a group is decided from exactly these, so a log that shows only
    // the decision cannot be argued with - and when a marker turns out to be wrong, these are what say
    // which one to trust instead. The picture count and the class test are printed without deciding
    // anything for that reason.
    var headerFacts = function () {
        var header = findHeader();
        if (!header) return 'no header';

        return 'links=' + distinctProfileLinks(header)
            + ', avatars=' + avatarCount(header)
            + ', imgs=' + header.querySelectorAll('img').length
            + ', groupClass=' + !!header.querySelector(GROUP_CLASSES);
    };

    // The failure variant: the same numbers with the name in front, because a chat that could not be
    // identified is identified by nothing else.
    var headerReport = function () {
        var header = findHeader();
        if (!header) return 'no header';

        return 'name="' + headerName(header).substring(0, 40) + '", ' + headerFacts();
    };

    // Abandons the chat that could not be read and carries on with the next one. The index only
    // moves when settle() did not already move it, so a failure cannot skip a chat or, worse, leave
    // the walk stuck on the same one until the visit limit runs out.
    var missChat = function (label, detail, indexBefore) {
        consecutiveMisses++;
        if (chatIndex === indexBefore) chatIndex++;
        log(label + ': ' + (detail && detail.message ? detail.message : detail));
        schedule();
    };

    var lastIdentifiedKey = null;

    var settle = function (entry) {
        var outcome = record(entry);

        // Stamped for a chat already known as well as a new one: either way this row has been read, and
        // opening it again after a scroll would be wasted work.
        if (outcome === 'new' || outcome === 'duplicate')
            markRowRead(rowBeingRead, rowBeingReadText, entry);

        rowBeingRead = null;
        rowBeingReadText = '';

        if (outcome === 'new') {
            lastIdentifiedKey = keyOf(entry);
            consecutiveMisses = 0;
            emptyScrolls = 0;
            // The header's own numbers are printed for every chat, not only the ones that fail. What
            // decides a person from a group is in there, so a log that shows only the decision cannot
            // be argued with - and if a marker is wrong, these say which one to trust instead.
            //
            // The reason is printed whenever there is one, for a person as well as a group: a chat filed
            // under its handle is unusual enough that the log should say so.
            log('Chat ' + scan.attempts + ': ' + (entry.isGroup
                    ? 'group "' + entry.displayName + '"'
                    : '@' + entry.username + ' "' + entry.displayName + '"')
                + (entry.reason ? ' (' + entry.reason + ')' : '')
                + ' [' + headerFacts() + ']');
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
        // One viewport at a time, never scrollHeight: jumping to the bottom skips conversations.
        container.scrollTop += container.clientHeight * 2;

        if (container.scrollTop <= before) return false;

        setTimeout(function () {
            var refreshed = findItems();

            // Back to the top of what is rendered, and let the walk pass over the rows it has already
            // read. Where reading resumes is therefore measured rather than guessed: the previous
            // screen's rows carry their marks, so the first row without one is the next unread chat -
            // whether the list kept its rows and grew, or moved a whole screen's worth.
            //
            // The guess that used to be here ("ten rows back from the end") was wrong in the direction
            // that costs: it went further back than the overlap, so a scroll re-opened conversations
            // that had already been read.
            chatIndex = 0;
            log('Scrolled; ' + refreshed.length + ' rows in view');
            schedule();
        }, 1500);

        return true;
    };


    // --- Identifying the open chat ---------------------------------------------
    //
    // These must stay inside this function. They close over findHeader, scan and log, none of
    // which exist at the top level, so written after the closing brace below every call throws a
    // ReferenceError instead. The first call happens in a timer callback, so the scan stopped
    // after opening a single chat while still looking alive - which is what shipped from 2.1.14
    // through 2.1.18, and why the import never recorded a single chat.

    // A chat is a group when its header has no single account to link to.
    //
    // This is where the rule started, and this is where it has come back to. It was set aside for a
    // while in favour of a handle that spells out the chat's name, because two rows in a device log
    // read as groups on that basis: "Humas SMKN 1 TROWULAN" under "humas.smkn.1.trow", and
    // "FIVE MULTIPLE|SEASON 6" under "five.multiple". They are ordinary accounts whose handles simply
    // read that way, and their owner has no group chats at all. So the sample that appeared to
    // disprove this rule was not a sample of groups, and the rule was never actually tested against
    // one - the handle rule was fitted to two people.
    //
    // That mistake cost something, which is why it is written down here: the handle rule classified
    // both of those accounts as groups, and it can only ever fire on a chat that links to exactly one
    // account - the shape this rule calls a person. It was a false positive generator with nothing
    // behind it.
    //
    // What is left is the one signal that comes from how the page is built rather than from a guess: a
    // direct message is a chat with one account, so its header links to that account, while a group
    // has no single account to link to. The other markers stay because they are things only a group
    // can show - several distinct links (a member stack), and a participant count such as "5 members".
    //
    // A class name mentioning group or member is reported by headerFacts but decides nothing, and so
    // is the number of pictures in the header, which is the marker detect_groups.js used. Neither has
    // been seen on a real group header yet, and both can only be trusted once the log says so: a class
    // like "member-avatar" on a direct message would turn every friend into a group at once.
    //
    // The remaining risk is stated plainly, because it is real: a group whose header does link to one
    // account would be read as a person, and the app would send to that account's chat rather than to
    // the group. Confirming which way that goes needs a group header in a log, and there has not been
    // one yet. Until then the import list is what corrects it - a row can be switched to a group by
    // hand, which is a correction, where the handle rule produced two wrong rows per twenty-four on
    // the only real inbox it was tried against.
    var GROUP_CLASSES = '[class*="group"], [class*="Group"], [class*="member"], [class*="Member"]';
    var MEMBER_COUNT = /(\d+)\s*(members|member|anggota|peserta|participants|participant|orang)/i;

    var headerProfileLink = function (header) {
        return header.querySelector('a[href*="/@"]');
    };

    // How many different accounts the header links to. One is a direct message; more than one is a
    // member stack, which only a group has.
    var distinctProfileLinks = function (header) {
        var links = header.querySelectorAll('a[href*="/@"]');
        var seenHrefs = {};
        var count = 0;

        for (var i = 0; i < links.length; i++) {
            var href = links[i].getAttribute('href') || '';
            if (!href || seenHrefs[href]) continue;
            seenHrefs[href] = true;
            count++;
        }

        return count;
    };

    // How many different pictures the header shows, counted by source so the same one twice is still
    // one. A direct message shows the person's, while a group shows a stack of its members - which is
    // the marker detect_groups.js used. Reported only: nothing has shown yet what a direct message's
    // header holds, and a badge or an icon counted here would turn friends into groups.
    var avatarCount = function (header) {
        var imgs = header.querySelectorAll('img');
        var seenSrc = {};
        var count = 0;

        for (var i = 0; i < imgs.length; i++) {
            var src = imgs[i].getAttribute('src') || imgs[i].getAttribute('data-src') || '';
            if (!src || seenSrc[src]) continue;
            seenSrc[src] = true;
            count++;
        }

        return count;
    };

    var usernameFrom = function (link) {
        if (!link) return '';

        var href = link.getAttribute('href') || '';
        var match = href.match(/\/@([^\/?]+)/);
        if (!match || !match[1]) return '';

        try {
            return decodeURIComponent(match[1]).trim();
        } catch (e) {
            return match[1].trim();
        }
    };

    // Characters that carry no name. The header renders a bare "@" before the account loads, and some
    // accounts are named with runs of invisible filler - which would import as blank contacts.
    //
    // The filler TikTok uses is U+FFF4, and reading it as U+FFFC is what let a contact named with
    // eleven invisible characters through: the two differ by one byte, and the log showed the name as
    // nothing either way. The whole blank set is listed rather than the one that was noticed.
    //
    // Every comment in this file has to sit on a line of its own. The script is flattened onto one line
    // before it is injected, so a comment trailing a statement would swallow the rest of the file -
    // which is what the "no //" check in ScriptPreparationTests exists to catch.
    var isBlankChar = function (ch) {
        var code = ch.charCodeAt(0);

        if (code <= 0x20) return true;
        if (code === 0x40) return true;
        if (code === 0x00a0 || code === 0x00ad) return true;
        if (code >= 0x115f && code <= 0x1160) return true;
        if (code >= 0x17b4 && code <= 0x17b5) return true;
        if (code === 0x180e) return true;
        if (code >= 0x2000 && code <= 0x200f) return true;
        if (code >= 0x2028 && code <= 0x202f) return true;
        if (code === 0x205f) return true;
        if (code >= 0x2060 && code <= 0x2064) return true;
        if (code === 0x2800) return true;
        if (code === 0x3000) return true;
        if (code === 0x3164) return true;
        if (code === 0xfeff) return true;
        if (code === 0xffa0) return true;
        if (code >= 0xfff0 && code <= 0xfffd) return true;

        return false;
    };

    var isUsableName = function (name) {
        if (!name) return false;

        for (var i = 0; i < name.length; i++) {
            if (!isBlankChar(name.charAt(i))) return true;
        }

        return false;
    };

    // A handle has no space, no "@" and no slash in it. The sending flow rebuilds the chat's address
    // from this value, so anything else could never find the chat again.
    var isUsableUsername = function (username) {
        return !!username && !/[\s@\/]/.test(username);
    };

    // The header's own text: the person's display name, or the group's chat name.
    var headerName = function (header) {
        var lines = (header.innerText || '').split('\n');
        for (var i = 0; i < lines.length; i++) {
            var line = (lines[i] || '').replace(/\s+/g, ' ').trim();
            if (line) return line;
        }

        return '';
    };

    // Why this header looks like a group, or empty when it looks like a person.
    var groupReason = function (header, linkCount) {
        if (header && header.querySelector && header.querySelector('[data-e2e="chat-uniqueid"]')) return '';
        if (linkCount === 0) return 'no profile link';
        if (linkCount > 1) return 'member stack';
        if (MEMBER_COUNT.test(text(header))) return 'participant count';
        return '';
    };

    // Identifies the chat that is currently open. Null means the header has not finished filling in,
    // which is a reason to look again rather than to record something.
    var identifyOpenChat = function () {
        var header = findHeader();
        if (!header) return null;

        var name = headerName(header);

        // The name is checked first because it is what the chat is filed under. While the header is
        // still filling in it is a bare "@" and nothing else, and that is what reached the list as a
        // contact called "@".
        if (!isUsableName(name)) return null;

        // The header's own link is read whatever the chat turns out to be, because a person is filed
        // under it - and because it is the count of these that says whether there is one account to
        // link to, which is what separates a direct message from a group.
        var username = usernameFrom(headerProfileLink(header));
        if (!username) {
            var uniqueElem = header.querySelector('[data-e2e="chat-uniqueid"]');
            if (uniqueElem) {
                var uniqueText = (uniqueElem.textContent || '').trim();
                if (uniqueText.startsWith('@')) {
                    uniqueText = uniqueText.substring(1);
                }
                if (isUsableUsername(uniqueText)) {
                    username = uniqueText;
                }
            }
        }
        var reason = groupReason(header, distinctProfileLinks(header));
        if (reason) return { username: '', displayName: name, isGroup: true, reason: reason };

        // No group marker, so the header's link has to name the person.
        if (!isUsableUsername(username)) return null;

        return { username: username, displayName: name, isGroup: false, reason: '' };
    };

    var keyOf = function (entry) {
        return entry.isGroup
            ? 'group:' + entry.displayName.toLowerCase()
            : 'user:' + entry.username.toLowerCase();
    };

    var finish = function (reason) {
        scan.done = true;
        log('Finished: ' + scan.items.length + ' chats (opened ' + scan.attempts + ', ' + reason + ')');
    };

    // The first step waits for the WebView to lay the page out before looking for rows.
    setTimeout(step, 1500);
})();
