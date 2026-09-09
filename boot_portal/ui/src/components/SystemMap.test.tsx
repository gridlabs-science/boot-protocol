import { act, fireEvent, render, screen } from "@testing-library/react";
import { vi } from "vitest";
import { SystemMap } from "./SystemMap";
import { diagramFixture } from "../test/fixture";
import type { DiagramEvent } from "../types";

const proofEvent: DiagramEvent = {
  sequence: 91,
  timestampUtc: "2026-07-29T16:00:05Z",
  kind: "proof-admitted",
  sourceKind: "peer",
  sourceId: "",
  sourceVisualId: diagramFixture.peers[0].visualId,
  visualId: diagramFixture.peers[0].visualId,
  transport: "",
  proofId: "",
  address: "",
  difficulty: null,
  blockQuality: false,
  receivedUtc: "2026-07-29T16:00:05Z",
  validatedUtc: "2026-07-29T16:00:05Z",
  mutatedUtc: "2026-07-29T16:00:05Z",
  rank: 120,
  displacedProofId: "",
  displacedVisualId: "",
  connected: null,
  latencyMs: null,
  acceptedShareDelta: null,
  hashrateThs: null,
  blockHash: "",
  blockHeight: null,
  snapshotId: "",
  lockedProofIds: [],
  lockedVisualIds: []
};

