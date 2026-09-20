// Runs the real contact scanner against a stub inbox, outside Android.
//
// Every other test of this script only looks for text in it, which is how a scanner that threw a
// ReferenceError on its first chat passed 302 tests and shipped four times. Presence of a name in a
// file says nothing about the scope it lives in or the call it makes, so this executes the thing.
//
// Usage: node scanner_harness.js <path to tiktok_read_contacts.js>
//
// Exits 0 when the scanner walked both conversations and stopped cleanly, and 1 with the scanner's
// own log when it did not.

'use strict';

const fs = require('fs');
const vm = require('vm');

const scriptPath = process.argv[2];
if (!scriptPath) {
    console.error('usage: node scanner_harness.js <scanner script>');
    process.exit(2);
}

const script = fs.readFileSync(scriptPath, 'utf8');
const reports = [];

// The stub inbox: one direct message with a real username, and one group, which is the pair the
// header logic exists to tell apart.
const chats = [
    { username: 'alice', name: 'Alice' },
    { username: '', name: 'Weekend Crew' }
];

let openIndex = -1;

const listContainer = {
    scrollTop: 0,
    clientHeight: 100,
    scrollHeight: 100,
    parentElement: null
};

const header = {
    innerText: '',
    querySelector: function (selector) {
        if (selector.indexOf('/@') === -1) return null;
        const chat = chats[openIndex];
        if (!chat || !chat.username) return null;
        return { getAttribute: function () { return '/@' + chat.username; } };
    }
};

const rows = chats.map(function (chat, index) {
    return {
        parentElement: listContainer,
        closest: function () { return null; },
        click: function () {
            openIndex = index;
            header.innerText = chat.name;
        }
    };
});

const document = {
    body: {},
    querySelectorAll: function (selector) {
        if (selector.indexOf('dm-new-conversation-item') !== -1) return rows;
        return [];
    },
    querySelector: function (selector) {
        if (selector.indexOf('chat-header') !== -1) return openIndex >= 0 ? header : null;
        return null;
    }
};

const sandbox = {
    document: document,
    navigator: { userAgent: 'harness' },
    location: { href: 'https://www.tiktok.com/messages' },
    SaverScan: { report: function (json) { reports.push(JSON.parse(json)); } },
    console: console,
    clearTimeout: clearTimeout,
    // Delays collapse to zero so the whole walk finishes in milliseconds. The scanner only ever
    // chains timers, so the order is unchanged.
    setTimeout: function (fn) { return setTimeout(fn, 0); }
};

sandbox.window = sandbox;
sandbox.addEventListener = function () { };

vm.createContext(sandbox);

function fail(message) {
    console.error('FAIL: ' + message);
    console.error('--- scanner log ---');
    reports.forEach(function (report) {
        (report.log || []).forEach(function (line) { console.error('  ' + line); });
    });
    process.exit(1);
}

// An error thrown from one of the scanner's timer callbacks arrives here rather than through the
// call that started it - the same way it reaches window.onerror on the device, where the browser
// reports it only as "Script error." and the scan simply stops. That masking is what made this take
// four releases to find, so the harness reports the real message.
process.on('uncaughtException', function (e) {
    fail('the scanner threw: ' + (e && e.message ? e.message : e) +
        (e && e.stack ? '\n' + e.stack : ''));
});

try {
    vm.runInContext(script, sandbox, { filename: 'tiktok_read_contacts.js' });
} catch (e) {
    fail('the scanner threw while loading: ' + e.message);
}

const deadline = Date.now() + 5000;

function check() {
    const scan = sandbox.__saverScan;
    if (!scan) fail('the scanner never created its state');
    if (!scan.done) {
        if (Date.now() > deadline) fail('the scanner never finished');
        return setTimeout(check, 10);
    }

    const lines = [];
    reports.forEach(function (report) {
        (report.log || []).forEach(function (line) { lines.push(line); });
    });

    const broken = lines.filter(function (line) {
        return line.indexOf('page error') !== -1 ||
            line.indexOf('Open failed') !== -1 ||
            line.indexOf('Step failed') !== -1 ||
            line.indexOf('Retry failed') !== -1;
    });
    if (broken.length > 0) fail('the scanner reported failures: ' + broken.join(' | '));

    const items = scan.items || [];
    if (items.length !== 2) fail('expected 2 chats, got ' + items.length);

    const person = items.filter(function (i) { return i.username === 'alice' && !i.isGroup; });
    if (person.length !== 1) fail('the direct message was not identified by username');

    const group = items.filter(function (i) { return i.isGroup && i.displayName === 'Weekend Crew'; });
    if (group.length !== 1) fail('the group was not identified by its chat name');

    console.log('RESULT chats=' + items.length);
    items.forEach(function (item) {
        console.log('CHAT ' + (item.isGroup ? 'group "' + item.displayName + '"' : '@' + item.username));
    });
    process.exit(0);
}

check();
