// Runs the real contact scanner against stub inboxes, outside Android.
//
// Every other test of this script only looks for text in it, which is how a scanner that threw a
// ReferenceError on its first chat passed 302 tests and shipped four times. Presence of a name in a
// file says nothing about the scope it lives in or the call it makes, so this executes the thing.
//
// The scenarios are the failures that reached a device: a header still showing a bare "@" while the
// account loads, a scan that read the same chats again after the list scrolled, and two accounts whose
// handles spell out their display names - which were read as groups for a while, and are not groups.
//
// Usage: node scanner_harness.js <path to tiktok_read_contacts.js>
//
// Exits 0 when every scenario behaves, and 1 with the scanner's own log when one does not.

'use strict';

const fs = require('fs');
const vm = require('vm');

const scriptPath = process.argv[2];
if (!scriptPath) {
    console.error('usage: node scanner_harness.js <scanner script>');
    process.exit(2);
}

const script = fs.readFileSync(scriptPath, 'utf8');

// ─── Scenarios ───────────────────────────────────────────────────────────────

// Each chat describes the header its conversation has once open.
//   links      hrefs of the profile links in the header - one for a person, one per member avatar
//              for a group, none at all for a group that links to no single account
//   memberText participant count, as a group's header shows it
//   blankReads how many reads still show the bare "@" the header renders before the account loads
// A list long enough to need scrolling, for the two scenarios below. Generated because what matters is
// the count and the order, not the names.
const scrollingInbox = Array.from({ length: 24 }, (_, i) => ({
    name: 'Chat ' + String(i + 1).padStart(2, '0'),
    links: ['/@user' + String(i + 1).padStart(2, '0')]
}));

const scrollingExpected = Array.from({ length: 24 }, (_, i) => ({
    displayName: 'Chat ' + String(i + 1).padStart(2, '0'),
    isGroup: false,
    username: 'user' + String(i + 1).padStart(2, '0')
}));

// A longer inbox for the case where a scroll moves the list by more than the old guess of ten rows. The
// names are what make a missing chat visible in the failure output.
const longInbox = Array.from({ length: 40 }, (_, i) => ({
    name: 'Chat ' + String(i + 1).padStart(2, '0'),
    links: ['/@user' + String(i + 1).padStart(2, '0')]
}));

const longExpected = Array.from({ length: 40 }, (_, i) => ({
    displayName: 'Chat ' + String(i + 1).padStart(2, '0'),
    isGroup: false,
    username: 'user' + String(i + 1).padStart(2, '0')
}));

