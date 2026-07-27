// Unit tests for the setup wizard's branch table.
//
// The wizard (Services/Setup/index.html) is an embedded resource with no build step and no module
// system, so the pure function is lifted straight out of the markup by its delimiters and exercised
// here. Dependency-free on purpose: `node --test`, no package.json, no npm install — matching the
// plain .mjs scripts already in perf-harness/.
//
//   node --test tests/setup-wizard/

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const pagePath = join(repoRoot, 'Services', 'Setup', 'index.html');
const page = readFileSync(pagePath, 'utf8');

function loadWizardLogic() {
    const match = page.match(/\/\/ <craft:wizard-state>([\s\S]*?)\/\/ <\/craft:wizard-state>/);
    assert.ok(match, 'craft:wizard-state markers not found in Services/Setup/index.html');
    return new Function(`${match[1]}\nreturn { decideWizardState, statusReadError };`)();
}

const { decideWizardState, statusReadError } = loadWizardLogic();

// Shorthand for the status payload /api/setup/status returns.
const status = (usersStatus, extra = {}) => ({ appName: 'Craft', usersStatus, ...extra });
const reachable = (hasUsers) => ({ connected: true, hasUsers });
const unreachable = (error) => ({ connected: false, hasUsers: false, error });

// ── The regression ──────────────────────────────────────────────────────────────────────────────
// The reported bug: the operator types an address and the Add Superadmin button will not take a
// click, because the button shipped disabled and only one branch of one un-retried fetch ever
// enabled it. Step 1 must be actionable in every state where step 1 is still outstanding, including
// the states where we know nothing at all about the host.

test('the seed button is usable in every state where step 1 is still outstanding', () => {
    const stillOutstanding = [
        ['first read still in flight', { stepOneDone: false, attempted: false, status: null }],
        ['status load failed', { stepOneDone: false, attempted: true, status: null, error: 'HTTP 503' }],
        ['status load timed out', { stepOneDone: false, attempted: true, status: null, error: 'signal timed out' }],
        ['storage unreachable', { stepOneDone: false, status: status(unreachable('no route to host')) }],
        ['usersStatus absent', { stepOneDone: false, status: status(undefined) }],
        ['storage reachable, table empty', { stepOneDone: false, status: status(reachable(false)) }],
    ];

    for (const [label, input] of stillOutstanding) {
        assert.equal(decideWizardState(input).seedEnabled, true, `seed must stay usable: ${label}`);
    }
});

test('an empty input object does not throw and leaves step 1 usable', () => {
    // Defensive: the poller can call this before anything has been read.
    assert.equal(decideWizardState({}).seedEnabled, true);
    assert.equal(decideWizardState(undefined).seedEnabled, true);
});

// ── Nothing known yet ───────────────────────────────────────────────────────────────────────────

test('before the first read comes back the page is usable and quiet', () => {
    // Not attempted yet is not the same as failed. Complaining at t=0, before the very first request
    // has had a chance to answer, greets every operator with an error the page is about to resolve.
    const state = decideWizardState({ stepOneDone: false, attempted: false, status: null });

    assert.equal(state.seedEnabled, true, 'step 1 must not wait on the network');
    assert.equal(state.authEnabled, false);
    assert.equal(state.message, '');
    assert.equal(state.messageType, '');
});

test('unreadable status locks the auth sections but says so and keeps retrying', () => {
    const state = decideWizardState({ stepOneDone: false, attempted: true, status: null, error: null });

    assert.equal(state.seedEnabled, true);
    assert.equal(state.authEnabled, false, 'must not unlock auth without knowing a superadmin exists');
    assert.equal(state.userSectionDone, false);
    assert.equal(state.retrying, true);
    assert.equal(state.messageType, 'error');
    assert.match(state.message, /Retrying automatically/);
});

test('the reason a status read failed reaches the operator', () => {
    // The original code swallowed this into console.error and showed a blank card.
    const state = decideWizardState({ stepOneDone: false, attempted: true, status: null, error: 'HTTP 503' });
    assert.match(state.message, /HTTP 503/);
});

test('recovering from a failed read produces an empty message so the error can be cleared', () => {
    // The applier writes the status line on every change, including a change to nothing. A recovered
    // state that carried its old text would leave the page reporting a problem it no longer has.
    const failed = decideWizardState({ attempted: true, status: null, error: 'HTTP 503' });
    const recovered = decideWizardState({ attempted: true, status: status(reachable(false)) });

    assert.notEqual(failed.message, '');
    assert.equal(recovered.message, '');
    assert.equal(recovered.messageType, '');
});

// ── Storage says no ─────────────────────────────────────────────────────────────────────────────

test('an unreachable store surfaces its error and still allows the attempt', () => {
    const state = decideWizardState({ status: status(unreachable('AuthenticationFailed')) });

    assert.equal(state.seedEnabled, true, 'the seed attempt is what produces the real diagnostic');
    assert.equal(state.authEnabled, false);
    assert.equal(state.messageType, 'error');
    assert.match(state.message, /AuthenticationFailed/);
});

test('an unreachable store with no error string still produces a message', () => {
    const state = decideWizardState({ status: status({ connected: false, hasUsers: false }) });
    assert.match(state.message, /Unknown error/);
});

// ── Normal first run ────────────────────────────────────────────────────────────────────────────

test('reachable store with an empty table is the quiet first-run state', () => {
    const state = decideWizardState({ stepOneDone: false, status: status(reachable(false)) });

    assert.equal(state.seedEnabled, true);
    assert.equal(state.userSectionDone, false);
    assert.equal(state.authEnabled, false, 'auth stays locked until a superadmin exists');
    assert.equal(state.message, '', 'nothing has gone wrong, so say nothing');
    assert.equal(state.retrying, false);
});

