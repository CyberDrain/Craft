namespace Craft.Services;

/// <summary>
/// Embedded HTML pages for the first-run setup wizard.
/// Uses device code flow for authentication (no redirect URI required).
/// </summary>
public static class SetupPages
{
    public const string IndexHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Craft Setup</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: #0f172a; color: #e2e8f0; min-height: 100vh;
            display: flex; align-items: center; justify-content: center;
        }
        .container { max-width: 640px; width: 100%; padding: 2rem; }
        h1 { font-size: 1.75rem; margin-bottom: 0.5rem; color: #f8fafc; }
        .subtitle { color: #94a3b8; margin-bottom: 2rem; }
        .card {
            background: #1e293b; border: 1px solid #334155; border-radius: 0.75rem;
            padding: 1.5rem; margin-bottom: 1rem;
        }
        .card h2 { font-size: 1.1rem; margin-bottom: 0.5rem; color: #f1f5f9; }
        .card p { color: #94a3b8; font-size: 0.9rem; margin-bottom: 1rem; }
        button, .btn {
            display: inline-block; padding: 0.6rem 1.25rem; border: none;
            border-radius: 0.5rem; font-size: 0.9rem; font-weight: 600;
            cursor: pointer; text-decoration: none; transition: background 0.15s;
        }
        .btn-primary { background: #3b82f6; color: #fff; }
        .btn-primary:hover { background: #2563eb; }
        .btn-primary:disabled { background: #475569; cursor: not-allowed; }
        .btn-secondary { background: #334155; color: #e2e8f0; }
        .btn-secondary:hover { background: #475569; }
        input[type="text"], input[type="password"] {
            width: 100%; padding: 0.5rem 0.75rem; border: 1px solid #475569;
            border-radius: 0.375rem; background: #0f172a; color: #e2e8f0;
            font-size: 0.9rem; margin-bottom: 0.75rem;
        }
        input:focus { outline: none; border-color: #3b82f6; }
        label { display: block; font-size: 0.85rem; color: #94a3b8; margin-bottom: 0.25rem; }
        .status { margin-top: 1rem; padding: 0.75rem; border-radius: 0.5rem; font-size: 0.85rem; display: none; }
        .status.info { background: #1e3a5f; border: 1px solid #2563eb; color: #93c5fd; display: block; }
        .status.success { background: #14532d; border: 1px solid #16a34a; color: #86efac; display: block; }
        .status.error { background: #450a0a; border: 1px solid #dc2626; color: #fca5a5; display: block; }
        .steps { list-style: none; margin: 1rem 0; }
        .steps li { padding: 0.25rem 0; color: #94a3b8; font-size: 0.85rem; }
        .steps li.done { color: #86efac; }
        .steps li.active { color: #93c5fd; }
        .steps li.error { color: #fca5a5; }
        .steps li::before { content: '○ '; }
        .steps li.done::before { content: '● '; }
        .steps li.active::before { content: '◐ '; }
        .steps li.error::before { content: '✕ '; }
        .divider { border-top: 1px solid #334155; margin: 1.5rem 0; }
        .hidden { display: none !important; }
        .toggle-group {
            display: flex; gap: 0.5rem; margin-bottom: 1rem;
        }
        .toggle-group button {
            flex: 1; padding: 0.5rem; border: 1px solid #475569;
            border-radius: 0.375rem; background: #0f172a; color: #94a3b8;
            font-size: 0.85rem; cursor: pointer; transition: all 0.15s;
        }
        .toggle-group button.active {
            background: #1e3a5f; border-color: #3b82f6; color: #93c5fd;
        }
        .toggle-hint {
            font-size: 0.8rem; color: #64748b; margin-bottom: 1rem;
        }
        .info-banner {
            background: #1e3a5f; border: 1px solid #2563eb; border-radius: 0.5rem;
            padding: 0.75rem 1rem; margin-bottom: 1.5rem; font-size: 0.85rem; color: #93c5fd;
        }
        .device-code-box {
            background: #0f172a; border: 2px solid #3b82f6; border-radius: 0.5rem;
            padding: 1.25rem; text-align: center; margin: 1rem 0;
        }
        .device-code-box .code {
            font-size: 2rem; font-weight: 700; letter-spacing: 0.15em;
            color: #f8fafc; font-family: 'Consolas', 'Monaco', monospace;
        }
        .device-code-box .hint {
            font-size: 0.85rem; color: #94a3b8; margin-top: 0.5rem;
        }
        .device-code-box a { color: #60a5fa; text-decoration: underline; }
        .device-code-copy {
            display: inline-flex; align-items: center; gap: 0.5rem;
            background: #1e293b; border: 1px solid #334155; border-radius: 0.375rem;
            padding: 0.25rem 0.75rem; margin-top: 0.5rem; cursor: pointer;
        }
        .device-code-copy:hover { border-color: #3b82f6; }
        .device-code-copy .copy-btn {
            background: none; border: none; color: #60a5fa; cursor: pointer;
            font-size: 0.8rem; padding: 0.25rem 0.5rem;
        }
        .device-code-copy .copy-btn:hover { color: #93c5fd; }
        .disabled-section { opacity: 0.4; pointer-events: none; }
    </style>
</head>
<body>
<div class="container">
    <h1 id="page-title">Setup</h1>
    <p class="subtitle">Configure authentication to get started.</p>

    <div id="status-banner" class="info-banner hidden"></div>

    <!-- Step 1: First User -->
    <div class="card" id="user-section">
        <h2>Step 1: First User</h2>
        <p>Add the first superadmin user before configuring authentication. This user will have full access to the application.</p>
        <label for="seed-upn">User Principal Name (email)</label>
        <input type="text" id="seed-upn" placeholder="admin@contoso.com">
        <button class="btn btn-primary" id="btn-seed" onclick="seedFirstUser()" disabled>Add Superadmin</button>
        <div id="seed-status" class="status"></div>
    </div>

    <div class="divider" id="divider-user"></div>

    <!-- Step 2: Automated Setup (Device Code Flow) -->
    <div class="card disabled-section" id="auto-section">
        <h2>Step 2a: Automated Setup</h2>
        <p>Sign in with a Global Administrator account to automatically create the EasyAuth app registration and configure this App Service.</p>

        <label style="margin-bottom: 0.25rem;">Tenant Access</label>
        <div class="toggle-group" id="auto-tenant-toggle">
            <button type="button" class="active" onclick="setTenantMode('auto', false)">Single Tenant</button>
            <button type="button" onclick="setTenantMode('auto', true)">Multi-Tenant</button>
        </div>
        <div class="toggle-hint" id="auto-tenant-hint">Only users from the tenant you sign in with can access this app.</div>

        <div id="device-code-display" class="hidden">
            <div class="device-code-box">
                <div>Go to <a id="device-link" href="https://microsoft.com/devicelogin" target="_blank">microsoft.com/devicelogin</a> and enter:</div>
                <div class="device-code-copy" onclick="copyDeviceCode()" title="Click to copy">
                    <span class="code" id="device-user-code"></span>
                    <button type="button" class="copy-btn" id="device-code-copy-btn">Copy</button>
                </div>
                <div class="hint">Waiting for you to sign in...</div>
            </div>
        </div>

        <ul class="steps" id="auto-steps">
            <li id="step-login">Sign in with Azure AD</li>
            <li id="step-app">Create app registration</li>
            <li id="step-policy">Check app management policies</li>
            <li id="step-secret">Create client secret</li>
            <li id="step-configure">Configure App Service</li>
        </ul>
        <button class="btn btn-primary" id="btn-auto" onclick="startAutomatedSetup()">Start Setup</button>
        <div id="auto-status" class="status"></div>
    </div>

    <div class="divider" id="divider-auth"></div>

    <!-- Step 2b: Manual Setup -->
    <div class="card disabled-section" id="manual-section">
        <h2>Step 2b: Manual Setup</h2>
        <p>If you already have an app registration, enter the details below.</p>
        <label style="margin-bottom: 0.25rem;">Tenant Access</label>
        <div class="toggle-group" id="manual-tenant-toggle">
            <button type="button" class="active" onclick="setTenantMode('manual', false)">Single Tenant</button>
            <button type="button" onclick="setTenantMode('manual', true)">Multi-Tenant</button>
        </div>
        <div class="toggle-hint" id="manual-tenant-hint">Only users from the specified tenant can access this app.</div>
        <details style="margin-bottom: 1rem;">
            <summary style="cursor: pointer; color: #60a5fa; font-size: 0.85rem;">How to create the app registration manually</summary>
            <ol style="margin: 0.75rem 0 0.5rem 1.25rem; font-size: 0.8rem; color: #94a3b8; line-height: 1.6;">
                <li>Go to <a href="https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade" target="_blank" style="color: #60a5fa;">Entra ID &gt; App registrations</a> &gt; New registration</li>
                <li>Name: <strong id="manual-app-name" style="color: #e2e8f0;">Craft-EasyAuth-App</strong></li>
                <li>Supported account types: <strong id="manual-account-type" style="color: #e2e8f0;">Accounts in this organizational directory only (Single tenant)</strong></li>
                <li>Redirect URI: <strong style="color: #e2e8f0;">Web</strong> &mdash; <code id="manual-redirect-uri" style="background: #0f172a; padding: 0.1rem 0.4rem; border-radius: 0.25rem; color: #f8fafc;">{your-app-url}/.auth/login/aad/callback</code></li>
                <li>After creation, go to <strong style="color: #e2e8f0;">Authentication</strong> &gt; enable <strong style="color: #e2e8f0;">ID tokens</strong> under Implicit grant</li>
                <li>Go to <strong style="color: #e2e8f0;">Certificates &amp; secrets</strong> &gt; New client secret &gt; copy the <strong style="color: #e2e8f0;">Value</strong> (not the ID)</li>
                <li>Copy the <strong style="color: #e2e8f0;">Application (client) ID</strong> and <strong style="color: #e2e8f0;">Directory (tenant) ID</strong> from the Overview page</li>
            </ol>
            <p style="font-size: 0.8rem; color: #94a3b8;">No API permissions are needed &mdash; this app is only used for EasyAuth sign-in, not for Graph API calls.</p>
        </details>
        <label for="manual-appid">Application (Client) ID</label>
        <input type="text" id="manual-appid" placeholder="00000000-0000-0000-0000-000000000000">
        <label for="manual-secret">Client Secret</label>
        <input type="password" id="manual-secret" placeholder="Client secret value">
        <label for="manual-tenant">Tenant ID</label>
        <input type="text" id="manual-tenant" placeholder="00000000-0000-0000-0000-000000000000">
        <button class="btn btn-secondary" id="btn-manual" onclick="submitManual()">Configure</button>
        <div id="manual-status" class="status"></div>
    </div>
</div>

<script>
    let setupState = {};
    let pollTimer = null;
    let autoMultiTenant = false;
    let manualMultiTenant = false;

    function setTenantMode(section, isMulti) {
        const toggle = document.getElementById(section + '-tenant-toggle');
        const hint = document.getElementById(section + '-tenant-hint');
        toggle.children[0].classList.toggle('active', !isMulti);
        toggle.children[1].classList.toggle('active', isMulti);
        if (section === 'auto') {
            autoMultiTenant = isMulti;
        } else {
            manualMultiTenant = isMulti;
        }
        hint.textContent = isMulti
            ? 'Users from any Microsoft Entra tenant can sign in to this app.'
            : 'Only users from ' + (section === 'auto' ? 'the tenant you sign in with' : 'the specified tenant') + ' can access this app.';
        if (section === 'manual') {
            document.getElementById('manual-account-type').textContent = isMulti
                ? 'Accounts in any organizational directory (Multitenant)'
                : 'Accounts in this organizational directory only (Single tenant)';
        }
    }

    (async function() {
        try {
            const res = await fetch('/api/setup/status');
            setupState = await res.json();
            document.getElementById('page-title').textContent = setupState.appName + ' Setup';
            if (setupState.authAppDisplayName) {
                document.getElementById('manual-app-name').textContent = setupState.authAppDisplayName;
            }
            document.getElementById('manual-redirect-uri').textContent = window.location.origin + '/.auth/login/aad/callback';
            if (setupState.isEasyAuthConfigured) {
                showBanner('Authentication is already configured. This page will redirect shortly.');
                return;
            }
            if (!setupState.isRunningInAppService || !setupState.hasManagedIdentity) {
                showBanner('Warning: No managed identity detected. ARM self-configuration may fail. Use manual setup instead.');
            }

            // Handle user table status
            const us = setupState.usersStatus;
            if (!us || !us.connected) {
                // Connection error — disable user section
                document.getElementById('btn-seed').disabled = true;
                showStatus('seed-status', 'Cannot connect to storage: ' + (us?.error || 'Unknown error'), 'error');
            } else if (us.hasUsers) {
                // Users already exist — skip to auth setup
                document.getElementById('user-section').classList.add('disabled-section');
                showStatus('seed-status', 'Users already exist in the table. Proceed to authentication setup below.', 'success');
                enableAuthSections();
            } else {
                // No users — enable the seed form, keep auth disabled
                document.getElementById('btn-seed').disabled = false;
            }
        } catch (e) {
            console.error('Failed to load status', e);
        }
    })();

    // Background poll — detect setup completion from any session
    let statusPollTimer = setInterval(async () => {
        try {
            const res = await fetch('/api/setup/status', { cache: 'no-store' });
            if (!res.ok) return;
            const status = await res.json();
            if (status.isSetupCompleted || status.isEasyAuthConfigured) {
                clearInterval(statusPollTimer);
                showRestartScreen();
            }
        } catch (e) {
            // Setup endpoint may be unavailable during restart — ignore
        }
    }, 5000);

    function enableAuthSections() {
        document.getElementById('auto-section').classList.remove('disabled-section');
        document.getElementById('manual-section').classList.remove('disabled-section');
    }

    async function seedFirstUser() {
        const upn = document.getElementById('seed-upn').value.trim();
        if (!upn) {
            showStatus('seed-status', 'Please enter a valid email address.', 'error');
            return;
        }

        const btn = document.getElementById('btn-seed');
        btn.disabled = true;
        btn.textContent = 'Adding user...';
        showStatus('seed-status', 'Adding superadmin user...', 'info');

        try {
            const res = await fetch('/api/setup/seed-user', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ upn })
            });

            const data = await res.json();
            if (!res.ok || !data.success) throw new Error(data.message || 'Failed to add user');

            showStatus('seed-status', data.message, 'success');
            document.getElementById('user-section').classList.add('disabled-section');
            enableAuthSections();
        } catch (e) {
            showStatus('seed-status', 'Error: ' + e.message, 'error');
            btn.disabled = false;
            btn.textContent = 'Add Superadmin';
        }
    }

    function showBanner(msg) {
        const el = document.getElementById('status-banner');
        el.textContent = msg;
        el.classList.remove('hidden');
    }

    function setStep(id, state) {
        document.getElementById(id).className = state;
    }

    function showStatus(elId, msg, type) {
        const el = document.getElementById(elId);
        el.textContent = msg;
        el.className = 'status ' + type;
    }

    async function copyDeviceCode() {
        const code = document.getElementById('device-user-code').textContent;
        if (!code) return;
        const btn = document.getElementById('device-code-copy-btn');
        try {
            await navigator.clipboard.writeText(code);
        } catch {
            // Clipboard API unavailable (e.g. non-HTTPS) — fall back to a text selection
            const range = document.createRange();
            range.selectNodeContents(document.getElementById('device-user-code'));
            const sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            document.execCommand('copy');
            sel.removeAllRanges();
        }
        btn.textContent = 'Copied!';
        setTimeout(() => { btn.textContent = 'Copy'; }, 2000);
    }

    async function startAutomatedSetup() {
        const btn = document.getElementById('btn-auto');
        btn.disabled = true;
        btn.textContent = 'Requesting code...';

        try {
            // Step 1: Start device code flow
            setStep('step-login', 'active');
            const dcRes = await fetch('/api/setup/device-code', { method: 'POST' });
            if (!dcRes.ok) throw new Error('Failed to start device code flow: ' + await dcRes.text());
            const dcData = await dcRes.json();

            // Show the device code to the user
            document.getElementById('device-user-code').textContent = dcData.userCode;
            document.getElementById('device-link').href = dcData.verificationUri;
            document.getElementById('device-code-display').classList.remove('hidden');
            btn.classList.add('hidden');

            showStatus('auto-status', dcData.message, 'info');

            // Poll for completion
            const interval = Math.max(dcData.interval || 5, 5) * 1000;
            const deviceCode = dcData.deviceCode;

            await pollForToken(deviceCode, interval);

        } catch (e) {
            showStatus('auto-status', 'Error: ' + e.message, 'error');
            for (const s of ['step-login', 'step-app', 'step-policy', 'step-secret', 'step-configure']) {
                if (document.getElementById(s).className === 'active') {
                    setStep(s, 'error');
                    break;
                }
            }
            btn.disabled = false;
            btn.textContent = 'Start Setup';
            btn.classList.remove('hidden');
        }
    }

    async function pollForToken(deviceCode, interval) {
        return new Promise((resolve, reject) => {
            const poll = async () => {
                try {
                    const res = await fetch('/api/setup/device-code-poll', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ deviceCode })
                    });

                    if (!res.ok) {
                        const err = await res.text();
                        reject(new Error(err));
                        return;
                    }

                    const data = await res.json();
                    if (data.pending) {
                        // Still waiting — poll again
                        setTimeout(poll, interval);
                        return;
                    }

                    // Got the token — proceed with setup
                    setStep('step-login', 'done');
                    document.getElementById('device-code-display').classList.add('hidden');

                    await completeSetup(data.accessToken, data.tenantId);
                    resolve();
                } catch (e) {
                    reject(e);
                }
            };
            poll();
        });
    }

    async function completeSetup(accessToken, tenantId) {
        // Step 2-4: Create app registration (includes policy + secret)
        setStep('step-app', 'active');
        showStatus('auto-status', 'Creating app registration... This may take a minute if policy exemptions are needed.', 'info');

        const appRes = await fetch('/api/setup/create-auth-app', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ accessToken, tenantId, redirectUri: window.location.origin, multiTenant: autoMultiTenant })
        });
        if (!appRes.ok) throw new Error('App creation failed: ' + await appRes.text());
        const appData = await appRes.json();
        setStep('step-app', 'done');
        setStep('step-policy', 'done');
        setStep('step-secret', 'done');

        // Step 5: Configure App Service
        setStep('step-configure', 'active');
        showStatus('auto-status', 'Configuring App Service authentication...', 'info');

        const configRes = await fetch('/api/setup/configure', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                appId: appData.appId,
                clientSecret: appData.clientSecret,
                tenantId: appData.tenantId,
                multiTenant: autoMultiTenant
            })
        });
        if (!configRes.ok) throw new Error('Configuration failed: ' + await configRes.text());
        setStep('step-configure', 'done');

        showStatus('auto-status', 'Setup complete! The application is restarting...', 'success');
        showRestartScreen();
    }

    async function submitManual() {
        const appId = document.getElementById('manual-appid').value.trim();
        const secret = document.getElementById('manual-secret').value.trim();
        const tenantId = document.getElementById('manual-tenant').value.trim();

        if (!appId || !secret || !tenantId) {
            showStatus('manual-status', 'All fields are required.', 'error');
            return;
        }

        const btn = document.getElementById('btn-manual');
        btn.disabled = true;
        btn.textContent = 'Configuring...';
        showStatus('manual-status', 'Configuring App Service...', 'info');

        try {
            const res = await fetch('/api/setup/manual', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ appId, clientSecret: secret, tenantId, multiTenant: manualMultiTenant })
            });

            if (!res.ok) throw new Error(await res.text());

            showStatus('manual-status', 'Configuration complete! The application is restarting...', 'success');
            showRestartScreen();
        } catch (e) {
            showStatus('manual-status', 'Failed: ' + e.message, 'error');
            btn.disabled = false;
            btn.textContent = 'Configure';
        }
    }
    function showRestartScreen() {
        // Stop background status polling
        clearInterval(statusPollTimer);
        // Hide setup sections, show restart status
        document.getElementById('user-section').classList.add('hidden');
        document.getElementById('auto-section').classList.add('hidden');
        document.getElementById('manual-section').classList.add('hidden');
        document.getElementById('divider-user').classList.add('hidden');
        document.getElementById('divider-auth').classList.add('hidden');
        document.querySelector('.subtitle').textContent = '';
        document.getElementById('page-title').textContent = 'Setup Complete';

        const banner = document.getElementById('status-banner');
        banner.innerHTML = '<div style="text-align:center;">' +
            '<div style="margin-bottom:0.75rem;">Authentication has been configured. The application is restarting to apply changes.</div>' +
            '<div id="restart-spinner" style="font-size:1.5rem;margin-bottom:0.5rem;">&#8987;</div>' +
            '<div id="restart-status" style="font-size:0.85rem;color:#94a3b8;">Waiting for the new container to come online...</div>' +
            '</div>';
        banner.classList.remove('hidden');

        // Poll /api/setup/status — once the new container comes online with EasyAuth
        // configured, isEasyAuthConfigured will be true and setup mode will no longer
        // be active. At that point redirect to / and let the app take over.
        const pollInterval = 5000;
        const pollRestart = async () => {
            try {
                const res = await fetch('/api/setup/status', { cache: 'no-store' });
                if (res.ok) {
                    const data = await res.json();
                    const headerRedirect = res.headers.get('Location');
                    const target = data.redirect || headerRedirect;
                    if (target || (data.isEasyAuthConfigured && !data.isSetupCompleted)) {
                        // New container is online with EasyAuth active — redirect to app
                        document.getElementById('restart-status').textContent = 'Application is ready!';
                        document.getElementById('restart-spinner').textContent = '\u2705';
                        setTimeout(() => window.location.href = target || '/', 1000);
                        return;
                    }
                    // Still on the old container (isSetupCompleted=true) — keep waiting
                    document.getElementById('restart-status').textContent = 'Auth configured, waiting for container restart...';
                }
            } catch (e) {
                // Container is cycling — expected during restart
                document.getElementById('restart-status').textContent = 'Container restarting...';
            }
            setTimeout(pollRestart, pollInterval);
        };

        setTimeout(pollRestart, 5000);
    }
</script>
</body>
</html>
""";

    public const string StartupHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Starting Up</title>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: #0f172a; color: #e2e8f0; min-height: 100vh;
            display: flex; align-items: center; justify-content: center;
        }
        .container { max-width: 480px; width: 100%; padding: 2rem; text-align: center; }
        h1 { font-size: 1.5rem; margin-bottom: 0.75rem; color: #f8fafc; }
        .spinner { font-size: 2rem; margin-bottom: 1rem; animation: pulse 1.5s ease-in-out infinite; }
        @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.4; } }
        .status { color: #94a3b8; font-size: 0.9rem; }
    </style>
</head>
<body>
<div class="container">
    <div class="spinner">&#9881;</div>
    <h1>Application Starting</h1>
    <div class="status" id="status">Initializing, please wait...</div>
</div>
<script>
    let attempts = 0;
    const maxAttempts = 240;
    const pollInterval = 3000;

    const poll = async () => {
        attempts++;
        const statusEl = document.getElementById('status');
        try {
            const res = await fetch('/api/setup/health', { cache: 'no-store' });
            if (res.ok) {
                const data = await res.json();
                if (data.ready) {
                    statusEl.textContent = 'Ready! Redirecting...';
                    setTimeout(() => window.location.reload(), 500);
                    return;
                }
            }
        } catch (e) {
            // Still starting
        }
        if (attempts >= maxAttempts) {
            statusEl.textContent = 'Startup is taking longer than expected. Try refreshing the page.';
            return;
        }
        statusEl.textContent = 'Initializing, please wait... (' + attempts + ')';
        setTimeout(poll, pollInterval);
    };

    setTimeout(poll, 2000);
</script>
</body>
</html>
""";
}