const scenarios = [
    {
        title: 'a person, a group with no link, a group with a member stack, a group with a count',
        chats: [
            { name: 'Alice', links: ['/@alice'] },
            { name: 'Weekend Crew', links: [] },
            { name: 'Band Practice', links: ['/@member1', '/@member2'] },
            { name: 'Class of 2027', links: ['/@one_member'], memberText: '5 members' },
            // A person whose display name is not their handle. The name has a space in it, which a rule
            // that read handles as generated from names used to call a group - see the next scenario,
            // which is where that rule was caught doing it to two real accounts.
            { name: 'Rasya RaisKuker', links: ['/@virkun01'] }
        ],
        expected: [
            { displayName: 'Alice', isGroup: false, username: 'alice' },
            { displayName: 'Weekend Crew', isGroup: true },
            { displayName: 'Band Practice', isGroup: true },
            { displayName: 'Class of 2027', isGroup: true },
            { displayName: 'Rasya RaisKuker', isGroup: false, username: 'virkun01' }
        ]
    },
    {
        // The two rows that were read as groups for a while, and are not groups at all.
        //
        // Both are ordinary accounts whose handles happen to spell out their display names -
        // "humas.smkn.1.trow" for "Humas SMKN 1 TROWULAN", "five.multiple" for "FIVE MULTIPLE|SEASON 6".
        // Their owner has no group chats: they checked both accounts and neither is one. A rule that
        // reads a spelled-out handle as a group therefore put two wrong rows into every import of this
        // inbox, and this scenario pins the opposite answer. One profile link is a person.
        title: 'accounts whose handle spells out their name are still people',
        chats: [
            { name: 'Humas SMKN 1 TROWULAN', links: ['/@humas.smkn.1.trow'] },
            { name: 'FIVE MULTIPLE|SEASON 6', links: ['/@five.multiple'] }
        ],
        expected: [
            { displayName: 'Humas SMKN 1 TROWULAN', isGroup: false, username: 'humas.smkn.1.trow' },
            { displayName: 'FIVE MULTIPLE|SEASON 6', isGroup: false, username: 'five.multiple' }
        ]
    },
    {
        // The whole inbox from the device log, exactly as it was reported: twenty-four people and no
        // groups at all. Every handle here was read off a real header, which makes this the closest
        // thing to a real run that can be had without a device.
        //
        // It was read as twenty-two people and two groups for a while, and the two were the whole
        // reason a handle rule was invented: their handles spell out their names, and that looked like
        // the one difference between a group and a person. They are accounts, so the count was wrong
        // and the rule was fitted to them. Both stay in this scenario as people, which is what makes it
        // the guard against that rule coming back.
        title: 'the whole inbox from the device log',
        chats: [
            { name: 'munchkie', links: ['/@vkyardians'] },
            { name: 'kenpanzz', links: ['/@kenpanzz'] },
            { name: 'jws\ud83e\udeb6', links: ['/@niank_889'] },
            { name: 'Bref', links: ['/@bref2027'] },
            { name: 'ikramnursofi', links: ['/@ikramnursofi2'] },
            { name: 'Zyou', links: ['/@zyouredgrave'] },
            { name: 'Zexor', links: ['/@zexora990'] },
            { name: 'v', links: ['/@yyyanzz'] },
            { name: 'Rasya RaisKuker', links: ['/@virkun01'] },
            { name: 'aryaaaaa612', links: ['/@aryaaaaa612'] },
            { name: 'padil pedil hy', links: ['/@fadhiel_mahen'] },
            { name: 'Humas SMKN 1 TROWULAN', links: ['/@humas.smkn.1.trow'] },
            { name: 'snowb\ud83c\udf35', links: ['/@suanz07'] },
            { name: 'Zyou', links: ['/@huzyou'] },
            { name: 'rap', links: ['/@rapreprapraprep'] },
            { name: 'eL', links: ['/@vaparelmc'] },
            { name: 'Koh_viiin', links: ['/@xzaldv'] },
            { name: '\ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4', links: ['/@xznsenpai'] },
            { name: 'Bayu6852', links: ['/@bayu6852'] },
            { name: 'trayyv', links: ['/@knethgr'] },
            { name: 'Qizi~huono', links: ['/@qizicihuy8'] },
            { name: 'FIVE MULTIPLE|SEASON 6', links: ['/@five.multiple'] },
            { name: 'sniie', links: ['/@agnieputraa'] },
            { name: 'Fadhil', links: ['/@lo.masfadhil'] }
        ],
        expected: [
            { displayName: 'munchkie', isGroup: false, username: 'vkyardians' },
            { displayName: 'kenpanzz', isGroup: false, username: 'kenpanzz' },
            { displayName: 'jws\ud83e\udeb6', isGroup: false, username: 'niank_889' },
            { displayName: 'Bref', isGroup: false, username: 'bref2027' },
            { displayName: 'ikramnursofi', isGroup: false, username: 'ikramnursofi2' },
            { displayName: 'Zyou', isGroup: false, username: 'zyouredgrave' },
            { displayName: 'Zexor', isGroup: false, username: 'zexora990' },
            { displayName: 'v', isGroup: false, username: 'yyyanzz' },
            { displayName: 'Rasya RaisKuker', isGroup: false, username: 'virkun01' },
            { displayName: 'aryaaaaa612', isGroup: false, username: 'aryaaaaa612' },
            { displayName: 'padil pedil hy', isGroup: false, username: 'fadhiel_mahen' },
            { displayName: 'Humas SMKN 1 TROWULAN', isGroup: false, username: 'humas.smkn.1.trow' },
            { displayName: 'snowb\ud83c\udf35', isGroup: false, username: 'suanz07' },
            { displayName: 'Zyou', isGroup: false, username: 'huzyou' },
            { displayName: 'rap', isGroup: false, username: 'rapreprapraprep' },
            { displayName: 'eL', isGroup: false, username: 'vaparelmc' },
            { displayName: 'Koh_viiin', isGroup: false, username: 'xzaldv' },
            // Named with eleven invisible characters on the account itself. It was imported on a device
            // under exactly those, which is what gave the filler character away - it is U+FFF4, and it
            // was not on the list of blanks. Filed under the handle instead, which is what the app sends
            // by anyway.
            { displayName: 'xznsenpai', isGroup: false, username: 'xznsenpai' },
            { displayName: 'Bayu6852', isGroup: false, username: 'bayu6852' },
            { displayName: 'trayyv', isGroup: false, username: 'knethgr' },
            { displayName: 'Qizi~huono', isGroup: false, username: 'qizicihuy8' },
            { displayName: 'FIVE MULTIPLE|SEASON 6', isGroup: false, username: 'five.multiple' },
            { displayName: 'sniie', isGroup: false, username: 'agnieputraa' },
            { displayName: 'Fadhil', isGroup: false, username: 'lo.masfadhil' }
        ]
    },
    {
        // What the device did with a name made of invisible filler. A person is kept under their
        // handle, because that is what the app sends by; a group is left alone, because a group is
        // matched by its name and there is no name to match.
        title: 'a name of invisible filler',
        chats: [
            { name: '\ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4', links: ['/@xznsenpai'] },
            { name: '\ufff4 \ufff4 \ufff4 \ufff4 \ufff4 \ufff4', links: ['/@member1', '/@member2'] }
        ],
        expected: [
            { displayName: 'xznsenpai', isGroup: false, username: 'xznsenpai' }
        ],
        expectedLog: ['named by its handle']
    },
    {
        // The bug the device log shows. Ten rows are rendered, then nineteen, then twenty-four, with the
        // rows already read still in place - and the scan re-read ten of them on every scroll because it
        // guessed where to resume from. It resumes at the first row without a mark now, so each chat is
        // opened exactly once.
        title: 'a list that grows as it is scrolled is read once',
        chats: scrollingInbox,
        windowSize: 10,
        revealPerScroll: 9,
        eachOpenedOnce: true,
        expected: scrollingExpected
    },
    {
        // The other way a list can behave: the same row elements are handed whatever is scrolled into
        // view, so a row carries the mark of the chat that used to be in it. The mark is not trusted
        // then and the row is read again - a few repeat opens, which is the safe direction - but every
        // chat is still found, and none is skipped.
        title: 'a list that recycles its rows still finds every chat',
        chats: scrollingInbox,
        windowSize: 10,
        revealPerScroll: 9,
        recycles: true,
        // Six rows come back into view across the two scrolls and are read again, which is the price of
        // not trusting a mark on a row that may have been handed another chat. The point of the scenario
        // is the line above it: every chat is still found.
        maxRepeatOpens: 8,
        expected: scrollingExpected
    },
    {
        // The dangerous half of the same bug. When a scroll moves the list further than the old guess of
        // ten rows, resuming ten rows back from the end of the screen lands past the first chat that has
        // not been read - and those chats were never recorded at all. Twenty-four rows move by eighteen
        // here, so six of them were in that gap.
        title: 'a list that jumps further than a screen does not lose chats',
        chats: longInbox,
        windowSize: 24,
        revealPerScroll: 18,
        recycles: true,
        maxRepeatOpens: 12,
        expected: longExpected
    },
    {
        title: 'a header still loading is waited for, not recorded as "@"',
        chats: [
            { name: 'Ken Panzz', links: ['/@kenpanzz'], blankReads: 2 },
            { name: 'Bref', links: ['/@Bref'] }
        ],
        expected: [
            { displayName: 'Ken Panzz', isGroup: false, username: 'kenpanzz' },
            { displayName: 'Bref', isGroup: false, username: 'Bref' }
        ],
        forbidden: ['"@"']
    },
    {
        // Nothing to file it under: the name never fills in and the header links to no account, so the
        // chat is given up on and the walk moves to the next one. This is the path that keeps a stuck
        // chat from stalling the scan.
        title: 'a header that never loads is given up on and the walk carries on',
        chats: [
            { name: 'Never Loads', links: [], blankReads: 99 },
            { name: 'Fadhil', links: ['/@Fadhil'] }
        ],
        expected: [
            { displayName: 'Fadhil', isGroup: false, username: 'Fadhil' }
        ],
        expectedLog: ['Header never settled']
    },
    {
        title: 'a Business Suite inbox hosted inside an iframe',
        wrapInIframe: true,
        href: 'https://www.tiktok.com/business-suite/messages',
        chats: [
            { name: 'Zexor', links: ['/@zexora990'] },
            { name: 'Alice', links: ['/@alice'] }
        ],
        expected: [
            { displayName: 'Zexor', isGroup: false, username: 'zexora990' },
            { displayName: 'Alice', isGroup: false, username: 'alice' }
        ]
    }
];

