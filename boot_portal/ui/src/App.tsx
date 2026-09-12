import { FormEvent, startTransition, useEffect, useState } from "react";
import { dashboardApi } from "./api";
import { StatusDot } from "./components/Primitives";
import { MinerConnectionPanel } from "./components/MinerConnection";
import { SystemMap } from "./components/SystemMap";
import { formatAge } from "./format";
import { useDashboard } from "./hooks/useDashboard";
import { useDiagram } from "./hooks/useDiagram";
import { dashboardModules } from "./modules";
import type { DashboardModuleContext } from "./modules/context";
import type { DashboardAddress, WindowKey } from "./types";

type Theme = "dark" | "light";

function initialTheme(): Theme {
  const stored = window.localStorage.getItem("gridpool-theme");
  return stored === "light" ? "light" : "dark";
}

export default function App() {
  return window.location.pathname.startsWith("/details") ? <DetailsApp /> : <MapApp />;
}

function MapApp() {
  const [theme, setTheme] = useState<Theme>(initialTheme);
  const [adminKey, setAdminKey] = useState("");
  const [adminDraft, setAdminDraft] = useState("");
  const [unlockOpen, setUnlockOpen] = useState(false);
  const [miningOpen, setMiningOpen] = useState(false);
  const live = useDiagram(adminKey);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    window.localStorage.setItem("gridpool-theme", theme);
  }, [theme]);

  const unlock = (event: FormEvent) => {
    event.preventDefault();
    setAdminKey(adminDraft);
    setAdminDraft("");
    setUnlockOpen(false);
  };

  if (live.loading && (!live.summary || !live.diagram)) {
    return (
      <main className="loading-screen">
        <div className="loading-mark" aria-hidden="true">GP</div>
        <p>Tracing the live node…</p>
      </main>
    );
  }

  if (!live.summary || !live.diagram) {
    return (
      <main className="fatal-screen">
        <p className="eyebrow">GridPool system map unavailable</p>
        <h1>The node did not return a coherent diagram.</h1>
        <p>{live.error || "Check the node service and try again."}</p>
        <button type="button" onClick={() => void live.refresh()}>Retry</button>
      </main>
    );
  }

  const summary = live.summary;
  const testnet = summary.node.bitcoinNetwork !== "mainnet";
  return (
    <div className={testnet ? "app map-app app-testnet" : "app map-app"}>
      <header className="truth-bar map-truth-bar">
        <a className="brand" href="/" aria-label="GridPool system map">
          <span className="brand-mark">GP</span>
          <span>GridPool</span>
        </a>
        <nav aria-label="Node truth">
          <span className={`truth-state truth-${summary.health.status}`}>
            <StatusDot status={summary.health.status} />
            {summary.health.status}
          </span>
          <span>tip {summary.health.currentTipBlockHeight ?? "--"}</span>
          <span>{summary.health.peerCount} peers</span>
          <span>{summary.workRate.estimateDisplay}</span>
        </nav>
        <div className="truth-actions">
          <span className={live.stale ? "freshness freshness-stale" : "freshness"}>
            {live.stale ? "reconnecting" : "live"}
          </span>
          <a className="details-link" href="/details">Details</a>
          {summary.mining?.nativeSv2.enabled ? (
            <button type="button" className="operator-button" onClick={() => setMiningOpen(true)}>
              Connect miner
            </button>
          ) : null}
          <button
            type="button"
            className="icon-button"
            aria-label={`Switch to ${theme === "dark" ? "light" : "dark"} theme`}
            onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
          >
            {theme === "dark" ? "○" : "●"}
          </button>
          {summary.capabilities.operatorApiAvailable ? (
            <button
              type="button"
              className={adminKey ? "operator-button operator-unlocked" : "operator-button"}
              onClick={() => adminKey ? setAdminKey("") : setUnlockOpen(true)}
            >
              {adminKey ? "Lock operator" : "Operator"}
            </button>
          ) : null}
        </div>
      </header>

      {live.error ? (
        <div className="global-notice" role="status">
          Showing the last coherent map: {live.error}
        </div>
      ) : null}

      <main className="map-shell">
        <SystemMap
          diagram={live.diagram}
          history={live.history}
          historyWindow={live.windowKey}
          onHistoryWindowChange={live.setWindowKey}
          activeEvent={live.activeEvent}
          onEventComplete={live.acknowledgeEvent}
          operatorUnlocked={Boolean(adminKey)}
        />
      </main>

      <footer className="map-footer">
        <p>Trust the proof, not the coordinator.</p>
        <div>
          <span>V{summary.node.consensusVersion}</span>
          <span>{summary.node.networkId}</span>
          <a href="/api/dashboard/v1/schema">API</a>
        </div>
      </footer>

      {unlockOpen ? (
        <OperatorUnlock
          adminDraft={adminDraft}
          setAdminDraft={setAdminDraft}
          close={() => setUnlockOpen(false)}
          unlock={unlock}
        />
      ) : null}
      {miningOpen ? (
        <div className="modal-backdrop" role="presentation" onMouseDown={() => setMiningOpen(false)}>
          <section
            className="modal connection-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="miner-connection-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <p className="eyebrow">Canonical miner transport</p>
            <h2 id="miner-connection-title">Connect a native SV2 miner</h2>
            <MinerConnectionPanel summary={summary} />
            <div className="modal-actions">
              <button type="button" onClick={() => setMiningOpen(false)}>Close</button>
            </div>
          </section>
        </div>
      ) : null}
    </div>
  );
}

