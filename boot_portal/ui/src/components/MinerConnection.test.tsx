import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { MinerConnectionPanel, nativeSv2Url } from "./MinerConnection";
import { summaryFixture } from "../test/fixture";

describe("MinerConnectionPanel", () => {
  it("renders an explicit native SV2 target", () => {
    render(<MinerConnectionPanel summary={summaryFixture} />);

    expect(screen.getByText("stratum2+noise://node.gridpool.test:34265")).toBeInTheDocument();
    expect(screen.getByText("Your payout address, or a worker label")).toBeInTheDocument();
  });

  it("falls back to the dashboard host when no public host is configured", () => {
    const localSummary = structuredClone(summaryFixture);
    localSummary.mining!.nativeSv2.publicHost = "";

    expect(nativeSv2Url(localSummary)).toBe("stratum2+noise://umbrel.local:34265");
  });

  it("handles a cached response from a runtime without connection metadata", () => {
    const legacySummary = structuredClone(summaryFixture);
    delete legacySummary.mining;

    render(<MinerConnectionPanel summary={legacySummary} />);
    expect(screen.getByText(/does not advertise/i)).toBeInTheDocument();
  });
});