// ─── Stub page ───────────────────────────────────────────────────────────────

function buildSandbox(scenario) {
    const total = scenario.chats.length;

    // How many rows the list renders at once, and how many more each scroll brings into view. A
    // scenario that sets neither has no scrolling at all, which is how the earlier scenarios stay as
    // they were.
    const windowSize = Math.min(scenario.windowSize || total, total);
    const revealPerScroll = scenario.revealPerScroll || windowSize;

    // Whether the list reuses its row elements for whatever is scrolled into view, or keeps the rows it
    // has already rendered and adds the new ones below them. Both were seen on a device, and the second
    // is the one the log shows: ten rows, then nineteen, then twenty-four.
    const recycles = !!scenario.recycles;

    const state = {
        openIndex: -1,
        reads: [],
        // How many times each chat was opened, so a test can tell a scan that read everything once from
        // one that went back over what it had already read.
        opened: new Array(total).fill(0),
        revealed: windowSize,
        windowStart: 0,
        exhausted: total <= windowSize
    };

    const chatOf = (index) => (index >= 0 ? scenario.chats[index] : null);
    const isBlank = (index) => state.reads[index] <= ((chatOf(index) || {}).blankReads || 0);

    const links = (chat) => chat.links.map((href) => ({
        getAttribute: () => href,
        closest: () => null
    }));

    const container = {
        parentElement: null,
        clientHeight: windowSize,
        get scrollHeight() { return total; },

        _scrollTop: 0,
        get scrollTop() { return this._scrollTop; },

        // Scrolling is what brings the next screen into view, and it stops when there is nothing left -
        // which is how the scanner learns it has reached the end of the list.
        set scrollTop(value) {
            if (state.exhausted) return;

            const next = Math.max(0, value);
            if (next === this._scrollTop) return;
            this._scrollTop = next;

            if (recycles) {
                state.windowStart = Math.min(total - windowSize, state.windowStart + revealPerScroll);
                if (state.windowStart + windowSize >= total) state.exhausted = true;
            } else {
                state.revealed = Math.min(total, state.revealed + revealPerScroll);
                if (state.revealed >= total) state.exhausted = true;
            }
        }
    };

    // One row element per chat, kept once made: the list grows by adding rows, so the ones already read
    // are still the same elements and still carry whatever the scanner wrote on them.
    const elements = [];

    // Or one element per slot, handed a different chat as the window moves - the recycling case, where a
    // mark left on a row points at the chat that used to be in it.
    const pool = [];

    const rowFor = (chatIndex, slot) => {
        const store = recycles ? pool : elements;
        const key = recycles ? slot : chatIndex;

        if (!store[key]) {
            store[key] = {
                parentElement: container,
                closest: () => null,
                index: chatIndex,

                // A row's text is what the scanner compares against the mark it left, so the stub has to
                // have it: a recycled row showing a different chat reads differently, and that difference
                // is the only thing standing between "already read" and skipping a chat that never was.
                get textContent() {
                    const chat = chatOf(store[key].index);
                    return chat ? chat.name + ' · sent a message' : '';
                },
                get innerText() { return this.textContent; },

                click: () => {
                    state.opened[store[key].index] = (state.opened[store[key].index] || 0) + 1;
                    state.openIndex = store[key].index;
                }
            };
        }

        store[key].index = chatIndex;
        return store[key];
    };

    const rowsFor = () => {
        const rows = [];

        if (recycles) {
            for (let slot = 0; slot < windowSize && state.windowStart + slot < total; slot++)
                rows.push(rowFor(state.windowStart + slot, slot));
        } else {
            for (let index = 0; index < state.revealed; index++)
                rows.push(rowFor(index, index));
        }

        return rows;
    };

    const header = {
        // Counting reads of innerText is how "the header is still filling in" is modelled: the
        // scanner reads it once per attempt, so the first blankReads attempts see the placeholder.
        get innerText() {
            const chat = chatOf(state.openIndex);
            if (!chat) return '';

            state.reads[state.openIndex] = (state.reads[state.openIndex] || 0) + 1;
            if (isBlank(state.openIndex)) return '@';

            return chat.name + (chat.memberText ? '\n' + chat.memberText : '');
        },

        get textContent() {
            const chat = chatOf(state.openIndex);
            if (!chat || isBlank(state.openIndex)) return '@';

            return chat.name + (chat.memberText ? ' ' + chat.memberText : '');
        },

        querySelectorAll(selector) {
            const chat = chatOf(state.openIndex);
            if (!chat) return [];

            if (selector === 'img') return new Array(chat.avatars || 1).fill({}).map((_, i) => ({
                getAttribute: (name) => (name === 'src' ? 'avatar' + i + '.jpg' : null)
            }));
            if (selector.indexOf('/@') !== -1) return links(chat);

            return [];
        },

        querySelector(selector) {
            const chat = chatOf(state.openIndex);
            if (!chat) return null;

            if (selector.indexOf('/@') !== -1) return links(chat)[0] || null;
            return null;
        }
    };

    // Profile links outside the header, which is what a group's message pane holds: one per member
    // who has written. The scanner must ignore these - taking one of them is what turned a group
    // into a friend named after a member.
    const paneLinks = () => chatOf(state.openIndex)
        ? [{ getAttribute: () => '/@writer1', closest: () => null },
           { getAttribute: () => '/@writer2', closest: () => null }]
        : [];

    const isIframe = !!scenario.wrapInIframe;
    const innerDocument = {
        body: {},
        querySelectorAll: (selector) => {
            if (selector.indexOf('dm-new-conversation-item') !== -1) return rowsFor();
            if (selector.indexOf('/@') !== -1) return paneLinks();
            return [];
        },
        querySelector: (selector) =>
            selector.indexOf('chat-header') !== -1 && state.openIndex >= 0 ? header : null
    };

    const sandbox = {
        navigator: { userAgent: 'harness' },
        location: { href: scenario.href || 'https://www.tiktok.com/messages' },
        SaverScan: { report: () => { } },
        console,
        clearTimeout,
        // Delays collapse to zero so a walk finishes in milliseconds. The scanner only ever chains
        // timers, so the order is unchanged.
        setTimeout: (fn) => setTimeout(fn, 0),
        innerWidth: 1280,
        innerHeight: 800,
        devicePixelRatio: 1
    };

    const document = isIframe ? {
        body: {},
        querySelectorAll: () => [],
        querySelector: (selector) => {
            if (selector.indexOf('iframe') !== -1) {
                return {
                    contentDocument: innerDocument,
                    contentWindow: sandbox
                };
            }
            return null;
        }
    } : innerDocument;

    sandbox.document = document;
    sandbox.window = sandbox;
    sandbox.addEventListener = () => { };

    return { sandbox, state };
}