function DetailsApp() {
  const [theme, setTheme] = useState<Theme>(initialTheme);
  const [windowKey, setWindowKey] = useState<WindowKey>("24h");
  const [adminKey, setAdminKey] = useState("");
  const [adminDraft, setAdminDraft] = useState("");
  const [unlockOpen, setUnlockOpen] = useState(false);
  const [addressResult, setAddressResult] = useState<DashboardAddress | null>(null);
  const [addressLoading, setAddressLoading] = useState(false);
  const [addressError, setAddressError] = useState("");
  const dashboard = useDashboard(windowKey, adminKey);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    window.localStorage.setItem("gridpool-theme", theme);
  }, [theme]);

  const lookupAddress = async (address: string) => {
    setAddressLoading(true);
    setAddressError("");
    try {
      setAddressResult(await dashboardApi.address(address));
    } catch (error) {
      setAddressResult(null);
      setAddressError(error instanceof Error ? error.message : "Address lookup failed.");
    } finally {
      setAddressLoading(false);
    }
  };

  const unlock = (event: FormEvent) => {
    event.preventDefault();
    setAdminKey(adminDraft);
    setAdminDraft("");
    setUnlockOpen(false);
  };

  if (dashboard.loading && !dashboard.summary) {
    return (
      <main className="loading-screen">
        <div className="loading-mark" aria-hidden="true">GP</div>
        <p>Verifying node state…</p>
      </main>
    );
  }

  if (!dashboard.summary) {
    return (
      <main className="fatal-screen">
        <p className="eyebrow">GridPool dashboard unavailable</p>
        <h1>The node API did not answer.</h1>
        <p>{dashboard.error || "Check the node service and try again."}</p>
        <button type="button" onClick={dashboard.refresh}>Retry</button>
      </main>
    );
  }

  const summary = dashboard.summary;
  const testnet = summary.node.bitcoinNetwork !== "mainnet";
  const context: DashboardModuleContext = {
    summary,
    history: dashboard.history,
    operator: dashboard.operator,
    adminKey,
    window: windowKey,
    setWindow: (value) => startTransition(() => setWindowKey(value)),
    requestOperatorUnlock: () => setUnlockOpen(true),
    addressResult,
    addressLoading,
    addressError,
    lookupAddress
  };

  return (
    <div className={testnet ? "app app-testnet" : "app"}>
      <header className="truth-bar">
        <a className="brand" href="/" aria-label="GridPool home">
          <span className="brand-mark">GP</span>
          <span>GridPool</span>
        </a>
        <nav aria-label="Node truth">
          <span className={`truth-state truth-${summary.health.status}`}>
            <StatusDot status={summary.health.status} />
            {summary.health.status}
          </span>
          <span className={testnet ? "network-tag network-testnet" : "network-tag"}>
            {summary.node.networkId}
          </span>
          <span>tip {summary.health.currentTipBlockHeight ?? "--"}</span>
          <span>{summary.health.peerCount} peers</span>
          <span>V{summary.node.consensusVersion}</span>
        </nav>
        <div className="truth-actions">
          <span className={dashboard.stale ? "freshness freshness-stale" : "freshness"}>
            {dashboard.stale ? "reconnecting" : `updated ${formatAge(dashboard.lastUpdatedUtc)}`}
          </span>
          <button
            type="button"
            className="icon-button"
            aria-label={`Switch to ${theme === "dark" ? "light" : "dark"} theme`}
            onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
          >
            {theme === "dark" ? "○" : "●"}
          </button>
          {summary.capabilities.operatorApiAvailable ? (
            <button
              type="button"
              className={adminKey ? "operator-button operator-unlocked" : "operator-button"}
              onClick={() => adminKey ? setAdminKey("") : setUnlockOpen(true)}
            >
              {adminKey ? "Lock operator" : "Operator"}
            </button>
          ) : null}
        </div>
      </header>

      {dashboard.error ? (
        <div className="global-notice" role="status">
          Showing the last good state: {dashboard.error}
        </div>
      ) : null}

      <main className="dashboard-shell">
        <div className="dashboard-grid">
          {dashboardModules.map((module) => (
            <div className={`module ${module.className}`} data-module={module.id} key={module.id}>
              {module.render(context)}
            </div>
          ))}
        </div>
      </main>

      <footer>
        <p>Trust the proof, not the coordinator.</p>
        <div>
          <a href="/api/dashboard/v1/schema">API</a>
          {summary.capabilities.legacyUiEnabled ? <a href="/legacy">Legacy UI</a> : null}
          <a href="https://github.com/gridlabs-science/boot-protocol">Source</a>
        </div>
      </footer>

      {unlockOpen ? (
        <div className="modal-backdrop" role="presentation" onMouseDown={() => setUnlockOpen(false)}>
          <section
            className="modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="operator-title"
            onMouseDown={(event) => event.stopPropagation()}
          >
            <p className="eyebrow">Local operator access</p>
            <h2 id="operator-title">Unlock private diagnostics</h2>
            <p>
              The admin key is held in memory for this tab only. It is never placed
              in the URL, browser storage, logs, or exports.
            </p>
            <form onSubmit={unlock}>
              <label htmlFor="admin-key">Admin API key</label>
              <input
                id="admin-key"
                type="password"
                value={adminDraft}
                onChange={(event) => setAdminDraft(event.target.value)}
                autoComplete="off"
                autoFocus
              />
              <div className="modal-actions">
                <button type="button" className="button-ghost" onClick={() => setUnlockOpen(false)}>Cancel</button>
                <button type="submit" disabled={!adminDraft}>Unlock</button>
              </div>
            </form>
          </section>
        </div>
      ) : null}
    </div>
  );
}

function OperatorUnlock({
  adminDraft,
  setAdminDraft,
  close,
  unlock
}: {
  adminDraft: string;
  setAdminDraft: (value: string) => void;
  close: () => void;
  unlock: (event: FormEvent) => void;
}) {
  return (
    <div className="modal-backdrop" role="presentation" onMouseDown={close}>
      <section
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="map-operator-title"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <p className="eyebrow">Local operator access</p>
        <h2 id="map-operator-title">Unlock live evidence</h2>
        <p>
          Reveal peer identities, local miners, payout addresses, and proof details.
          The key remains in memory for this tab only.
        </p>
        <form onSubmit={unlock}>
          <label htmlFor="map-admin-key">Admin API key</label>
          <input
            id="map-admin-key"
            type="password"
            value={adminDraft}
            onChange={(event) => setAdminDraft(event.target.value)}
            autoComplete="off"
            autoFocus
          />
          <div className="modal-actions">
            <button type="button" className="button-ghost" onClick={close}>Cancel</button>
            <button type="submit" disabled={!adminDraft}>Unlock</button>
          </div>
        </form>
      </section>
    </div>
  );
}
