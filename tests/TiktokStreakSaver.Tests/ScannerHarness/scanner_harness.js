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
            { name: 'Humas SMKN 1 TROWULAN', links: ['/@humas'], memberText: '5 members' },
            // A person whose display name is not their handle. The name has a space in it, so this is
            // the shape a group's generated handle has - but the handle is nothing like the name, and
            // reading it as a group is the false positive that would hurt.
            { name: 'Rasya RaisKuker', links: ['/@virkun01'] }
        ],
        expected: [
            { displayName: 'Alice', isGroup: false, username: 'alice' },
            { displayName: 'Weekend Crew', isGroup: true },
            { displayName: 'FIVE MULTIPLE|SEASON 6', isGroup: true },
            { displayName: 'Humas SMKN 1 TROWULAN', isGroup: true },
            { displayName: 'Rasya RaisKuker', isGroup: false, username: 'virkun01' }
        ]
    },
    {
        // Taken from the device log, where these two were imported as friends. Their headers each
        // hold one profile link and nothing else to go on: no member stack, no participant count.
        // What they share is the handle, which is the group's name spelled out - truncated, in the
        // first case, exactly as TikTok truncates it.
        title: 'the two groups from the device log, recognised by their generated handle',
        chats: [
            { name: 'Humas SMKN 1 TROWULAN', links: ['/@humas.smkn.1.trow'] },
            { name: 'FIVE MULTIPLE|SEASON 6', links: ['/@five.multiple'] }
        ],
        expected: [
            { displayName: 'Humas SMKN 1 TROWULAN', isGroup: true },
            { displayName: 'FIVE MULTIPLE|SEASON 6', isGroup: true }
        ]
    },
    {
        // The whole inbox from the device log, exactly as it was reported: twenty-two people and two
        // groups. Every handle here was read off a real header, which makes this the closest thing to
        // a real run that can be had without a device - and the pair that started this is in it.
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
            { displayName: 'Humas SMKN 1 TROWULAN', isGroup: true },
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
            { displayName: 'FIVE MULTIPLE|SEASON 6', isGroup: true },
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

// The key a chat is filed under, so that two chats sharing a display name are still told apart.
const keyOf = (item) => (item.isGroup ? 'group:' + item.displayName : 'user:' + item.username);
const wantKey = (want) => (want.isGroup ? 'group:' + want.displayName : 'user:' + want.username);

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