// ─── Runner ──────────────────────────────────────────────────────────────────

function fail(message) {
    console.error('FAIL: ' + message);
    process.exit(1);
}

// An error thrown from one of the scanner's timer callbacks arrives here rather than through the
// call that started it - the same way it reaches window.onerror on the device, where the browser
// reports it only as "Script error." and the scan simply stops. That masking is what made this take
// four releases to find, so the harness reports the real message.
process.on('uncaughtException', (e) => {
    fail('the scanner threw: ' + (e && e.message ? e.message : e)
        + (e && e.stack ? '\n' + e.stack : ''));
});

const describe = (item) => (item.isGroup
    ? 'group "' + item.displayName + '"'
    : '@' + item.username + ' "' + item.displayName + '"');

function run(scenario) {
    const { sandbox, state } = buildSandbox(scenario);
    vm.createContext(sandbox);

    try {
        vm.runInContext(script, sandbox, { filename: 'tiktok_read_contacts.js' });
    } catch (e) {
        fail(scenario.title + ': the scanner threw while loading: ' + e.message);
    }

    return new Promise((resolve) => {
        const deadline = Date.now() + 5000;

        const poll = () => {
            const scan = sandbox.__saverScan;
            if (!scan) fail(scenario.title + ': the scanner never created its state');

            if (!scan.done) {
                if (Date.now() > deadline) fail(scenario.title + ': the scanner never finished');
                return setTimeout(poll, 5);
            }

            resolve({ scan, state });
        };

        poll();
    });
}

