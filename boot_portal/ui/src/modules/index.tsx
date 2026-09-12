import { FormEvent, useState } from "react";
import { dashboardApi } from "../api";
import { Card, EmptyState, HashValue, Metric, Progress, StatusDot } from "../components/Primitives";
import { MinerConnectionPanel } from "../components/MinerConnection";
import { formatAge, formatDate, formatPercent, formatSats, formatUncertainty } from "../format";
import type { DashboardModuleContext } from "./context";

export interface DashboardModule {
  id: string;
  scope: "public" | "operator";
  className: string;
  render: (context: DashboardModuleContext) => React.ReactNode;
}

function StatusModule({ summary }: DashboardModuleContext) {
  const { health, node } = summary;
  const headline = health.status === "ready"
    ? "Verifiable work. No pool wallet."
    : health.status === "unsafe"
      ? "Mining work is paused for safety."
      : "The node is operating with degraded signals.";
  return (
    <section className={`hero hero-${health.status}`}>
      <div className="hero-copy">
        <p className="eyebrow">GridPool V2.2 / live node</p>
        <h1>{headline}</h1>
        <p>
          A public ranked set of unpaid proof-of-work replaces the pool operator&apos;s
          private accounting spreadsheet.
        </p>
      </div>
      <div className="hero-status">
        <span className="hero-state">
          <StatusDot status={health.status} />
          {health.status}
        </span>
        <span>{node.displayName || node.role || "GridPool node"}</span>
        <span>{node.region || node.publicEndpoint || "sovereign endpoint"}</span>
      </div>
      {health.miningWorkSafetyReason ? (
        <div className="hero-alert">{health.miningWorkSafetyReason}</div>
      ) : null}
    </section>
  );
}

function SnapshotModule({ summary }: DashboardModuleContext) {
  const snapshot = summary.snapshot;
  return (
    <Card title="Active payout snapshot" eyebrow="Locked for current templates">
      <div className="metric-grid metric-grid-2">
        <Metric label="Payout positions" value={snapshot.lockedPayoutCount} detail="consensus outputs" />
        <Metric label="Proof lineage" value={snapshot.lockedProofCount} detail="paid once" />
      </div>
      <dl className="definition-list">
        <div>
          <dt>Snapshot</dt>
          <dd><HashValue value={snapshot.activeSnapshotId} /></dd>
        </div>
        <div>
          <dt>Family</dt>
          <dd><HashValue value={snapshot.activeSnapshotFamilyId} /></dd>
        </div>
        <div>
          <dt>Locked</dt>
          <dd>{formatDate(snapshot.lastRotationUtc)}</dd>
        </div>
      </dl>
      <p className="explain">
        This is the payout commitment miners are using now. V2.2 can merge compatible
        sibling reserves, but it does not rewrite this snapshot using later hashrate.
      </p>
    </Card>
  );
}

function ReserveModule({ summary }: DashboardModuleContext) {
  const snapshot = summary.snapshot;
  return (
    <Card title="Unpaid Work Set" eyebrow="Provisional / difficulty ranked">
      <div className="metric-grid metric-grid-2">
        <Metric
          label="Reserve"
          value={`${snapshot.reserveCount} / ${snapshot.reserveLimit}`}
          detail="best verified proofs"
        />
        <Metric
          label="Current floor"
          value={snapshot.reserveFloorDifficultyDisplay}
          detail="difficulty to displace"
        />
      </div>
      <Progress
        value={snapshot.reserveCount}
        maximum={snapshot.reserveLimit}
        label="Fixed-size reserve"
      />
      <p className="explain">
        These positions are not locked. Stronger valid work can push weaker proofs out
        before the next payout snapshot is formed.
      </p>
    </Card>
  );
}

function WorkRateModule({ summary, window, setWindow }: DashboardModuleContext) {
  const rate = summary.workRate;
  return (
    <Card
      title="Observed team work rate"
      eyebrow="Order statistic / non-consensus"
      action={
        <select
          aria-label="Work-rate window"
          value={window}
          onChange={(event) => setWindow(event.target.value as typeof window)}
        >
          <option value="6h">6 hours</option>
          <option value="24h">24 hours</option>
          <option value="7d">7 days</option>
        </select>
      }
    >
      <div className="work-rate-primary">
        <strong>{rate.estimateDisplay}</strong>
        <span className={`confidence confidence-${rate.confidence}`}>{rate.confidence}</span>
      </div>
      <div className="metric-grid metric-grid-3">
        <Metric label="Proofs sampled" value={rate.retainedOrderStatisticCount} />
        <Metric label="Boundary proof" value={rate.orderStatisticDifficultyDisplay} />
        <Metric label="Uncertainty" value={formatUncertainty(rate.relativeStandardErrorPercent)} />
      </div>
      {rate.warmup ? <div className="notice">Partial window: local telemetry is still warming up.</div> : null}
      <p className="explain">{rate.note}</p>
    </Card>
  );
}

