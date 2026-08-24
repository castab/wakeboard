import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";

interface WakeHost { id: string; name: string; macAddress: string; preferredInterfaceId: string; checkTarget: string }
interface PasskeyView { id: string; label: string; rpId: string; createdAt: string; lastUsedAt: string | null }
interface AdapterAddress { address: string; subnetMask: string; broadcastAddress: string }
interface NetworkAdapter { id: string; name: string; description: string; addresses: AdapterAddress[] }
interface LivenessResult { target: string; reachable: boolean; roundTripTimeMs: number | null; address: string | null; message: string; checkedAt: string }
interface WakeResult { packetsSent: number }
interface Draft { name: string; macAddress: string; preferredInterfaceId: string; checkTarget: string }
type HostStatus = { state: "checking" | "reachable" | "unreachable" | "error"; result?: LivenessResult; error?: string };
const blankDraft: Draft = { name: "", macAddress: "", preferredInterfaceId: "", checkTarget: "" };
const appVersion = import.meta.env.VITE_WAKEBOARD_VERSION || "dev";
const repositoryUrl = "https://github.com/castab/wakeboard";

function AppFooter() {
  return <footer>{appVersion === "dev" ? "dev" : `v${appVersion}`} ·
    <a href={repositoryUrl} target="_blank" rel="noreferrer noopener" title="Wakeboard on GitHub" aria-label="Wakeboard on GitHub">
      <svg viewBox="0 0 16 16" width="16" height="16" fill="currentColor" aria-hidden="true" focusable="false"><path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.012 8.012 0 0 0 16 8c0-4.42-3.58-8-8-8Z" /></svg>
    </a></footer>;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { "content-type": "application/json", ...init?.headers } });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(response.status === 401 ? "AUTH_REQUIRED" : payload.error || `Request failed (${response.status}).`);
  return payload as T;
}

function passkeySupported() {
  const ctor = (window as unknown as { PublicKeyCredential?: typeof PublicKeyCredential }).PublicKeyCredential;
  return typeof ctor?.parseRequestOptionsFromJSON === "function" && typeof ctor?.parseCreationOptionsFromJSON === "function";
}
function passkeyOriginEligible() {
  return window.isSecureContext && (location.hostname === "localhost" || location.protocol === "https:");
}
function humanizePasskeyError(reason: unknown): string {
  if (reason instanceof DOMException && reason.name === "NotAllowedError") return "Passkey action was cancelled or timed out.";
  return reason instanceof Error ? reason.message : "Passkey action failed.";
}

function Login({ onLogin, passkeyAvailable }: { onLogin: () => void; passkeyAvailable: boolean }) {
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [passkeyBusy, setPasskeyBusy] = useState(false);
  const [passkeyError, setPasskeyError] = useState("");
  async function submit(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError("");
    try { await request("/api/auth/login", { method: "POST", body: JSON.stringify({ password }) }); onLogin(); }
    catch (reason) { setError((reason as Error).message); }
    finally { setBusy(false); }
  }
  async function signInWithPasskey() {
    setPasskeyBusy(true); setPasskeyError("");
    try {
      const { options, state } = await request<{ options: PublicKeyCredentialRequestOptionsJSON; state: string }>("/api/auth/passkey/options", { method: "POST", body: "{}" });
      const credential = await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(options) }) as PublicKeyCredential | null;
      if (!credential) throw new Error("Passkey sign-in was cancelled.");
      await request("/api/auth/passkey/verify", { method: "POST", body: JSON.stringify({ state, assertion: credential.toJSON() }) });
      onLogin();
    } catch (reason) { setPasskeyError(humanizePasskeyError(reason)); }
    finally { setPasskeyBusy(false); }
  }
  const showPasskey = passkeyAvailable && passkeySupported();
  return <main className="login-shell"><section className="login-panel">
    <div className="brand-mark" aria-hidden="true">⌁</div><p className="eyebrow">Local network console</p><h1>Wakeboard</h1>
    <p className="lede">Sign in to wake and monitor devices on your network.</p>
    <form onSubmit={submit} className="login-form"><label htmlFor="password">Shared password</label>
      <input id="password" type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} required autoFocus />
      {error && <p className="form-error" role="alert">{error}</p>}<button className="button primary wide" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
    </form>
    {showPasskey && <div className="login-passkey">
      <div className="divider" role="separator"><span>or</span></div>
      <button type="button" className="button ghost wide" onClick={() => void signInWithPasskey()} disabled={passkeyBusy}>{passkeyBusy ? "Waiting for passkey…" : "Sign in with a passkey"}</button>
      {passkeyError && <p className="form-error" role="alert">{passkeyError}</p>}
    </div>}
  </section><AppFooter /></main>;
}