// The key a chat is filed under, so that two chats sharing a display name are still told apart.
const keyOf = (item) => (item.isGroup ? 'group:' + item.displayName : 'user:' + item.username);
const wantKey = (want) => (want.isGroup ? 'group:' + want.displayName : 'user:' + want.username);

function check(scenario, scan, state) {
    const lines = scan.log || [];
    const items = scan.items || [];

    for (const line of scenario.forbidden || []) {
        if (lines.some((entry) => entry.indexOf(line) !== -1)) {
            return 'the log still contains ' + line;
        }
    }

    for (const line of scenario.expectedLog || []) {
        if (!lines.some((entry) => entry.indexOf(line) !== -1)) {
            return 'the log never says "' + line + '"';
        }
    }

    if (items.length !== scenario.expected.length) {
        return 'expected ' + scenario.expected.length + ' chats, got ' + items.length
            + ': ' + items.map(describe).join(', ');
    }

    for (const want of scenario.expected) {
        const found = items.filter((item) => keyOf(item) === wantKey(want));
        if (found.length !== 1) {
            return 'expected one chat filed under ' + wantKey(want) + ', got ' + found.length;
        }

        const item = found[0];
        if (item.isGroup !== want.isGroup) {
            return '"' + want.displayName + '" was read as ' + describe(item)
                + ' but is ' + (want.isGroup ? 'a group' : 'a person');
        }

        if (item.displayName !== want.displayName) {
            return 'the chat filed under ' + wantKey(want) + ' is named "' + item.displayName
                + '" but should be "' + want.displayName + '"';
        }
    }

    // The scrolling case: every chat opened exactly once, which is what stops a scroll from reading the
    // same conversations over again. The old index guess re-opened ten of them per scroll.
    const repeats = state.opened.reduce((sum, count) => sum + Math.max(0, count - 1), 0);

    if (scenario.eachOpenedOnce && repeats > 0) {
        const repeated = scenario.chats
            .map((chat, index) => ({ chat, count: state.opened[index] }))
            .filter((entry) => entry.count > 1)
            .map((entry) => entry.chat.name + ' x' + entry.count);

        return 'chats were opened more than once: ' + repeated.join(', ');
    }

    if (scenario.maxRepeatOpens !== undefined && repeats > scenario.maxRepeatOpens) {
        return 'the scan opened chats again ' + repeats + ' times, more than the '
            + scenario.maxRepeatOpens + ' a scroll should cost';
    }

    return '';
}