function PulseModule({ summary }: DashboardModuleContext) {
  const pulse = summary.pulse;
  const tone = !pulse.enabled ? "neutral" : pulse.outboundRelayHealthy ? "good" : "warn";
  return (
    <Card title="Network pulse" eyebrow="Liveness, not hashrate">
      <div className="metric-grid metric-grid-2">
        <Metric
          label="Last pulse"
          value={formatAge(pulse.lastAcceptedUtc)}
          detail={`${pulse.acceptedInWindow} in selected window`}
          tone={tone}
        />
        <Metric
          label="Relay"
          value={pulse.outboundRelayHealthy ? "healthy" : "degraded"}
          detail={`TTL ${pulse.relayTtl} / target ${pulse.targetIntervalSeconds}s`}
          tone={tone}
        />
      </div>
      <p className="explain">{pulse.interpretation}</p>
    </Card>
  );
}

function MinerConnectionModule({ summary }: DashboardModuleContext) {
  return (
    <Card title="Connect a miner" eyebrow="Native Stratum V2">
      <MinerConnectionPanel summary={summary} />
    </Card>
  );
}

function AddressModule(context: DashboardModuleContext) {
  const [address, setAddress] = useState("");
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (address.trim()) void context.lookupAddress(address.trim());
  };
  const result = context.addressResult;
  return (
    <Card title="Find your work" eyebrow="Payout address lookup">
      <form className="address-search" onSubmit={submit}>
        <label htmlFor="address">Bitcoin payout address</label>
        <div>
          <input
            id="address"
            value={address}
            onChange={(event) => setAddress(event.target.value)}
            placeholder="bc1q…"
            autoComplete="off"
            spellCheck={false}
          />
          <button type="submit" disabled={context.addressLoading}>
            {context.addressLoading ? "Checking…" : "Locate"}
          </button>
        </div>
      </form>
      {context.addressError ? <div className="notice notice-bad">{context.addressError}</div> : null}
      {result ? (
        <div className="address-result">
          <p>{result.interpretation}</p>
          <div className="metric-grid metric-grid-3">
            <Metric label="Locked slots" value={result.lockedSlotCount} detail={`${formatSats(result.lockedValueSats)} sats`} />
            <Metric label="Reserve positions" value={result.provisionalPositionCount} detail={result.provisionalPositions.join(", ") || "--"} />
            <Metric
              label="Top-300 survival"
              value={formatPercent(result.estimatedTop300SurvivalProbability)}
              detail="statistical estimate"
            />
          </div>
        </div>
      ) : null}
    </Card>
  );
}

function NetworkModule({ summary }: DashboardModuleContext) {
  const { health, snapshot } = summary;
  const candidatePrepared = Boolean(snapshot.candidateStateId);
  return (
    <Card title="Network convergence" eyebrow="V2.2 merge-forward">
      <div className="metric-grid metric-grid-3">
        <Metric label="Observed peers" value={health.peerCount} />
        <Metric
          label="Candidate status"
          value={candidatePrepared ? "provisional" : "not prepared"}
          tone={candidatePrepared ? "neutral" : "warn"}
        />
        <Metric label="Snapshot siblings" value={snapshot.familyMemberCount || 1} />
      </div>
      <dl className="definition-list">
        <div><dt>Current state</dt><dd><HashValue value={snapshot.currentStateId} /></dd></div>
        <div><dt>Candidate state</dt><dd><HashValue value={snapshot.candidateStateId} /></dd></div>
        <div><dt>Bitcoin tip</dt><dd><HashValue value={health.currentTipBlockHash} /></dd></div>
      </dl>
      <p className="explain">
        Compatible fully validated sibling reserves merge monotonically within the
        same snapshot family. The current and candidate IDs normally differ; peer
        count and later hashrate do not elect a winner.
      </p>
    </Card>
  );
}

