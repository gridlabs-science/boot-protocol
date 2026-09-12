import { useState } from "react";
import type { DashboardSummary } from "../types";

const unavailableSv2 = {
  enabled: false,
  publicHost: "",
  publicPort: 34265,
  scheme: "stratum2+noise",
  usernameGuidance: "Use a Bitcoin payout address or worker label."
};

function connectionHost(configuredHost: string): string {
  if (configuredHost.trim()) return configuredHost.trim();
  const browserHost = window.location.hostname;
  return !browserHost || browserHost === "localhost" || browserHost === "127.0.0.1"
    ? "umbrel.local"
    : browserHost;
}

export function nativeSv2Url(summary: DashboardSummary): string {
  const sv2 = summary.mining?.nativeSv2 ?? unavailableSv2;
  return `${sv2.scheme}://${connectionHost(sv2.publicHost)}:${sv2.publicPort}`;
}

export function MinerConnectionPanel({ summary }: { summary: DashboardSummary }) {
  const [copied, setCopied] = useState(false);
  const sv2 = summary.mining?.nativeSv2 ?? unavailableSv2;
  const url = nativeSv2Url(summary);

  if (!sv2.enabled) {
    return <p className="explain">This node does not advertise a miner-facing Stratum V2 service.</p>;
  }

  const copy = async () => {
    await navigator.clipboard.writeText(url);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
  };

  return (
    <div className="miner-connection">
      <div className="connection-target">
        <span>Stratum V2 endpoint</span>
        <code>{url}</code>
        <button type="button" className="button-ghost" onClick={() => void copy()}>
          {copied ? "Copied" : "Copy"}
        </button>
      </div>
      <dl className="connection-fields">
        <div><dt>Host</dt><dd>{connectionHost(sv2.publicHost)}</dd></div>
        <div><dt>Port</dt><dd>{sv2.publicPort}</dd></div>
        <div><dt>Protocol</dt><dd>Native Stratum V2 with Noise</dd></div>
        <div><dt>Username</dt><dd>Your payout address, or a worker label</dd></div>
      </dl>
      <p className="explain">{sv2.usernameGuidance}</p>
      <p className="explain">
        The miner must be on a network that can reach this node. If the suggested
        hostname does not resolve from the miner, use the Umbrel device&apos;s LAN IP.
      </p>
      {!summary.health.miningWorkSafe ? (
        <p className="notice notice-bad">Mining work is currently paused: {summary.health.miningWorkSafetyReason}</p>
      ) : null}
    </div>
  );
}