describe("GridPool system map", () => {
  it("renders the exact rail and verified slot zero evidence", () => {
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked
      />
    );

    expect(container.querySelectorAll(".proof-tick")).toHaveLength(0);
    expect(container.querySelectorAll(".workset-skyline")).toHaveLength(1);
    expect(container.querySelector(".workset-skyline")?.getAttribute("points")?.split(" ")).toHaveLength(897);
    expect(container.querySelectorAll(".slot-zero-proof")).toHaveLength(1);
    expect(screen.getByText(/Slot 0 · tb1qhome/)).toBeInTheDocument();
    expect(container.querySelector(".rail-line")).toHaveAttribute("x1", "320");
    expect(container.querySelector(".rail-line")).toHaveAttribute("x2", "1159");
    expect(container.querySelector(".snapshot-line")).toHaveAttribute("x2", "1159");
    expect(container.querySelector(".prospective-boundary")).toHaveAttribute("x1", "600");
    expect(container.querySelector(".slot-zero-tick")).toHaveAttribute("x1", "320");
    expect(screen.getByText(/Slot 0 · tb1qhome/)).toHaveAttribute("transform", expect.stringContaining("rotate(22"));
  });

  it("keeps the Slot 0 legend at the rail origin while highlighting its honest proof ranks", () => {
    const shiftedSlotProofs = diagramFixture.workSet.map((proof, index) => ({
      ...proof,
      address: index === 100 ? diagramFixture.slotZero.address : "tb1qpeer"
    }));
    const { container } = render(
      <SystemMap
        diagram={{ ...diagramFixture, workSet: shiftedSlotProofs }}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    expect(container.querySelector(".slot-zero-tick")).toHaveAttribute("x1", "320");
    expect(container.querySelector(".slot-zero-proof")).not.toHaveAttribute("x1", "320");
  });

  it("navigates proof ranks as one keyboard composite", () => {
    render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked
      />
    );

    const rail = screen.getByRole("listbox");
    fireEvent.keyDown(rail, { key: "End" });
    expect(rail).toHaveAttribute("aria-activedescendant", "proof-proof-897");
    fireEvent.keyDown(rail, { key: "Home" });
    expect(rail).toHaveAttribute("aria-activedescendant", "proof-proof-1");
  });

  it("reveals learning labels only when help is active", () => {
    render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked
      />
    );

    expect(screen.queryByText("Bitcoin")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Help" }));
    expect(screen.getByText("Bitcoin")).toBeInTheDocument();
  });

  it("shows compact honest hashrate cues and public peer names without help", () => {
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    expect(screen.getByText("Dallas")).toBeInTheDocument();
    expect(screen.getByText("evomining.farted.net")).toBeInTheDocument();
    expect(screen.getByText(/1.2 PH\/s remote/)).toBeInTheDocument();
    expect(screen.getByText("1.2 PH/s local")).toBeInTheDocument();
    expect(screen.getByText("400 TH/s")).toBeInTheDocument();
    expect(screen.getByText(/730 EH\/s · 129 T diff/)).toBeInTheDocument();
    expect(container.querySelectorAll(".hashrate-arc")).toHaveLength(3);
    expect(container.querySelector(".target-aperture")).toBeInTheDocument();
    expect(screen.getByText(/1.2 PH\/s remote/)).toHaveAttribute("y", "175");
    expect(screen.getByText(/730 EH\/s · 129 T diff/)).toHaveAttribute("y", "175");
  });

  it("uses unmarked convergence points and renders journal motion itself", () => {
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={proofEvent}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    expect(container.querySelectorAll(".node-hit")).toHaveLength(3);
    expect(container.querySelector(".node-mark rect")).toBeNull();
    expect(container.querySelector(".event-marker")).toBeInTheDocument();
    expect(container.querySelector(".event-marker")).toHaveAttribute("data-route", "peer-grid-rail-rank");
    expect(container.querySelector(".event-marker")?.tagName).toBe("line");
    expect(container.querySelector("animateMotion")).toBeNull();
  });

  it("branches block-quality work to Bitcoin and relays it to other peers", () => {
    const blockDiagram = {
      ...diagramFixture,
      peers: [
        ...diagramFixture.peers,
        { ...diagramFixture.peers[0], visualId: "peer-detroit", nodeId: "detroit", latencyMs: 22 }
      ]
    };
    const { container } = render(
      <SystemMap
        diagram={blockDiagram}
        activeEvent={{ ...proofEvent, blockQuality: true, displacedVisualId: "evicted-proof" }}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    const routes = Array.from(container.querySelectorAll(".event-marker"), (marker) => marker.getAttribute("data-route"));
    expect(routes).toContain("peer-grid-rail-rank");
    expect(routes).toContain("peer-grid-bitcoin-block");
    expect(routes).toContain("peer-grid-peer-block");
    expect(routes).toContain("rail-evict");
    expect(container.querySelectorAll(".event-block-quality").length).toBeGreaterThan(1);
    expect(Array.from(container.querySelectorAll(".event-block-quality")).every((marker) => marker.tagName === "rect")).toBe(true);
    expect(container.querySelector('[data-route="peer-grid-rail-rank"]')?.tagName).toBe("line");
  });

  it("uses one latency scale so faster GridPool peers sit closer", () => {
    const { container } = render(
      <SystemMap
        diagram={{
          ...diagramFixture,
          peers: [
            { ...diagramFixture.peers[0], visualId: "fast", latencyMs: 10 },
            { ...diagramFixture.peers[0], visualId: "slow", nodeId: "slow", latencyMs: 100 }
          ]
        }}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    const links = Array.from(container.querySelectorAll(".peer-constellation .map-link"));
    const length = (link: Element) => Math.hypot(
      Number(link.getAttribute("x2")) - Number(link.getAttribute("x1")),
      Number(link.getAttribute("y2")) - Number(link.getAttribute("y1"))
    );
    expect(length(links[0])).toBeLessThan(length(links[1]));
  });

  it("uses the observer command line to focus the chase without changing node state", () => {
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        history={{
          schemaVersion: 1,
          window: "24h",
          generatedAtUtc: "2026-07-29T12:00:00Z",
          redacted: true,
          slotZeroAddress: "tb1qhome",
          bestDifficulty: 10_000,
          bestDifficultyDisplay: "10K",
          proofs: [{
            proofId: "local-proof",
            address: "tb1qhome",
            sourceKind: "miner",
            source: "",
            username: "",
            proofClass: "work",
            difficulty: 10_000,
            difficultyDisplay: "10K",
            timestampUtc: "2026-07-29T11:59:00Z",
            enteredWorkSet: true,
            blockQuality: false
          }]
        }}
        historyWindow="24h"
        onHistoryWindowChange={() => undefined}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    expect(container.querySelector(".difficulty-chase")).toBeNull();
    const input = screen.getByLabelText("System map command");
    fireEvent.change(input, { target: { value: "focus slot0" } });
    fireEvent.submit(input.closest("form")!);
    expect(container.querySelector(".difficulty-chase")).toBeInTheDocument();
    expect(screen.getByText(/focus slot0/)).toBeInTheDocument();
  });

  it("gives observer commands distinct focus, inspect, auto, and multiline-help behavior", () => {
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        history={null}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );
    const input = screen.getByLabelText("System map command");
    const run = (command: string) => {
      fireEvent.change(input, { target: { value: command } });
      fireEvent.submit(input.closest("form")!);
    };

    run("help");
    expect(screen.getByText(/observer commands/).textContent).toContain("\n");
    run("focus bitcoin");
    expect(screen.getByRole("complementary", { name: "Local Bitcoin node" })).toBeInTheDocument();
    expect(container.querySelector(".target-aperture")).toHaveClass("metric-focused");
    expect(container.querySelector(".difficulty-chase")).toBeInTheDocument();
    run("rail auto");
    expect(container.querySelector(".difficulty-chase")).toBeNull();
    run("inspect rank 25");
    expect(screen.getByText(/inspect rank 25/)).toBeInTheDocument();
    expect(container.querySelector(".difficulty-chase")).toBeNull();
    run("focus rank 25");
    expect(screen.getByText(/focus rank 25/)).toBeInTheDocument();
    expect(container.querySelector(".difficulty-chase")).toBeInTheDocument();
  });

  it("colors paid GridPool boundaries green and starts accounting after rail arrival", () => {
    vi.useFakeTimers();
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={{ ...proofEvent, kind: "boundary-validated", boundaryKind: "gridpool-paid" }}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );

    expect(container.querySelector(".event-gridpool-paid")).toBeInTheDocument();
    expect(container.querySelector(".paid-snapshot-drain")).toBeNull();
    act(() => vi.advanceTimersByTime(800));
    expect(container.querySelector(".paid-snapshot-drain")).toBeInTheDocument();
    vi.useRealTimers();
  });

  it("inverts the asymmetric rail geometry for nearest-rank hit testing", () => {
    const { container } = render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked={false}
      />
    );
    const hitArea = container.querySelector(".skyline-hit-area") as SVGRectElement;
    vi.spyOn(hitArea, "getBoundingClientRect").mockReturnValue({
      left: 0,
      right: 1000,
      top: 0,
      bottom: 100,
      width: 1000,
      height: 100,
      x: 0,
      y: 0,
      toJSON: () => ({})
    });

    fireEvent.click(hitArea, { clientX: 334 });

    expect(screen.getByRole("complementary", { name: /Work proof · rank 300/ })).toBeInTheDocument();
  });

  it("exports only the public Work Set and local-proof history fields", async () => {
    const createObjectUrl = vi.fn((_blob: Blob) => "blob:test");
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: createObjectUrl });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    render(
      <SystemMap
        diagram={diagramFixture}
        history={null}
        activeEvent={null}
        onEventComplete={() => undefined}
        operatorUnlocked
      />
    );

    const input = screen.getByLabelText("System map command");
    fireEvent.change(input, { target: { value: "export json" } });
    fireEvent.submit(input.closest("form")!);

    expect(createObjectUrl).toHaveBeenCalledOnce();
    const blob = createObjectUrl.mock.calls[0][0] as Blob;
    const contents = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(reader.error);
      reader.onload = () => resolve(String(reader.result ?? ""));
      reader.readAsText(blob);
    });
    expect(contents).toContain("\"workSet\"");
    expect(contents).not.toContain(diagramFixture.peers[0].endpoint);
    expect(contents).not.toContain(diagramFixture.miners[0].username);
    delete (URL as unknown as { createObjectURL?: unknown }).createObjectURL;
    delete (URL as unknown as { revokeObjectURL?: unknown }).revokeObjectURL;
  });

  it("settles motion immediately for reduced-motion observers", () => {
    vi.useFakeTimers();
    const originalMatchMedia = Object.getOwnPropertyDescriptor(window, "matchMedia");
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn((query: string) => ({
      matches: query.includes("prefers-reduced-motion"),
      media: query,
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn()
      }))
    });
    const complete = vi.fn();
    render(
      <SystemMap
        diagram={diagramFixture}
        activeEvent={proofEvent}
        onEventComplete={complete}
        operatorUnlocked={false}
      />
    );

    act(() => vi.advanceTimersByTime(180));
    expect(complete).toHaveBeenCalledOnce();
    if (originalMatchMedia) {
      Object.defineProperty(window, "matchMedia", originalMatchMedia);
    } else {
      delete (window as unknown as { matchMedia?: unknown }).matchMedia;
    }
    vi.useRealTimers();
  });
});