function HistoryModule({ history }: DashboardModuleContext) {
  const points = history?.points.filter((point) => point.workRateThs != null) ?? [];
  const width = 720;
  const height = 180;
  const maximum = Math.max(1, ...points.map((point) => point.workRateThs ?? 0));
  const path = points.map((point, index) => {
    const x = points.length <= 1 ? 0 : (index / (points.length - 1)) * width;
    const y = height - ((point.workRateThs ?? 0) / maximum) * (height - 12);
    return `${index === 0 ? "M" : "L"} ${x.toFixed(1)} ${y.toFixed(1)}`;
  }).join(" ");
  return (
    <Card title="Observation history" eyebrow="Local measurement window">
      {points.length > 1 ? (
        <div className="chart-wrap">
          <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Observed team work-rate history">
            <path className="chart-grid" d={`M 0 ${height - 1} L ${width} ${height - 1}`} />
            <path className="chart-line" d={path} />
          </svg>
          <div className="chart-axis">
            <span>{formatDate(points[0].timestampUtc)}</span>
            <span>{formatDate(points.at(-1)?.timestampUtc)}</span>
          </div>
        </div>
      ) : (
        <EmptyState>Collecting enough complete Work proofs to draw a trend.</EmptyState>
      )}
    </Card>
  );
}

function LocalMiningModule({ operator, requestOperatorUnlock }: DashboardModuleContext) {
  if (!operator) {
    return (
      <Card title="Local mining" eyebrow="Operator-only">
        <EmptyState>
          <p>Unlock this node to view connected adapters and source-reported hashrate.</p>
          <button type="button" onClick={requestOperatorUnlock}>Unlock operator view</button>
        </EmptyState>
      </Card>
    );
  }
  return (
    <Card title="Local mining" eyebrow="Source-reported telemetry">
      {operator.localMiningSources.length ? (
        <div className="source-list">
          {operator.localMiningSources.map((source) => (
            <article key={source.source}>
              <div>
                <strong>{source.displayName || source.source}</strong>
                <span>{source.activeMinerCount} active</span>
              </div>
              <div>
                <strong>{source.currentHashrateDisplay}</strong>
                <span>{formatAge(source.lastShareUtc)}</span>
              </div>
            </article>
          ))}
        </div>
      ) : (
        <EmptyState>No active local mining sources are reporting telemetry.</EmptyState>
      )}
    </Card>
  );
}

function WatchtowerModule({ summary }: DashboardModuleContext) {
  const evidence = [
    { label: "Mining safety", healthy: summary.health.miningWorkSafe },
    { label: "Peer loops", healthy: summary.health.peerLoopsHealthy },
    { label: "Outbound relay", healthy: summary.health.outboundRelayHealthy }
  ];
  return (
    <Card title="Protocol watch" eyebrow="Evidence-based signals">
      <div className="watch-list">
        {evidence.map((item) => (
          <div key={item.label}>
            <StatusDot status={item.healthy ? "ready" : "degraded"} />
            <span>{item.label}</span>
            <strong>{item.healthy ? "normal" : "inspect"}</strong>
          </div>
        ))}
      </div>
      <p className="explain">
        Censorship and block-withholding detectors are not enabled. Future modules
        will appear here only when the node can show their evidence and confidence.
      </p>
    </Card>
  );
}

function ProtocolModule({ summary }: DashboardModuleContext) {
  return (
    <Card title="Protocol inspector" eyebrow="Machine-verifiable state">
      <details>
        <summary>Identity and versions</summary>
        <dl className="definition-list">
          <div><dt>Node ID</dt><dd><HashValue value={summary.node.nodeId} /></dd></div>
          <div><dt>Network</dt><dd>{summary.node.networkId}</dd></div>
          <div><dt>Release</dt><dd>{summary.node.releaseVersion || "development"}</dd></div>
          <div><dt>Consensus</dt><dd>V{summary.node.consensusVersion}</dd></div>
          <div><dt>HTTP API</dt><dd>v{summary.node.httpApiVersion}</dd></div>
        </dl>
      </details>
      <details>
        <summary>Reconciliation counters</summary>
        <pre>{JSON.stringify(summary.snapshot.reconciliation, null, 2)}</pre>
      </details>
      <a className="text-link" href="/api/dashboard/v1/schema">Dashboard API schema</a>
      {summary.capabilities.legacyUiEnabled ? <a className="text-link" href="/legacy">Legacy dashboard</a> : null}
    </Card>
  );
}

