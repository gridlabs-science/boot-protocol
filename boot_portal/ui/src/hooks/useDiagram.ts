import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { useCallback, useEffect, useEffectEvent, useRef, useState } from "react";
import { dashboardApi, redirectToSetupIfRequired } from "../api";
import type {
  DashboardChanged,
  DashboardDiagram,
  DashboardSummary,
  DiagramEvent,
  DiagramHistory
} from "../types";
import { AsyncRefreshGate } from "./AsyncRefreshGate";

const maximumQueuedEvents = 40;
const eventRefreshIntervalMs = 1_000;
const diagramRefreshIntervalMs = 2_000;
const summaryRefreshIntervalMs = 15_000;
const historyRefreshIntervalMs = 15_000;

const diagramTopics = new Set([
  "status", "snapshot", "reserve", "network", "pulse", "miners", "scenario", "timeline"
]);
const summaryTopics = new Set([
  "status", "snapshot", "reserve", "network", "pulse", "miners", "work-rate", "scenario", "timeline"
]);

export function useDiagram(adminKey: string) {
  const [summary, setSummary] = useState<DashboardSummary | null>(null);
  const [diagram, setDiagram] = useState<DashboardDiagram | null>(null);
  const [history, setHistory] = useState<DiagramHistory | null>(null);
  const [windowKey, setWindowKey] = useState<"24h" | "7d">("24h");
  const [events, setEvents] = useState<DiagramEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [stale, setStale] = useState(false);
  const [error, setError] = useState("");
  const cursor = useRef(0);
  const acknowledgeEvent = useCallback(
    () => setEvents((current) => current.slice(1)),
    []
  );

  const reportFailure = useEffectEvent((reason: unknown, fallback: string) => {
    if (redirectToSetupIfRequired(reason)) return;
    setLoading(false);
    setStale(diagram !== null);
    setError(reason instanceof Error ? reason.message : fallback);
  });

  const refreshDiagram = useEffectEvent(async () => {
    try {
      const snapshot = await dashboardApi.diagram(adminKey || undefined);
      setDiagram(snapshot);
      setLoading(false);
      setStale(false);
      setError("");
    } catch (reason) {
      reportFailure(reason, "Diagram data is unavailable.");
    }
  });

  const refreshHistory = useEffectEvent(async () => {
    try {
      setHistory(await dashboardApi.diagramHistory(windowKey, adminKey || undefined));
      setStale(false);
      setError("");
    } catch (reason) {
      reportFailure(reason, "Proof history is unavailable.");
    }
  });

  const refreshSummary = useEffectEvent(async () => {
    try {
      setSummary(await dashboardApi.summary("24h"));
      setLoading(false);
      setStale(false);
      setError("");
    } catch (reason) {
      reportFailure(reason, "Node summary is unavailable.");
    }
  });

  const refreshAll = useEffectEvent(async () => {
    try {
      const [snapshot, proofHistory, nodeSummary] = await Promise.all([
        dashboardApi.diagram(adminKey || undefined),
        dashboardApi.diagramHistory(windowKey, adminKey || undefined),
        dashboardApi.summary("24h")
      ]);
      cursor.current = snapshot.latestSequence;
      setDiagram(snapshot);
      setHistory(proofHistory);
      setSummary(nodeSummary);
      setLoading(false);
      setStale(false);
      setError("");
    } catch (reason) {
      reportFailure(reason, "System map data is unavailable.");
    }
  });

  const drainEvents = useEffectEvent(async () => {
    let pages = 0;
    let page = await dashboardApi.diagramEvents(cursor.current, adminKey || undefined);
    if (page.gap) {
      cursor.current = page.latestSequence;
      setEvents([]);
      return { diagramChanged: true, historyChanged: true };
    }
    const incoming: DiagramEvent[] = [];
    while (true) {
      incoming.push(...page.events);
      cursor.current = page.nextSequence;
      pages++;
      if (!page.hasMore || pages >= 4) break;
      page = await dashboardApi.diagramEvents(cursor.current, adminKey || undefined);
      if (page.gap) {
        cursor.current = page.latestSequence;
        setEvents([]);
        return { diagramChanged: true, historyChanged: true };
      }
    }
    if (page.hasMore) {
      cursor.current = page.latestSequence;
      setEvents([]);
      return { diagramChanged: true, historyChanged: true };
    }
    if (!incoming.length) {
      setStale(false);
      return { diagramChanged: false, historyChanged: false };
    }
    setEvents((current) => [...current, ...incoming].slice(-maximumQueuedEvents));
    const historyChanged = incoming.some((event) =>
      (event.kind === "proof-admitted" || event.kind === "pulse-accepted") &&
      event.sourceKind !== "peer");
    return { diagramChanged: true, historyChanged };
  });

  useEffect(() => {
    cursor.current = 0;
    setEvents([]);
    let disposed = false;

    const diagramGate = new AsyncRefreshGate(refreshDiagram, diagramRefreshIntervalMs);
    const summaryGate = new AsyncRefreshGate(refreshSummary, summaryRefreshIntervalMs);
    const historyGate = new AsyncRefreshGate(refreshHistory, historyRefreshIntervalMs);
    const eventGate = new AsyncRefreshGate(async () => {
      try {
        const result = await drainEvents();
        if (result.diagramChanged) diagramGate.request();
        if (result.historyChanged) historyGate.request();
        setStale(false);
        setError("");
      } catch (reason) {
        reportFailure(reason, "Live diagram events are unavailable.");
      }
    }, eventRefreshIntervalMs);

    const queueChange = (change: DashboardChanged) => {
      if (change.topics.includes("diagram")) eventGate.request();
      if (change.topics.some((topic) => diagramTopics.has(topic))) diagramGate.request();
      if (change.topics.some((topic) => summaryTopics.has(topic))) summaryGate.request();
    };

    const poll = window.setInterval(() => {
      eventGate.request();
      diagramGate.request();
      summaryGate.request();
    }, 15_000);
    const connection = new HubConnectionBuilder()
      .withUrl("/dashboardHub")
      .withAutomaticReconnect([0, 1000, 3000, 10_000])
      .build();
    connection.on("DashboardChanged", queueChange);
    connection.onreconnecting(() => setStale(diagram !== null));
    connection.onreconnected(() => {
      eventGate.request(true);
      diagramGate.request(true);
      summaryGate.request(true);
      historyGate.request(true);
    });

    void refreshAll().then(() => {
      if (disposed) return;
      void connection.start()
        .then(() => eventGate.request(true))
        .catch(() => setStale(diagram !== null));
    });

    return () => {
      disposed = true;
      window.clearInterval(poll);
      eventGate.dispose();
      diagramGate.dispose();
      summaryGate.dispose();
      historyGate.dispose();
      if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
    };
  }, [adminKey, windowKey]);

  return {
    summary,
    diagram,
    history,
    windowKey,
    setWindowKey,
    events,
    activeEvent: events[0] ?? null,
    acknowledgeEvent,
    loading,
    stale,
    error,
    refresh: refreshAll
  };
}