// ── Step 1 already satisfied ────────────────────────────────────────────────────────────────────
// The second half of the report: with step 1 done, step 2 stayed greyed out.

test('an existing user completes step 1 and unlocks the auth sections', () => {
    const state = decideWizardState({ stepOneDone: false, status: status(reachable(true)) });

    assert.equal(state.authEnabled, true, 'step 2 must ungrey once a superadmin exists');
    assert.equal(state.userSectionDone, true);
    assert.equal(state.seedEnabled, false);
    assert.equal(state.messageType, 'success');
});

test('a completed step 1 is sticky and survives a later status failure', () => {
    // A poll that fails mid device-code flow must not re-lock the section the operator is using.
    const state = decideWizardState({
        stepOneDone: true,
        stepOneMessage: 'Superadmin user admin@contoso.com added successfully.',
        status: null,
        error: 'HTTP 503',
    });

    assert.equal(state.authEnabled, true);
    assert.equal(state.userSectionDone, true);
    assert.equal(state.seedEnabled, false);
    assert.equal(state.messageType, 'success');
    assert.match(state.message, /admin@contoso\.com/);
});

test('a completed step 1 survives the store going unreachable', () => {
    const state = decideWizardState({
        stepOneDone: true,
        status: status(unreachable('connection reset')),
    });

    assert.equal(state.authEnabled, true);
    assert.equal(state.messageType, 'success');
});

test('a completed step 1 with no recorded message still reads as done', () => {
    const state = decideWizardState({ stepOneDone: true, status: null });
    assert.equal(state.message, 'Superadmin user added.');
});

// ── Invariants across the whole table ───────────────────────────────────────────────────────────

test('the auth sections are only ever unlocked on positive evidence of a superadmin', () => {
    const noEvidence = [
        { stepOneDone: false, attempted: false, status: null },
        { stepOneDone: false, attempted: true, status: null, error: 'HTTP 500' },
        { stepOneDone: false, status: status(unreachable('timeout')) },
        { stepOneDone: false, status: status(undefined) },
        { stepOneDone: false, status: status(reachable(false)) },
    ];

    for (const input of noEvidence) {
        assert.equal(decideWizardState(input).authEnabled, false, JSON.stringify(input));
    }
});

test('step 1 and step 2 are never both actionable at once', () => {
    const everyInput = [
        {},
        { stepOneDone: true },
        { attempted: false, status: null },
        { attempted: true, status: null, error: 'x' },
        { status: status(reachable(false)) },
        { status: status(reachable(true)) },
        { status: status(unreachable('x')) },
    ];

    for (const input of everyInput) {
        const state = decideWizardState(input);
        assert.ok(!(state.seedEnabled && state.authEnabled), JSON.stringify(input));
    }
});

test('every branch returns the full shape the DOM applier reads', () => {
    const everyInput = [
        {},
        { stepOneDone: true },
        { attempted: false, status: null },
        { attempted: true, status: null, error: 'x' },
        { status: status(reachable(false)) },
        { status: status(reachable(true)) },
        { status: status(unreachable('x')) },
    ];

    for (const input of everyInput) {
        const state = decideWizardState(input);
        for (const key of ['seedEnabled', 'userSectionDone', 'authEnabled', 'retrying']) {
            assert.equal(typeof state[key], 'boolean', `${key} missing for ${JSON.stringify(input)}`);
        }
        assert.equal(typeof state.message, 'string');
        assert.equal(typeof state.messageType, 'string');
    }
});

// ── Reading the status response ─────────────────────────────────────────────────────────────────
// How the wizard decides a status response is unusable. Checking res.ok alone is not enough, and
// that is not a theoretical point: /api/setup/status shipped bound to MapGet's RequestDelegate
// overload, which computed the payload and discarded it. The wire result was a 200 with no
// Content-Type and no body, so res.ok was true and res.json() threw on empty input — straight into
// the silent catch that disabled the page.

test('an empty 200 with no content type is rejected, not parsed', () => {
    assert.equal(statusReadError(true, 200, null), 'unexpected non-JSON response');
    assert.equal(statusReadError(true, 200, ''), 'unexpected non-JSON response');
});

test('an HTML holding page served in place of the API is rejected', () => {
    // A proxy or a startup gate answering with the loading page.
    assert.equal(statusReadError(true, 200, 'text/html; charset=utf-8'), 'unexpected non-JSON response');
});

test('an error status is reported with its code', () => {
    assert.equal(statusReadError(false, 503, 'application/json'), 'HTTP 503');
    assert.equal(statusReadError(false, 500, null), 'HTTP 500');
});

test('a real JSON 200 is accepted', () => {
    assert.equal(statusReadError(true, 200, 'application/json; charset=utf-8'), null);
});

// ── Markup guards ───────────────────────────────────────────────────────────────────────────────
// The branch table is only half the fix; the other half is that the page must not contradict it.

test('the seed button does not ship disabled in the markup', () => {
    // It used to, which is why a failed status read produced a permanently dead page.
    const button = page.match(/<button[^>]*id="btn-seed"[^>]*>/);
    assert.ok(button, 'btn-seed not found');
    assert.doesNotMatch(button[0], /\bdisabled\b/);
});

test('the auth sections start locked in the markup', () => {
    // Complementary to the above: these are the ones that must be earned.
    for (const id of ['auto-section', 'manual-section']) {
        const section = page.match(new RegExp(`<div[^>]*id="${id}"[^>]*>`));
        assert.ok(section, `${id} not found`);
        assert.match(section[0], /disabled-section/);
    }
});

test('the status read is retried rather than attempted once', () => {
    assert.match(page, /setInterval\(refreshStatus/, 'refreshStatus must be on a timer');
});