function ConsoleModule(context: DashboardModuleContext) {
  const [command, setCommand] = useState("status");
  const [output, setOutput] = useState("Type help to list the curated read-only commands.");
  const [running, setRunning] = useState(false);
  const run = async (event: FormEvent) => {
    event.preventDefault();
    const [name, ...args] = command.trim().split(/\s+/);
    setRunning(true);
    try {
      let result: unknown;
      switch (name.toLowerCase()) {
        case "help":
          result = {
            commands: ["status", "tip", "snapshot", "reserve", "peers", "miners", "pulses", "latency", "versions", "connect", "export"]
          };
          break;
        case "status": result = context.summary.health; break;
        case "tip": result = {
          hash: context.summary.health.currentTipBlockHash,
          height: context.summary.health.currentTipBlockHeight,
          authority: context.summary.health.bitcoinAuthorityClass
        }; break;
        case "snapshot": result = context.summary.snapshot; break;
        case "reserve": result = {
          count: context.summary.snapshot.reserveCount,
          limit: context.summary.snapshot.reserveLimit,
          floor: context.summary.snapshot.reserveFloorDifficulty
        }; break;
        case "pulses": result = context.summary.pulse; break;
        case "versions": result = context.summary.node; break;
        case "peers":
          if (!context.operator) throw new Error("Unlock operator view before requesting peer details.");
          result = context.operator.peers;
          break;
        case "miners":
          if (!context.operator) throw new Error("Unlock operator view before requesting local mining details.");
          result = context.operator.localMiningSources;
          break;
        case "latency":
          if (!context.adminKey) throw new Error("Unlock operator view before requesting relay diagnostics.");
          result = await dashboardApi.raw("/api/network/peer-relay-latency?window=12h&limit=100", context.adminKey);
          break;
        case "connect":
          result = await dashboardApi.raw("/api/mining/connect-info");
          break;
        case "export": {
          const safeExport = {
            exportedAtUtc: new Date().toISOString(),
            summary: context.summary,
            history: context.history
          };
          const blob = new Blob([JSON.stringify(safeExport, null, 2)], { type: "application/json" });
          const url = URL.createObjectURL(blob);
          const link = document.createElement("a");
          link.href = url;
          link.download = `gridpool-dashboard-${Date.now()}.json`;
          link.click();
          URL.revokeObjectURL(url);
          result = { status: "exported", includesOperatorData: false };
          break;
        }
        default:
          throw new Error(`Unknown command '${name}'. Type help.`);
      }
      setOutput(JSON.stringify(result, null, 2));
    } catch (error) {
      setOutput(error instanceof Error ? `error: ${error.message}` : "error: command failed");
    } finally {
      setRunning(false);
    }
  };
  return (
    <Card title="Node console" eyebrow="Curated API / no shell">
      <form className="console-form" onSubmit={run}>
        <span aria-hidden="true">gp&gt;</span>
        <input
          aria-label="Dashboard command"
          value={command}
          onChange={(event) => setCommand(event.target.value)}
          spellCheck={false}
          autoComplete="off"
        />
        <button type="submit" disabled={running}>{running ? "…" : "Run"}</button>
      </form>
      <pre className="console-output" aria-live="polite">{output}</pre>
    </Card>
  );
}

export const dashboardModules: DashboardModule[] = [
  { id: "status", scope: "public", className: "module-full", render: (context) => <StatusModule {...context} /> },
  { id: "miner-connection", scope: "public", className: "module-full", render: (context) => <MinerConnectionModule {...context} /> },
  { id: "snapshot", scope: "public", className: "module-half", render: (context) => <SnapshotModule {...context} /> },
  { id: "reserve", scope: "public", className: "module-half", render: (context) => <ReserveModule {...context} /> },
  { id: "work-rate", scope: "public", className: "module-wide", render: (context) => <WorkRateModule {...context} /> },
  { id: "pulse", scope: "public", className: "module-narrow", render: (context) => <PulseModule {...context} /> },
  { id: "address", scope: "public", className: "module-full", render: (context) => <AddressModule {...context} /> },
  { id: "network", scope: "public", className: "module-half", render: (context) => <NetworkModule {...context} /> },
  { id: "local-mining", scope: "public", className: "module-half", render: (context) => <LocalMiningModule {...context} /> },
  { id: "history", scope: "public", className: "module-wide", render: (context) => <HistoryModule {...context} /> },
  { id: "watchtower", scope: "public", className: "module-narrow", render: (context) => <WatchtowerModule {...context} /> },
  { id: "protocol", scope: "public", className: "module-half", render: (context) => <ProtocolModule {...context} /> },
  { id: "console", scope: "public", className: "module-half", render: (context) => <ConsoleModule {...context} /> }
];
