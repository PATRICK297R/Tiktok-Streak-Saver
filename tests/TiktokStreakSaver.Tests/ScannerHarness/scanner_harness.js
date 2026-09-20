// Runs the real contact scanner against stub inboxes, outside Android.
//
// Every other test of this script only looks for text in it, which is how a scanner that threw a
// ReferenceError on its first chat passed 302 tests and shipped four times. Presence of a name in a
// file says nothing about the scope it lives in or the call it makes, so this executes the thing.
//
// The scenarios are the failures that reached a device: a header still showing a bare "@" while the
// account loads, and a group whose header links to one of its members.
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
const scenarios = [
    {
        title: 'a person, a group with no link, a group with a member stack, a group with a count',
        chats: [
            { name: 'Alice', links: ['/@alice'] },
            { name: 'Weekend Crew', links: [] },
            { name: 'FIVE MULTIPLE|SEASON 6', links: ['/@member1', '/@member2'] },
            { name: 'Humas SMKN 1 TROWULAN', links: ['/@humas'], memberText: '5 members' }
        ],
        expected: [
            { displayName: 'Alice', isGroup: false, username: 'alice' },
            { displayName: 'Weekend Crew', isGroup: true },
            { displayName: 'FIVE MULTIPLE|SEASON 6', isGroup: true },
            { displayName: 'Humas SMKN 1 TROWULAN', isGroup: true }
        ]
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
        title: 'a header that never loads is given up on and the walk carries on',
        chats: [
            { name: 'Never Loads', links: ['/@ghost'], blankReads: 99 },
            { name: 'Fadhil', links: ['/@Fadhil'] }
        ],
        expected: [
            { displayName: 'Fadhil', isGroup: false, username: 'Fadhil' }
        ],
        expectedLog: ['Header never settled']
    }
];

// ─── Stub page ───────────────────────────────────────────────────────────────

function buildSandbox(scenario) {
    const state = { openIndex: -1, reads: [] };

    const chatOf = (index) => (index >= 0 ? scenario.chats[index] : null);
    const isBlank = (index) => state.reads[index] <= ((chatOf(index) || {}).blankReads || 0);

    const links = (chat) => chat.links.map((href) => ({
        getAttribute: () => href,
        closest: () => null
    }));

    const container = {
        scrollTop: 0,
        clientHeight: 100,
        scrollHeight: 100,
        parentElement: null
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

            if (selector === 'img') return new Array(chat.avatars || 1).fill({});
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

    const rows = scenario.chats.map((chat, index) => ({
        parentElement: container,
        closest: () => null,
        click: () => { state.openIndex = index; }
    }));

    // Profile links outside the header, which is what a group's message pane holds: one per member
    // who has written. The scanner must ignore these - taking one of them is what turned a group
    // into a friend named after a member.
    const paneLinks = () => chatOf(state.openIndex)
        ? [{ getAttribute: () => '/@writer1', closest: () => null },
           { getAttribute: () => '/@writer2', closest: () => null }]
        : [];

    const document = {
        body: {},
        querySelectorAll: (selector) => {
            if (selector.indexOf('dm-new-conversation-item') !== -1) return rows;
            if (selector.indexOf('/@') !== -1) return paneLinks();
            return [];
        },
        querySelector: (selector) =>
            selector.indexOf('chat-header') !== -1 && state.openIndex >= 0 ? header : null
    };

    const sandbox = {
        document,
        navigator: { userAgent: 'harness' },
        location: { href: 'https://www.tiktok.com/messages' },
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

    sandbox.window = sandbox;
    sandbox.addEventListener = () => { };

    return sandbox;
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
    const sandbox = buildSandbox(scenario);
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

            resolve(scan);
        };

        poll();
    });
}

function check(scenario, scan) {
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
        const found = items.filter((item) => item.displayName === want.displayName);
        if (found.length !== 1) {
            return 'expected one chat named "' + want.displayName + '", got ' + found.length;
        }

        const item = found[0];
        if (item.isGroup !== want.isGroup) {
            return '"' + want.displayName + '" was read as ' + describe(item)
                + ' but is ' + (want.isGroup ? 'a group' : 'a person');
        }

        if (!want.isGroup && item.username !== want.username) {
            return '"' + want.displayName + '" was read as @' + item.username
                + ' but its username is ' + want.username;
        }
    }

    return '';
}

(async () => {
    const failures = [];
    let passed = 0;

    for (const scenario of scenarios) {
        const scan = await run(scenario);
        const problem = check(scenario, scan);

        if (problem) {
            failures.push({ scenario, scan, problem });
            continue;
        }

        passed++;
        console.log('PASS ' + scenario.title + ' (' + scan.items.length + ' chats)');
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

