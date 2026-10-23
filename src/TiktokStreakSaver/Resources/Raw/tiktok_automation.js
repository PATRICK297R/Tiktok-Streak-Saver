// TikTok Message Automation Script
// This script is injected into the TikTok WebView to automate messaging

(function () {
    var userName = '[UserName]';
    var message = '[Message]';
    var isGroup = '[IsGroup]' === 'true';
    var dryRun = '[DryRun]' === 'true';
    var found = false;
    var chatIndex = 0;
    var chatItems = [];
    var maxScrollAttempts = 50;
    var scrollAttempts = 0;
    var checkedUsernames = {};
    var newItemsFound = 0;

    var globalWatchdog = setTimeout(function () {
        if (!found) {
            log('[WATCHDOG] Global timeout reached (60s)');
            reportError('Automation timed out after 60s');
        }
    }, 60000);

    var clearWatchdog = function () {
        if (globalWatchdog) {
            clearTimeout(globalWatchdog);
            globalWatchdog = null;
        }
    };

    var log = function (msg) {
        if (typeof StreakApp == 'undefined') {
            console.log(msg);
            return;
        }
        StreakApp.log(msg);
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

    // ── Business Suite detection ─────────────────────────────────────────────
    //
    // TikTok Business Suite serves the inbox in an iframe whose composer is a Draft.js editor that
    // rejects the DOM-level typing the classic inbox accepts. Everything below this marker is used
    // only when that inbox is detected; the classic path is left exactly as it was, because it is
    // known to work and is what most accounts still get.
    //
    // How the two differ, measured on the live page:
    //   - Writing to the composer with execCommand leaves the Draft.js state empty. The text is in
    //     the DOM but the editor does not know about it, so no send button ever appears and the
    //     component throws while reconciling, replacing the chat panel with "Something went wrong".
    //     The state has to be set through the editor instance instead.
    //   - The send control is an <svg role="button" data-e2e="dm-new-send-btn"> that only exists once
    //     the composer holds text, and it has no click() method.
    //   - The page also contains a button[type="submit"], but it belongs to the search box. Clicking
    //     it as a fallback runs a search instead of sending, so it must never be used here.

    var isBusinessInbox = function () {
        try {
            if (window.location.href.indexOf('business-suite') !== -1) return true;
            // The inbox frame itself carries scene=business even when the outer URL does not.
            if (/[?&]scene=business(&|$)/.test(window.location.href)) return true;
            // The classic inbox renders in the page itself. Business Suite renders it inside a frame,
            // so the presence of that frame is what tells the two apart when the URL is ambiguous.
            return !!document.querySelector('iframe[data-testid*="message-index"]');
        } catch (e) {
            return false;
        }
    };

    var business = isBusinessInbox();

    // Whether the chat panel has been replaced by TikTok's error state.
    //
    // When the composer is written to the wrong way, TikTok unmounts the whole chat panel and puts
    // "Something went wrong" in its place - the chat box element itself is gone, which is why this
    // checks both: the error text alone could be an unrelated banner, and the missing panel alone is
    // also true before any chat has been opened.
    var chatPanelCrashed = function () {
        var doc = getRootDoc();
        var body = (doc.body && doc.body.innerText) || '';

        if (body.indexOf('Something went wrong') === -1) return false;

        return !doc.querySelector('[data-e2e="dm-new-chatbox"]');
    };

    // ── Environment & Diagnostics ────────────────────────────────────────────

    var dumpEnvironment = function () {
        var doc = getRootDoc();
        var win = getRootWin();
        log('=== FULL ENVIRONMENT DUMP ===');
        log('[ENV] User Agent: ' + navigator.userAgent);
        log('[ENV] Platform: ' + navigator.platform);
        log('[ENV] Page URL: ' + window.location.href);
        log('[ENV] Page Title: ' + document.title);
        log('[ENV] Document Ready: ' + document.readyState);
        log('[ENV] Is Iframe: ' + (doc !== document ? 'YES' : 'NO'));
        if (doc !== document) {
            log('[ENV] Iframe Ready: ' + doc.readyState);
        }
        log('[ENV] Total DOM elements: ' + doc.querySelectorAll('*').length);
        log('[ENV] Viewport: ' + win.innerWidth + 'x' + win.innerHeight);
        log('[ENV] Screen: ' + screen.width + 'x' + screen.height);

        log('[DOM] <html>: ' + (doc.documentElement ? 'YES' : 'NO'));
        log('[DOM] <body>: ' + (doc.body ? 'YES' : 'NO'));
        log('[DOM] Elements with id: ' + doc.querySelectorAll('[id]').length);
        log('[DOM] Elements with class: ' + doc.querySelectorAll('[class]').length);
        log('[DOM] Elements with data-e2e: ' + doc.querySelectorAll('[data-e2e]').length);

        // TikTok-specific structure checks
        log('[TIKTOK] #app: ' + (doc.getElementById('app') ? 'YES' : 'NO'));
        log('[TIKTOK] [id*="app"]: ' + doc.querySelectorAll('[id*="app"]').length);
        log('[TIKTOK] [class*="messages"]: ' + doc.querySelectorAll('[class*="messages"]').length);
        log('[TIKTOK] [class*="Messages"]: ' + doc.querySelectorAll('[class*="Messages"]').length);
        log('[TIKTOK] [class*="chat"]: ' + doc.querySelectorAll('[class*="chat"]').length);
        log('[TIKTOK] [class*="Chat"]: ' + doc.querySelectorAll('[class*="Chat"]').length);
        log('[TIKTOK] [class*="conversation"]: ' + doc.querySelectorAll('[class*="conversation"]').length);
        log('[TIKTOK] [class*="Conversation"]: ' + doc.querySelectorAll('[class*="Conversation"]').length);
        log('[TIKTOK] [contenteditable]: ' + doc.querySelectorAll('[contenteditable]').length);

        // Framework detection
        log('[FRAMEWORK] window.React: ' + (typeof win.React !== 'undefined' || typeof window.React !== 'undefined' ? 'YES' : 'NO'));
        log('[FRAMEWORK] __REACT_DEVTOOLS_GLOBAL_HOOK__: ' + (typeof win.__REACT_DEVTOOLS_GLOBAL_HOOK__ !== 'undefined' || typeof window.__REACT_DEVTOOLS_GLOBAL_HOOK__ !== 'undefined' ? 'YES' : 'NO'));
        log('[FRAMEWORK] __NEXT_DATA__: ' + (typeof win.__NEXT_DATA__ !== 'undefined' || typeof window.__NEXT_DATA__ !== 'undefined' ? 'YES' : 'NO'));

        log('=== END ENVIRONMENT DUMP ===');
    };

    var dumpDataE2eValues = function () {
        var doc = getRootDoc();
        var allE2e = doc.querySelectorAll('[data-e2e]');
        var uniqueVals = {};
        for (var i = 0; i < allE2e.length; i++) {
            var val = allE2e[i].getAttribute('data-e2e');
            if (val) uniqueVals[val] = true;
        }
        var keys = Object.keys(uniqueVals);
        log('[DATA-E2E] Total elements: ' + allE2e.length + ', Unique values: ' + keys.length);

        var chunk = [];
        for (var j = 0; j < keys.length; j++) {
            chunk.push(keys[j]);
            if (chunk.length >= 15 || j === keys.length - 1) {
                log('[DATA-E2E] ' + chunk.join(', '));
                chunk = [];
            }
        }
    };

    var searchUsernameInDom = function () {
        var doc = getRootDoc();
        log('[USERNAME-SEARCH] Hunting for: ' + userName);
        var xpath = "//*[contains(text(), '" + userName + "')]";
        var result = doc.evaluate(xpath, doc, null, XPathResult.ANY_TYPE, null);
        var node = result.iterateNext();
        var foundNodes = 0;

        while (node && foundNodes < 5) {
            foundNodes++;
            var current = node;
            var path = [];
            var foundE2e = null;

            for (var k = 0; k < 8 && current && current !== doc.body; k++) {
                path.unshift(current.tagName);
                if (current.hasAttribute && current.hasAttribute('data-e2e')) {
                    foundE2e = current.getAttribute('data-e2e');
                    break;
                }
                current = current.parentNode;
            }

            if (foundE2e) {
                log('[USERNAME-SEARCH] Found inside data-e2e="' + foundE2e + '" (tags: ' + path.join('>') + ')');
            } else {
                log('[USERNAME-SEARCH] Found text, no data-e2e parent. Tags: ' + path.join('>'));
            }

            node = result.iterateNext();
        }

        if (foundNodes === 0) {
            log('[USERNAME-SEARCH] CRITICAL: "' + userName + '" not found anywhere in DOM!');
        }
    };

    var dumpFullDiagnostics = function () {
        dumpEnvironment();
        log('=== PAGE DIAGNOSTICS START ===');
        dumpDataE2eValues();
        searchUsernameInDom();

        // Log visible body text length to detect empty/blocked pages
        var doc = getRootDoc();
        var bodyText = (doc.body && doc.body.innerText) || (document.body && document.body.innerText) || '';
        log('[PAGE] Body text length: ' + bodyText.length + ' chars');
        if (bodyText.length < 200) {
            log('[PAGE] Body text (short page): ' + bodyText.substring(0, 200));
        }

        // Check for common TikTok error/block indicators
        var errorIndicators = ['captcha', 'verify', 'blocked', 'suspended', 'error', 'login'];
        for (var i = 0; i < errorIndicators.length; i++) {
            if (bodyText.toLowerCase().indexOf(errorIndicators[i]) !== -1) {
                log('[PAGE] WARNING: Body contains "' + errorIndicators[i] + '"');
            }
        }

        log('=== PAGE DIAGNOSTICS END ===');
    };

    // ── Chat Item Discovery ──────────────────────────────────────────────────

    var findChatItems = function () {
        var doc = getRootDoc();
        // Primary: current TikTok DM selectors. TikTok renames these periodically, so the current
        // and the immediately preceding spellings are tried together rather than one at a time.
        var items = doc.querySelectorAll(
            "[data-e2e*='dm-new-conversation-item'], [data-e2e*='conversation-item'], [data-e2e*='conversation']");
        if (items.length > 0) {
            log('[FIND] Found ' + items.length + ' items via: conversation selectors');
            return items;
        }

        // Fallback selectors — TikTok periodically renames data-e2e values.
        // These stay specific on purpose: a broad class or role match clicks the wrong row.
        var fallbacks = [
            "a[href*='/messages/']",
            "[data-e2e*='chat-list-item']",
            "[data-e2e*='chat-item']",
            "[class*='ConversationItem']",
            "[class*='conversation-item']"
        ];
        for (var i = 0; i < fallbacks.length; i++) {
            try {
                items = doc.querySelectorAll(fallbacks[i]);
                if (items.length > 0) {
                    log('[FIND] Found ' + items.length + ' items via fallback: ' + fallbacks[i]);
                    return items;
                }
            } catch (e) { }
        }

        log('[FIND] WARNING: No chat items found with any selector');
        return [];
    };

    var findChatListContainer = function () {
        var doc = getRootDoc();
        if (chatItems.length > 0) {
            var parent = chatItems[0].parentElement;
            while (parent && parent !== doc.body && parent !== document.body) {
                if (parent.scrollHeight > parent.clientHeight + 10) {
                    return parent;
                }
                parent = parent.parentElement;
            }
        }
        var candidates = doc.querySelectorAll('[class*="ChatList"], [class*="chatList"], [class*="conversation-list"]');
        for (var i = 0; i < candidates.length; i++) {
            if (candidates[i].scrollHeight > candidates[i].clientHeight + 10) {
                return candidates[i];
            }
        }
        return null;
    };

    // ── Scroll & Retry ───────────────────────────────────────────────────────

    var scrollAndRetry = function () {
        scrollAttempts++;
        if (scrollAttempts > maxScrollAttempts) {
            log('[SCROLL] Max scroll attempts reached (' + maxScrollAttempts + ')');
            reportError('User not found in chat list');
            return;
        }
        var container = findChatListContainer();
        if (!container) {
            log('[SCROLL] No scrollable chat container found');
            reportError('User not found in chat list');
            return;
        }
        var prevScrollTop = container.scrollTop;
        var prevCount = chatItems.length;
        // Scroll by one viewport height, not to the bottom — TikTok's virtualized
        // list recycles DOM nodes and jumping to scrollHeight skips conversations.
        container.scrollTop += container.clientHeight * 3;
        log('[SCROLL] Scrolling chat list (attempt ' + scrollAttempts + '/' + maxScrollAttempts + ', scrolling by ' + (container.clientHeight * 3) + 'px)...');
        setTimeout(function () {
            chatItems = findChatItems();
            log('[SCROLL] After scroll: ' + chatItems.length + ' items, scrollTop: ' + container.scrollTop + ' (was ' + prevScrollTop + ')');

            if (container.scrollTop <= prevScrollTop) {
                log('[SCROLL] Scroll did not move — end of list');
                reportError('User not found in chat list');
                return;
            }

            // After scrolling, clicking items near the top scrolls the sidebar
            // back up, undoing our scroll. Only check items from the lower portion
            // of the list where new content from the scroll appears.
            // Use max(0, prevCount - 5) to catch a few overlapping items for safety.
            var startFrom = Math.max(0, prevCount - 15);
            newItemsFound = 0;
            log('[SCROLL] Scanning from item ' + (startFrom + 1) + '/' + chatItems.length + ' (known: ' + Object.keys(checkedUsernames).length + ' users)');
            chatIndex = startFrom;
            checkNextChat();
        }, 2000);
    };

    // ── Username Detection ───────────────────────────────────────────────────

    var findCurrentChatUsername = function () {
        var doc = getRootDoc();
        var chatHeader = doc.querySelector('[data-e2e="chat-header"]') ||
            doc.querySelector('[class*="ChatHeader"]') ||
            doc.querySelector('[class*="chatHeader"]') ||
            doc.querySelector('[class*="DivChatHeader"]');

        if (chatHeader) {
            var uniqueIdElem = chatHeader.querySelector('[data-e2e="chat-uniqueid"]');
            if (uniqueIdElem) {
                var uniqueText = (uniqueIdElem.textContent || '').trim();
                if (uniqueText.startsWith('@')) {
                    uniqueText = uniqueText.substring(1);
                }
                if (uniqueText) {
                    return uniqueText;
                }
            }

            var headerLink = chatHeader.querySelector('a[href*="/@"]');
            if (headerLink) {
                var href = headerLink.getAttribute('href') || '';
                var match = href.match(/\/@([^\/\?]+)/);
                return match ? match[1] : '';
            }
            log('[HEADER] ChatHeader found but no a[href*="/@"] link inside it');
        } else {
            log('[HEADER] No ChatHeader element found on page');
        }

        // Fallback: any profile link in the chat panel area
        var links = doc.querySelectorAll('[class*="StyledLink"]');
        for (var i = 0; i < links.length; i++) {
            var link = links[i];
            var parent = link.closest('[data-e2e]');
            var parentAttr = parent ? parent.getAttribute('data-e2e') : '';

            if (!parentAttr || parentAttr === 'chat-header') {
                var href = link.getAttribute('href') || '';
                var match = href.match(/\/@([^\/\?]+)/);
                if (match && match[1]) {
                    return match[1];
                }
            }
        }

        // Last resort: any a[href*="/@"] in the chat panel (right side), not in nav or chat list
        var allProfileLinks = doc.querySelectorAll('a[href*="/@"]');
        for (var j = 0; j < allProfileLinks.length; j++) {
            var pLink = allProfileLinks[j];
            // Skip links inside the chat list sidebar
            if (pLink.closest('[data-e2e*="conversation-item"]') || pLink.closest('[data-e2e*="chat-list"]') || pLink.closest('[data-e2e*="dm-new-conversation"]')) continue;
            // Skip links in the nav bar (these are the logged-in user's own profile)
            if (pLink.closest('[data-e2e*="nav-"]') || pLink.closest('[data-e2e="profile-icon"]') || pLink.closest('nav')) continue;
            var href = pLink.getAttribute('href') || '';
            var match = href.match(/\/@([^\/\?]+)/);
            if (match && match[1]) {
                log('[HEADER] Found username via last-resort profile link: ' + match[1]);
                return match[1];
            }
        }

        return '';
    };

    var isTargetUser = function (currentName) {
        if (!currentName || !userName) return false;
        if (isGroup) {
            // Substring match for group names (may contain emoji/extra text)
            return currentName.toLowerCase().trim().indexOf(userName.toLowerCase().trim()) !== -1;
        }
        return currentName.toLowerCase().trim() === userName.toLowerCase().trim();
    };

    // Get the header text for group matching (no profile link needed)
    var findCurrentChatName = function () {
        var doc = getRootDoc();
        var header = doc.querySelector('[data-e2e="chat-header"]') ||
            doc.querySelector('[class*="ChatHeader"]') ||
            doc.querySelector('[class*="chatHeader"]') ||
            doc.querySelector('[class*="DivChatHeader"]');
        if (!header) {
            log('[HEADER] No chat header found');
            return '';
        }

        var headerText = (header.textContent || '').trim();
        var profileLink = header.querySelector('a[href*="/@"]');
        var uniqueIdElem = header.querySelector('[data-e2e="chat-uniqueid"]');

        // Group-specific markers from detect_groups.js
        var hasGroupClass = !!header.querySelector('[class*="group"], [class*="Group"], [class*="member"], [class*="Member"]');
        var memberMatch = headerText.match(/(\d+)\s*member/i);
        var avatars = header.querySelectorAll('img');

        log('[HEADER] Detect: profileLink=' + !!profileLink + ', uniqueId=' + !!uniqueIdElem + ', groupClass=' + hasGroupClass + ', memberMatch=' + !!memberMatch + ', avatars=' + avatars.length);

        if (profileLink || uniqueIdElem) {
            log('[HEADER] Detected as DM (has profile link or uniqueid)');
            return '';
        }

        if (hasGroupClass || memberMatch || avatars.length > 1) {
            log('[HEADER] Detected as GROUP via markers: "' + headerText.substring(0, 30) + '"');
            return headerText;
        }

        // Conservative fallback: if there is NO profile link, we treat it as a potential group
        log('[HEADER] Treating as potential group (no profile link): "' + headerText.substring(0, 30) + '"');
        return headerText;
    };

    // ── Message Input & Sending ──────────────────────────────────────────────

    var findMessageInput = function () {
        var doc = getRootDoc();
        var editor = doc.querySelector('[class*="DraftEditor-editorContainer"] [contenteditable="true"]') ||
            doc.querySelector('[class*="DraftEditor-root"] [contenteditable="true"]') ||
            doc.querySelector('div[contenteditable="true"][role="textbox"]') ||
            doc.querySelector('div[contenteditable="true"]');

        return editor;
    };

    var findDraftEditor = function (messageInput) {
        var key = Object.keys(messageInput).find(function (k) {
            return k.startsWith('__reactFiber$') || k.startsWith('__reactInternalInstance$');
        });

        if (!key) {
            log('[DRAFT] React fiber not found on input element');
            return null;
        }

        log('[DRAFT] Found React fiber key: ' + key.substring(0, 20) + '...');
        var fiber = messageInput[key];
        var current = fiber;
        var depth = 0;

        while (current) {
            depth++;
            if (current.stateNode && current.stateNode.editor) {
                log('[DRAFT] Found Draft.js editor at fiber depth ' + depth);
                return current.stateNode;
            }
            current = current.return;
        }

        log('[DRAFT] Draft editor instance not found (walked ' + depth + ' fiber nodes)');
        return null;
    };

    // Writes the message into a Draft.js composer by handing the editor a new state.
    //
    // This is the only way that works on Business Suite. Draft.js keeps its own copy of the text in
    // editorState and renders from it; inserting characters into the DOM behind its back leaves the
    // two out of step, so the editor reports an empty document (no send button appears) and throws
    // while reconciling the nodes it did not expect.
    //
    // Returns true when the editor accepted the state.
    var typeMessageIntoDraftEditor = function (messageInput) {
        var draftEditor = findDraftEditor(messageInput);

        if (!draftEditor || !draftEditor.props) {
            log('[TYPE] Draft editor instance not reachable from the composer');
            return false;
        }

        try {
            var currentState = draftEditor.props.editorState;
            var ContentState = currentState.getCurrentContent().constructor;
            var EditorState = currentState.constructor;

            // createFromText builds the block structure Draft.js expects, so the editor re-renders
            // from its own state and every listener (including the send button) sees the text.
            var nextState = EditorState.createWithContent(ContentState.createFromText(message));
            draftEditor.props.onChange(nextState);

            log('[TYPE] Draft editor state set via onChange');
            return true;
        } catch (e) {
            log('[TYPE] Draft editor state update failed: ' + (e && e.message ? e.message : e));
            return false;
        }
    };

    var typeMessage = function (messageInput, callback) {
        log('[TYPE] Starting typeMessage...');

        // Business Suite: hand the text to the editor instead of the DOM. Falling back to the DOM
        // route here is not an option - it is what leaves the panel showing "Something went wrong".
        if (business) {
            if (!typeMessageIntoDraftEditor(messageInput)) {
                reportError('Could not type into the message composer');
                return;
            }

            setTimeout(callback, 300);
            return;
        }

        var tryExecCommand = function () {
            log('[TYPE] Using execCommand insertText...');
            messageInput.click();
            messageInput.focus();

            setTimeout(function () {
                var doc = getRootDoc();
                var win = getRootWin();
                var selection = win.getSelection ? win.getSelection() : window.getSelection();
                var range = doc.createRange ? doc.createRange() : document.createRange();
                range.selectNodeContents(messageInput);
                range.collapse(false);
                selection.removeAllRanges();
                selection.addRange(range);

                if (doc.execCommand) {
                    doc.execCommand('insertText', false, message);
                } else {
                    document.execCommand('insertText', false, message);
                }

                var content = (messageInput.textContent || '').trim();
                log('[TYPE] Content after execCommand: "' + content.substring(0, 50) + '" (len=' + content.length + ')');
                if (content.length > 0) {
                    setTimeout(callback, 300);
                } else {
                    messageInput.textContent = message;
                    messageInput.dispatchEvent(new (win.InputEvent || InputEvent)('input', { bubbles: true }));
                    setTimeout(callback, 300);
                }
            }, 150);
        };

        tryExecCommand();
    };

    // Business Suite send: the control is an <svg role="button"> that React listens to, and it has no
    // click() method of its own. Dispatching a bubbling click at it is what reaches the handler; the
    // classic path's extra parent clicks are deliberately not repeated here, because a second click
    // on an already-empty composer is a second send.
    //
    // Returns true when a click was dispatched.
    var clickBusinessSendButton = function () {
        var doc = getRootDoc();
        var win = getRootWin();
        var sendBtn = doc.querySelector('[data-e2e="dm-new-send-btn"]') ||
            doc.querySelector('[data-e2e="dm-new-send-btn"] [role="button"]');

        if (!sendBtn) {
            log('[SEND] No Business Suite send button (composer still empty?)');
            return false;
        }

        log('[SEND] Clicking Business Suite send button (' + sendBtn.tagName + ')');
        sendBtn.dispatchEvent(new (win.MouseEvent || MouseEvent)('click', {
            bubbles: true,
            cancelable: true,
            view: win,
            button: 0
        }));
        return true;
    };

    var sendMessage = function (messageInput) {
        var doc = getRootDoc();
        var win = getRootWin();

        if (business) {
            if (clickBusinessSendButton()) {
                return;
            }

            log('[SEND] Business Suite send button unavailable, pressing Enter...');
            var businessKeyEvent = win.KeyboardEvent || KeyboardEvent;
            messageInput.dispatchEvent(new businessKeyEvent('keydown', {
                key: 'Enter',
                code: 'Enter',
                keyCode: 13,
                which: 13,
                bubbles: true,
                cancelable: true
            }));
            messageInput.dispatchEvent(new businessKeyEvent('keyup', {
                key: 'Enter',
                code: 'Enter',
                keyCode: 13,
                which: 13,
                bubbles: true
            }));
            return;
        }

        var sendBtn = doc.querySelector('[data-e2e*="send"]') ||
            doc.querySelector('[data-e2e*="Send"]') ||
            doc.querySelector('button[type="submit"]');

        if (sendBtn) {
            log('[SEND] Found send button (' + sendBtn.tagName + '), clicking...');
            if (typeof sendBtn.click === 'function') {
                sendBtn.click();
            }
            sendBtn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: win }));
            if (sendBtn.parentElement && sendBtn.parentElement !== doc.body && typeof sendBtn.parentElement.click === 'function') {
                sendBtn.parentElement.click();
            }
            return;
        }

        log('[SEND] No send button found, pressing Enter...');
        var keyEventClass = win.KeyboardEvent || KeyboardEvent;
        messageInput.dispatchEvent(new keyEventClass('keydown', {
            key: 'Enter',
            code: 'Enter',
            keyCode: 13,
            which: 13,
            bubbles: true,
            cancelable: true
        }));

        messageInput.dispatchEvent(new keyEventClass('keyup', {
            key: 'Enter',
            code: 'Enter',
            keyCode: 13,
            which: 13,
            bubbles: true
        }));
    };

    // ── Result Reporting ─────────────────────────────────────────────────────

    // The app treats one callback as one finished friend and moves on, so a second callback for the
    // same injection would make it retry a message that may already have been delivered. The
    // watchdog can fire while a send is still in flight, which is exactly that case.
    var resultReported = false;

    var reportSuccess = function () {
        if (resultReported) return;
        resultReported = true;
        clearWatchdog();
        log('[RESULT] SUCCESS — Message sent to ' + userName);
        if (typeof StreakApp !== 'undefined') {
            StreakApp.onMessageSent(userName, true, '');
        }
    };

    var reportError = function (errorMessage) {
        if (resultReported) return;
        resultReported = true;
        clearWatchdog();
        log('[RESULT] FAIL — ' + errorMessage);
        if (typeof StreakApp !== 'undefined') {
            StreakApp.onMessageSent(userName, false, errorMessage);
        }
    };

    // Reads the composer as it is now, rather than the node captured before sending.
    //
    // Draft.js re-renders its content on send, so the element handed to us before typing can be
    // detached by the time the message is on its way. Returns null when there is no composer left,
    // which is a distinct outcome from an empty one.
    var readComposerText = function () {
        var doc = getRootDoc();
        var editor = doc.querySelector('[contenteditable="true"][role="textbox"]') ||
            doc.querySelector('[class*="DraftEditor-editorContainer"] [contenteditable="true"]');
        if (!editor) return null;
        return (editor.textContent || '').trim();
    };

    // ── Chat Processing ──────────────────────────────────────────────────────

    var sendMessageViaButton = function () {
        var messageInput = findMessageInput();

        if (!messageInput) {
            log('[CHAT] Message input NOT found on page');
            reportError('Message input not found');
            return;
        }

        if (dryRun) {
            log('[DRYRUN] Chat reachable - no message sent');
            reportSuccess();
            return;
        }

        log('[CHAT] Found message input, typing...');
        typeMessage(messageInput, function () {
            var typedContent = (messageInput.textContent || '').trim();
            if (typedContent.length === 0) {
                log('[CHAT] Message input empty after typing');
                reportError('Failed to type message in composer');
                return;
            }

            sendMessage(messageInput);

            if (business) {
                // The composer node captured before typing is usually detached by the time Draft.js
                // has re-rendered, so the outcome is read from the live composer. A crashed panel
                // means the message did not go out: reporting success there is how a broken send
                // used to look like a delivered one.
                setTimeout(function () {
                    if (chatPanelCrashed()) {
                        log('[VERIFY] Chat panel shows an error after sending');
                        if (typeof StreakApp !== 'undefined') {
                            StreakApp.reportVerification(false);
                        }
                        reportError('Chat panel failed after send');
                        return;
                    }

                    var remaining = readComposerText();
                    var confirmed = remaining === '';

                    log('[VERIFY] Composer after send: ' + (remaining === null ? 'gone' : 'len=' + remaining.length) +
                        ' -> ' + (confirmed ? 'confirmed' : 'unverified'));

                    if (typeof StreakApp !== 'undefined') {
                        StreakApp.reportVerification(confirmed);
                    }

                    reportSuccess();
                }, 1500);
                return;
            }

            setTimeout(function () {
                var remaining = (messageInput.textContent || '').trim();
                if (remaining.length > 0) {
                    log('[VERIFY] Composer still has text after send button click, trying Enter fallback...');
                    var win = getRootWin();
                    var keyEventClass = win.KeyboardEvent || KeyboardEvent;
                    messageInput.dispatchEvent(new keyEventClass('keydown', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
                    messageInput.dispatchEvent(new keyEventClass('keyup', { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                }

                setTimeout(function () {
                    var confirmed = false;
                    try {
                        var finalRemaining = (messageInput.textContent || '').trim();
                        confirmed = finalRemaining.length === 0;
                        log('[VERIFY] Composer final status: len=' + finalRemaining.length + ' -> ' + (confirmed ? 'confirmed' : 'unverified'));
                    } catch (e) {
                        log('[VERIFY] Could not read composer: ' + e.message);
                    }

                    if (typeof StreakApp !== 'undefined') {
                        StreakApp.reportVerification(confirmed);
                    }

                    reportSuccess();
                }, 1000);
            }, 1000);
        });
    };

    var searchForUserInCurrentChat = function () {
        var currentName;
        if (isGroup) {
            currentName = findCurrentChatName();
            log('[CHAT] Current chat: "' + (currentName || '(DM)').substring(0, 50) + '" (group target: "' + userName + '")');
        } else {
            currentName = findCurrentChatUsername();
            log('[CHAT] Current chat username: "' + currentName + '" (target: "' + userName + '")');
        }

        if (currentName) {
            checkedUsernames[currentName.toLowerCase()] = true;
        }

        if (isTargetUser(currentName)) {
            found = true;
            log('[CHAT] MATCH — Found ' + (isGroup ? 'group' : 'target user'));
            sendMessageViaButton();
            return true;
        }

        return false;
    };

    var checkNextChat = function () {
        if (found || chatIndex >= chatItems.length) {
            if (!found) {
                log('[CHAT] Exhausted ' + chatItems.length + ' visible items (' + newItemsFound + ' new), trying scroll...');
                scrollAndRetry();
            }
            return;
        }

        var chatItem = chatItems[chatIndex];

        // Optimization: Detect type from list item before clicking to avoid unnecessary loads
        var itemAvatars = chatItem.querySelectorAll('img');
        var itemLinks = chatItem.querySelectorAll('a[href*="/@"]');

        if (isGroup) {
            // If searching for a group, but the list item has a clear profile link, it's a DM
            if (itemLinks.length > 0) {
                log('[CHAT] Item ' + (chatIndex + 1) + ' has profile link, skipping DM...');
                chatIndex++;
                setTimeout(checkNextChat, 10);
                return;
            }
        } else {
            // DM mode only pre-click optimization
            for (var i = 0; i < itemLinks.length; i++) {
                var href = itemLinks[i].getAttribute('href') || '';
                var match = href.match(/\/@([^\/\?]+)/);
                if (match && match[1]) {
                    var inlineUsername = match[1].toLowerCase().trim();
                    if (checkedUsernames[inlineUsername]) {
                        chatIndex++;
                        setTimeout(checkNextChat, 10);
                        return;
                    }
                    if (isTargetUser(match[1])) {
                        log('[CHAT] MATCH — Found @' + match[1] + ' in item ' + (chatIndex + 1) + ' (pre-click)');
                        chatItem.click();
                        found = true;
                        setTimeout(sendMessageViaButton, 1500);
                        return;
                    }
                    break;
                }
            }
        }

        log('[CHAT] Clicking item ' + (chatIndex + 1) + '/' + chatItems.length);
        chatItem.click();

        var processOpenedChat = function (retriesLeft) {
            try {
                var currentName;
                if (isGroup) {
                    currentName = findCurrentChatName();
                    if (!currentName && retriesLeft > 0) {
                        setTimeout(function () { processOpenedChat(retriesLeft - 1); }, 500);
                        return;
                    }
                    if (currentName) {
                        log('[CHAT] Current chat: "' + currentName.substring(0, 50) + '" (group target: "' + userName + '")');
                    } else {
                        var dmUser = findCurrentChatUsername();
                        log('[CHAT] Skipping DM: @' + dmUser);
                        if (dmUser) checkedUsernames[dmUser.toLowerCase()] = true;
                        chatIndex++;
                        checkNextChat();
                        return;
                    }
                } else {
                    currentName = findCurrentChatUsername();
                    if (!currentName && retriesLeft > 0) {
                        setTimeout(function () { processOpenedChat(retriesLeft - 1); }, 500);
                        return;
                    }
                    log('[CHAT] Current chat username: "' + currentName + '" (target: "' + userName + '")');
                }

                var nameKey = currentName ? currentName.toLowerCase().trim() : '';

                if (nameKey && checkedUsernames[nameKey]) {
                    chatIndex++;
                    checkNextChat();
                    return;
                }

                if (nameKey) {
                    checkedUsernames[nameKey] = true;
                    newItemsFound++;
                }

                if (isTargetUser(currentName)) {
                    found = true;
                    log('[CHAT] MATCH — Found ' + (isGroup ? 'group' : 'target user') + ': "' + currentName + '"');
                    sendMessageViaButton();
                    return;
                }

                chatIndex++;
                checkNextChat();
            } catch (err) {
                log('[CHAT] Exception in processOpenedChat: ' + err.message);
                reportError('Exception in chat traversal: ' + err.message);
            }
        };

        setTimeout(function () {
            processOpenedChat(2);
        }, 1500);
    };

    // ── Initialization with Polling ──────────────────────────────────────────

    var init = function () {
        try {
            if (userName.startsWith('@')) {
                userName = userName.substring(1);
            }

            log('[INIT] Starting automation initialization...');
            dumpEnvironment();
            log('[INIT] Target user: ' + userName);
            log('[INIT] Inbox type: ' + (business ? 'Business Suite' : 'classic'));

            // A crashed chat panel never recovers on its own, and the app reuses the page for every
            // friend in the run. Failing here names the real cause instead of letting every remaining
            // friend fail one by one with "chat not found".
            if (business && chatPanelCrashed()) {
                log('[INIT] Chat panel is showing an error before we started');
                reportError('Chat panel is broken - reload the messages page');
                return;
            }

            // Pre-check: if the target chat is already open (e.g. when re-injecting between friends)
            var preCheckName = isGroup ? findCurrentChatName() : findCurrentChatUsername();
            if (preCheckName && isTargetUser(preCheckName)) {
                log('[INIT] Target ' + (isGroup ? 'group' : 'chat') + ' already open: ' + preCheckName);
                found = true;
                setTimeout(sendMessageViaButton, 500);
                return;
            }

            // 20 attempts at 1.5s apart is the 30s the error message below quotes.
            // This note must stay on its own line: the script is flattened onto one line before
            // injection, and a trailing comment would comment out every statement after it.
            var initAttempts = 0;
            var maxInitAttempts = 20;
            var pollForItems = function () {
                initAttempts++;
                log('[INIT] Checking for chat items (attempt ' + initAttempts + '/' + maxInitAttempts + ')...');
                chatItems = findChatItems();
                log('[INIT] Found ' + chatItems.length + ' chat items');

                if (chatItems.length > 0) {
                    log('[INIT] Items found, starting chat scan');
                    checkNextChat();
                    return;
                }

                if (initAttempts >= maxInitAttempts) {
                    log('[INIT] Still zero items after ' + (maxInitAttempts * 1.5) + 's, running diagnostics...');
                    dumpFullDiagnostics();
                    reportError('No chat items found after 30s wait');
                    return;
                }

                setTimeout(pollForItems, 1500);
            };

            log('[INIT] Waiting 2s for page stabilization...');
            setTimeout(pollForItems, 2000);

        } catch (e) {
            log('[INIT] EXCEPTION: ' + e.message);
            reportError('Error: ' + e.message);
        }
    };

    // Start the automation
    init();
})();