function Dashboard({ onLogout }: { onLogout: () => void }) {
  const [hosts, setHosts] = useState<WakeHost[]>([]);
  const [adapters, setAdapters] = useState<NetworkAdapter[]>([]);
  const [draft, setDraft] = useState<Draft>(blankDraft);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [wakeAdapters, setWakeAdapters] = useState<Record<string, string>>({});
  const [statuses, setStatuses] = useState<Record<string, HostStatus>>({});
  const [loading, setLoading] = useState(true);
  const [helperError, setHelperError] = useState("");
  const [notice, setNotice] = useState<{ kind: "success" | "error"; text: string } | null>(null);
  const [busyKey, setBusyKey] = useState("");
  const [passkeys, setPasskeys] = useState<PasskeyView[]>([]);
  const [passkeyLabel, setPasskeyLabel] = useState("");
  const [passkeyBusyKey, setPasskeyBusyKey] = useState("");
  const adapterMap = useMemo(() => new Map(adapters.map(adapter => [adapter.id, adapter])), [adapters]);
  const passkeyEligible = useMemo(() => passkeyOriginEligible() && passkeySupported(), []);

  const handleError = useCallback((reason: unknown) => {
    if ((reason as Error).message === "AUTH_REQUIRED") { onLogout(); return; }
    setNotice({ kind: "error", text: (reason as Error).message });
  }, [onLogout]);

  const checkHost = useCallback(async (host: WakeHost) => {
    if (!host.checkTarget) return;
    setStatuses(current => ({ ...current, [host.id]: { state: "checking" } }));
    try {
      const result = await request<LivenessResult>(`/api/hosts/${host.id}/status`, { method: "POST", body: "{}" });
      setStatuses(current => ({ ...current, [host.id]: { state: result.reachable ? "reachable" : "unreachable", result } }));
    } catch (reason) {
      if ((reason as Error).message === "AUTH_REQUIRED") onLogout();
      else setStatuses(current => ({ ...current, [host.id]: { state: "error", error: (reason as Error).message } }));
    }
  }, [onLogout]);

  const load = useCallback(async () => {
    setLoading(true);
    const [hostResult, adapterResult, passkeyResult] = await Promise.allSettled([
      request<WakeHost[]>("/api/hosts"), request<NetworkAdapter[]>("/api/interfaces"), request<PasskeyView[]>("/api/passkeys"),
    ]);
    if (hostResult.status === "fulfilled") { setHosts(hostResult.value); for (const host of hostResult.value) if (host.checkTarget) void checkHost(host); }
    else handleError(hostResult.reason);
    if (adapterResult.status === "fulfilled") { setAdapters(adapterResult.value); setHelperError(""); }
    else { setAdapters([]); setHelperError((adapterResult.reason as Error).message); }
    if (passkeyResult.status === "fulfilled") setPasskeys(passkeyResult.value);
    else handleError(passkeyResult.reason);
    setLoading(false);
  }, [checkHost, handleError]);

  useEffect(() => { const timer = window.setTimeout(() => void load(), 0); return () => window.clearTimeout(timer); }, [load]);

  function beginEdit(host: WakeHost) { setEditingId(host.id); setDraft({ name: host.name, macAddress: host.macAddress, preferredInterfaceId: host.preferredInterfaceId, checkTarget: host.checkTarget }); window.scrollTo({ top: 0, behavior: "smooth" }); }
  function cancelEdit() { setEditingId(null); setDraft(blankDraft); }
  async function saveHost(event: FormEvent) {
    event.preventDefault(); setBusyKey("save"); setNotice(null);
    try {
      await request(editingId ? `/api/hosts/${editingId}` : "/api/hosts", { method: editingId ? "PUT" : "POST", body: JSON.stringify(draft) });
      cancelEdit(); await load(); setNotice({ kind: "success", text: editingId ? "Device updated." : "Device added." });
    } catch (reason) { handleError(reason); } finally { setBusyKey(""); }
  }
  async function removeHost(host: WakeHost) {
    if (!confirm(`Remove ${host.name}? This cannot be undone.`)) return;
    setBusyKey(`delete:${host.id}`);
    try { await request(`/api/hosts/${host.id}`, { method: "DELETE" }); setHosts(items => items.filter(item => item.id !== host.id)); setNotice({ kind: "success", text: `${host.name} removed.` }); }
    catch (reason) { handleError(reason); } finally { setBusyKey(""); }
  }
  async function wake(host: WakeHost) {
    setBusyKey(`wake:${host.id}`); setNotice(null);
    try {
      const result = await request<WakeResult>(`/api/hosts/${host.id}/wake`, { method: "POST", body: JSON.stringify({ interfaceId: wakeAdapters[host.id] || host.preferredInterfaceId }) });
      setNotice({ kind: "success", text: `${result.packetsSent} wake packets sent for ${host.name}. This confirms transmission, not that the device started.` });
    } catch (reason) { handleError(reason); } finally { setBusyKey(""); }
  }
  async function logout() { try { await request("/api/auth/logout", { method: "POST", body: "{}" }); } finally { onLogout(); } }

  async function addPasskey() {
    setPasskeyBusyKey("add"); setNotice(null);
    try {
      const { options, state } = await request<{ options: PublicKeyCredentialCreationOptionsJSON; state: string }>("/api/passkeys/register/options", { method: "POST", body: "{}" });
      const credential = await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(options) }) as PublicKeyCredential | null;
      if (!credential) throw new Error("Passkey creation was cancelled.");
      const label = passkeyLabel.trim() || `Passkey added ${new Date().toLocaleDateString()}`;
      await request("/api/passkeys/register/verify", { method: "POST", body: JSON.stringify({ state, attestation: credential.toJSON(), label }) });
      setPasskeyLabel("");
      setPasskeys(await request<PasskeyView[]>("/api/passkeys"));
      setNotice({ kind: "success", text: "Passkey added." });
    } catch (reason) {
      if ((reason as Error).message === "AUTH_REQUIRED") onLogout();
      else setNotice({ kind: "error", text: humanizePasskeyError(reason) });
    } finally { setPasskeyBusyKey(""); }
  }
  async function removePasskey(passkey: PasskeyView) {
    if (!confirm(`Remove "${passkey.label}"? This cannot be undone.`)) return;
    setPasskeyBusyKey(`delete:${passkey.id}`);
    try {
      await request(`/api/passkeys/${encodeURIComponent(passkey.id)}`, { method: "DELETE" });
      setPasskeys(items => items.filter(item => item.id !== passkey.id));
      setNotice({ kind: "success", text: "Passkey removed." });
    } catch (reason) { handleError(reason); } finally { setPasskeyBusyKey(""); }
  }

  return <main className="app-shell">
    <header className="topbar"><div><p className="eyebrow">Native Windows console</p><h1>Wakeboard</h1></div><button className="button ghost" onClick={logout}>Sign out</button></header>
    {notice && <div className={`notice ${notice.kind}`} role="status">{notice.text}<button onClick={() => setNotice(null)} aria-label="Dismiss">×</button></div>}
    <section className="device-form-panel"><div className="section-heading"><div><p className="eyebrow">Configuration</p><h2>{editingId ? "Edit device" : "Add a device"}</h2></div>{editingId && <button className="button ghost" onClick={cancelEdit}>Cancel edit</button>}</div>
      <form className="device-form" onSubmit={saveHost}>
        <label>Device name<input value={draft.name} maxLength={100} onChange={event => setDraft({ ...draft, name: event.target.value })} placeholder="Office workstation" required /></label>
        <label>MAC address<input value={draft.macAddress} onChange={event => setDraft({ ...draft, macAddress: event.target.value })} placeholder="AA:BB:CC:DD:EE:FF" required /></label>
        <label>IP address or hostname <span className="optional">Optional</span><input value={draft.checkTarget} maxLength={253} onChange={event => setDraft({ ...draft, checkTarget: event.target.value })} placeholder="192.168.1.50" /></label>
        <label>Preferred adapter<select value={draft.preferredInterfaceId} onChange={event => setDraft({ ...draft, preferredInterfaceId: event.target.value })} required disabled={!adapters.length}><option value="">{adapters.length ? "Choose an adapter" : "No adapters available"}</option>{adapters.map(adapter => <option key={adapter.id} value={adapter.id}>{adapter.name} — {adapter.addresses.map(a => a.address).join(", ")}</option>)}{editingId && draft.preferredInterfaceId && !adapterMap.has(draft.preferredInterfaceId) && <option value={draft.preferredInterfaceId}>Saved adapter unavailable</option>}</select></label>
        <button className="button primary" disabled={busyKey === "save" || !adapters.length}>{busyKey === "save" ? "Saving…" : editingId ? "Save changes" : "Add device"}</button>
      </form>{helperError && <div className="helper-warning"><strong>Windows networking unavailable.</strong> {helperError} <button onClick={() => void load()}>Retry</button></div>}
    </section>
    <section className="devices-section"><div className="section-heading"><div><p className="eyebrow">Saved devices</p><h2>{hosts.length} {hosts.length === 1 ? "device" : "devices"}</h2></div><button className="button ghost" onClick={() => void load()} disabled={loading}>Refresh</button></div>
      {loading && !hosts.length ? <div className="empty-state">Loading devices…</div> : !hosts.length ? <div className="empty-state"><strong>No devices yet.</strong><span>Add the first device above to get started.</span></div> : <div className="device-grid">{hosts.map(host => {
        const selectedId = wakeAdapters[host.id] || host.preferredInterfaceId; const selected = adapterMap.get(selectedId); const status = statuses[host.id]; const responseTime = status?.result?.roundTripTimeMs;
        const statusLabel = !host.checkTarget ? "Not configured" : !status ? "Waiting to check" : status.state === "checking" ? "Checking…" : status.state === "reachable" ? `Awake${typeof responseTime === "number" ? ` · ${responseTime} ms` : ""}` : status.state === "unreachable" ? "No ping response" : "Check failed";
        return <article className="device-card" key={host.id}><div className="device-title"><div className="status-dot"/><div><h3>{host.name}</h3><code>{host.macAddress}</code></div></div>
          <div className="liveness-row" title={status?.result?.message || status?.error}><span className={`liveness-indicator ${status?.state || "unknown"}`} /><div><span>Reachability</span><strong>{statusLabel}</strong>{host.checkTarget && <small>{host.checkTarget}</small>}</div></div>
          <div className="saved-adapter"><span>Preferred adapter</span><strong>{adapterMap.get(host.preferredInterfaceId)?.name || <span className="missing">Adapter unavailable</span>}<small>{adapterMap.get(host.preferredInterfaceId)?.addresses.map(item => item.address).join(", ")}</small></strong></div>
          <label className="wake-select">Send using<select value={selectedId} onChange={event => setWakeAdapters({ ...wakeAdapters, [host.id]: event.target.value })} disabled={!adapters.length}>{!selected && <option value={selectedId}>Saved adapter unavailable</option>}{adapters.map(adapter => <option key={adapter.id} value={adapter.id}>{adapter.name}</option>)}</select></label>
          <div className="device-controls"><button className="button check" onClick={() => void checkHost(host)} disabled={!host.checkTarget || status?.state === "checking"}>{status?.state === "checking" ? "Checking…" : "Check now"}</button><button className="button wake" onClick={() => void wake(host)} disabled={!selected || Boolean(busyKey)}>{busyKey === `wake:${host.id}` ? "Sending…" : "Wake device"}</button></div>
          <div className="card-actions"><button onClick={() => beginEdit(host)}>Edit</button><button className="danger-link" onClick={() => void removeHost(host)} disabled={busyKey === `delete:${host.id}`}>Remove</button></div>
        </article>;
      })}</div>}
    </section>
    <section className="passkeys-section"><div className="section-heading"><div><p className="eyebrow">Security</p><h2>Passkeys</h2></div></div>
      {!passkeys.length ? <div className="empty-state"><strong>No passkeys yet.</strong><span>Add one below to sign in without typing the shared password.</span></div> : <ul className="passkey-list">{passkeys.map(passkey =>
        <li className="passkey-row" key={passkey.id}><div><strong>{passkey.label}</strong><small>{passkey.rpId} · added {new Date(passkey.createdAt).toLocaleDateString()}{passkey.lastUsedAt ? ` · last used ${new Date(passkey.lastUsedAt).toLocaleDateString()}` : ""}</small></div>
          <button className="danger-link" onClick={() => void removePasskey(passkey)} disabled={passkeyBusyKey === `delete:${passkey.id}`}>Remove</button>
        </li>)}</ul>}
      {passkeyEligible ? <div className="passkey-add">
        <label>Label <span className="optional">Optional</span><input value={passkeyLabel} maxLength={100} onChange={event => setPasskeyLabel(event.target.value)} placeholder={`Passkey added ${new Date().toLocaleDateString()}`} /></label>
        <button className="button" onClick={() => void addPasskey()} disabled={passkeyBusyKey === "add"}>{passkeyBusyKey === "add" ? "Adding…" : "Add a passkey"}</button>
      </div> : <div className="helper-warning"><strong>Passkeys aren't available here.</strong> Passkeys need HTTPS or http://localhost — you're on {location.hostname} right now. Open Wakeboard via the Tailscale HTTPS link or http://localhost to add one.</div>}
    </section><AppFooter />
  </main>;
}

export function App() {
  const [authenticated, setAuthenticated] = useState<boolean | null>(null);
  const [passkeyAvailable, setPasskeyAvailable] = useState(false);
  useEffect(() => {
    void request<{ authenticated: boolean; passkeyAvailable: boolean }>("/api/auth/session")
      .then(result => { setAuthenticated(result.authenticated); setPasskeyAvailable(result.passkeyAvailable); })
      .catch(() => setAuthenticated(false));
  }, []);
  if (authenticated === null) return <main className="splash"><div className="splash-body"><div className="brand-mark">⌁</div><span>Loading Wakeboard…</span></div><AppFooter /></main>;
  return authenticated ? <Dashboard onLogout={() => setAuthenticated(false)} /> : <Login onLogin={() => setAuthenticated(true)} passkeyAvailable={passkeyAvailable} />;
}
