import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";
import { useEffect, useEffectEvent, useState } from "react";
import { dashboardApi, redirectToSetupIfRequired } from "../api";
import type {
  DashboardChanged,
  DashboardState,
  WindowKey
} from "../types";
import { AsyncRefreshGate } from "./AsyncRefreshGate";

const initialState: DashboardState = {
  summary: null,
  history: null,
  operator: null,
  loading: true,
  stale: false,
  error: "",
  lastUpdatedUtc: null
};

export function useDashboard(windowKey: WindowKey, adminKey: string) {
  const [state, setState] = useState<DashboardState>(initialState);

  const refresh = useEffectEvent(async (includeHistory = false) => {
    try {
      const [summary, history, operator] = await Promise.all([
        dashboardApi.summary(windowKey),
        includeHistory ? dashboardApi.history(windowKey) : Promise.resolve(state.history),
        adminKey ? dashboardApi.operator(adminKey) : Promise.resolve(null)
      ]);
      setState((current) => ({
        ...current,
        summary,
        history,
        operator,
        loading: false,
        stale: false,
        error: "",
        lastUpdatedUtc: new Date().toISOString()
      }));
    } catch (error) {
      if (redirectToSetupIfRequired(error)) return;
      setState((current) => ({
        ...current,
        loading: false,
        stale: current.summary !== null,
        error: error instanceof Error ? error.message : "Dashboard data is unavailable."
      }));
    }
  });

  useEffect(() => {
    let disposed = false;
    const refreshGate = new AsyncRefreshGate(() => refresh(false), 5_000);
    const poll = window.setInterval(() => refreshGate.request(), 15_000);
    const connection = new HubConnectionBuilder()
      .withUrl("/dashboardHub")
      .withAutomaticReconnect([0, 1000, 3000, 10_000])
      .build();
    connection.on("DashboardChanged", (change: DashboardChanged) => {
      if (change.topics.length) refreshGate.request();
    });
    connection.onreconnecting(() => {
      setState((current) => ({ ...current, stale: current.summary !== null }));
    });
    connection.onreconnected(() => refreshGate.request(true));
    void refresh(true).then(() => {
      if (disposed) return;
      void connection.start()
        .catch(() => {
          setState((current) => ({ ...current, stale: current.summary !== null }));
        });
    });

    return () => {
      disposed = true;
      window.clearInterval(poll);
      refreshGate.dispose();
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop();
      }
    };
  }, [windowKey, adminKey]);

  return {
    ...state,
    refresh: () => refresh(true)
  };
}