(async () => {
    const failures = [];
    let passed = 0;

    for (const scenario of scenarios) {
        const { scan, state } = await run(scenario);
        const problem = check(scenario, scan, state);

        if (problem) {
            failures.push({ scenario, scan, problem });
            continue;
        }

        passed++;
        const repeats = state.opened.reduce((sum, count) => sum + Math.max(0, count - 1), 0);
        console.log('PASS ' + scenario.title + ' (' + scan.items.length + ' chats, '
            + repeats + ' repeat opens)');
        scan.items.forEach((item) => console.log('  CHAT ' + describe(item)));
    }

    // Every scenario is run before reporting, so one run shows everything that is wrong instead of
    // stopping at the first.
    if (failures.length > 0) {
        for (const { scenario, scan, problem } of failures) {
            console.error('FAIL: ' + scenario.title + ' - ' + problem);
            console.error('--- chats recorded ---');
            (scan.items || []).forEach((item) => console.error('  ' + describe(item)));
            console.error('--- scanner log ---');
            (scan.log || []).forEach((line) => console.error('  ' + line));
        }

        console.error('FAILED ' + failures.length + ' of ' + scenarios.length + ' scenarios');
        process.exit(1);
    }

    console.log('RESULT scenarios=' + passed);
    process.exit(0);
})();

