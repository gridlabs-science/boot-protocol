# GridPool Regtest Release-Retest Lab

This guide builds a disposable two-node GridPool lab backed by one Bitcoin Core
regtest node. It covers setup, normal operation, lifecycle checks, convergence,
and evidence collection. It does not describe exploit traffic or offensive
testing.

## Safety Boundary

- Use a dedicated host, VM, or private CI runner.
- Keep the Docker network internal and bind observer ports to `127.0.0.1`.
- Do not use public GridPool peers or production credentials.
- Generate disposable RPC credentials, payout addresses, identities, and state.
- Record exact GridPool commits and Bitcoin Core image digests.
- Never copy identity or state files between GridPool nodes.

## 1. Pin The Candidate

Requirements: Docker Engine with Compose v2, Git, `curl`, `jq`, `openssl`, and
a verified Bitcoin Core release. Recommended host capacity is four CPU threads,
8 GB RAM, and 20 GB free.

```bash
export LAB_ROOT="$HOME/gridpool-release-lab"
export GRIDPOOL_REPO="$HOME/src/boot-protocol"
export CANDIDATE_COMMIT="REPLACE_WITH_IMMUTABLE_COMMIT"

mkdir -p "$LAB_ROOT/worktrees"
git -C "$GRIDPOOL_REPO" fetch --all --tags --prune
git -C "$GRIDPOOL_REPO" worktree add --detach \
  "$LAB_ROOT/worktrees/candidate" "$CANDIDATE_COMMIT"
git -C "$LAB_ROOT/worktrees/candidate" rev-parse HEAD
```

Do not move this worktree to another revision during the run.

## 2. Private Inputs

Place verified `bitcoind` and `bitcoin-cli` binaries under
`$LAB_ROOT/core-dist/bin/`, then create disposable inputs:

```bash
cd "$LAB_ROOT"
mkdir -p node-a node-b artifacts core-dist/bin
chmod 700 node-a node-b artifacts
umask 077
cat > .env <<EOF
GRIDPOOL_SOURCE=$LAB_ROOT/worktrees/candidate
GRIDPOOL_TAG=rt076-candidate
RPC_USER=gridpool_regtest
RPC_PASSWORD=$(openssl rand -hex 32)
LAB_NETWORK_ID=gridpool-regtest-$(openssl rand -hex 6)
EOF
chmod 600 .env
```

Create `Dockerfile.bitcoin`:

```dockerfile
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates libevent-2.1-7 && rm -rf /var/lib/apt/lists/*
COPY core-dist/bin/bitcoind /usr/local/bin/bitcoind
COPY core-dist/bin/bitcoin-cli /usr/local/bin/bitcoin-cli
RUN chmod 0755 /usr/local/bin/bitcoind /usr/local/bin/bitcoin-cli
ENTRYPOINT ["bitcoind"]
```

## 3. Compose Topology

Create `compose.yaml`:

```yaml
name: gridpool-release-lab
services:
  bitcoin:
    build:
      context: .
      dockerfile: Dockerfile.bitcoin
    command:
      - -regtest=1
      - -server=1
      - -printtoconsole=1
      - -fallbackfee=0.00001
      - -rpcbind=0.0.0.0
      - -rpcallowip=172.16.0.0/12
      - -rpcuser=${RPC_USER}
      - -rpcpassword=${RPC_PASSWORD}
      - -zmqpubhashblock=tcp://0.0.0.0:28332
      - -zmqpubrawblock=tcp://0.0.0.0:28333
    volumes: ["bitcoin-data:/root/.bitcoin"]
    networks: [lab]

  node-a:
    build:
      context: ${GRIDPOOL_SOURCE}
      dockerfile: Dockerfile
    image: gridpool-release-lab:${GRIDPOOL_TAG}
    environment:
      BOOT_PORTAL_CONFIG_PATH: /data/config.json
      BOOT_PORTAL_STATE_PATH: /data/pool_state.json
      GRIDPOOL_RELEASE_VERSION: release-lab-${GRIDPOOL_TAG}
    volumes: ["./node-a:/data"]
    ports: ["127.0.0.1:15001:5000"]
    networks: [lab]

  node-b:
    image: gridpool-release-lab:${GRIDPOOL_TAG}
    environment:
      BOOT_PORTAL_CONFIG_PATH: /data/config.json
      BOOT_PORTAL_STATE_PATH: /data/pool_state.json
      GRIDPOOL_RELEASE_VERSION: release-lab-${GRIDPOOL_TAG}
    volumes: ["./node-b:/data"]
    ports: ["127.0.0.1:15002:5000"]
    networks: [lab]

networks:
  lab:
    internal: true
volumes:
  bitcoin-data:
```

RPC and ZMQ are reachable only inside the private Compose network.

## 4. Initialize Regtest

```bash
set -a; source "$LAB_ROOT/.env"; set +a
docker compose --env-file .env up -d --build bitcoin
until docker compose exec -T bitcoin bitcoin-cli \
  -regtest -rpcuser="$RPC_USER" -rpcpassword="$RPC_PASSWORD" \
  getblockchaininfo >/dev/null 2>&1; do sleep 1; done

docker compose exec -T bitcoin bitcoin-cli \
  -regtest -rpcuser="$RPC_USER" -rpcpassword="$RPC_PASSWORD" \
  -named createwallet wallet_name=lab load_on_startup=true

export LAB_PAYOUT_ADDRESS="$(docker compose exec -T bitcoin bitcoin-cli \
  -regtest -rpcuser="$RPC_USER" -rpcpassword="$RPC_PASSWORD" \
  -rpcwallet=lab getnewaddress "" bech32 | tr -d '\r')"
case "$LAB_PAYOUT_ADDRESS" in bcrt1*) ;; *) exit 1 ;; esac

docker compose exec -T bitcoin bitcoin-cli \
  -regtest -rpcuser="$RPC_USER" -rpcpassword="$RPC_PASSWORD" \
  -rpcwallet=lab generatetoaddress 101 "$LAB_PAYOUT_ADDRESS"
```

## 5. Node Configuration

Render a mode-600 `/data/config.json` for each node. Use the `.env` values for
credentials and network ID rather than literal placeholders:

```json
{
  "NotificationSource": "BitcoinZmq",
  "bitcoin_notification_mode": "attached-node",
  "bitcoin_rpc_url": "http://bitcoin:18443",
  "bitcoin_rpc_username": "FROM_ENV",
  "bitcoin_rpc_password": "FROM_ENV",
  "bitcoin_zmq_endpoint": "tcp://bitcoin:28332",
  "bitcoin_zmq_rawblock_endpoint": "tcp://bitcoin:28333",
  "bitcoin_network": "regtest",
  "boot_network_id": "UNIQUE_SHARED_LAB_ID",
  "boot_protocol_version": 22,
  "v22_activation_block_height": 0,
  "node_mode": "development",
  "pool_payout_script": "DISPOSABLE_BCRT1_ADDRESS",
  "grid_labs_support_fee_enabled": false,
  "enable_web_ui": false,
  "enable_admin_api": false,
  "enable_peer_sync": true,
  "peer_allow_private_advertisements": true,
  "enable_peer_persistent_sessions": true,
  "enable_peer_udp_fast_relay": false,
  "enable_peer_tip_stale_protection": false,
  "pause_mining_on_outbound_relay_stale": false,
  "min_diff": 1,
  "allow_empty_snapshot_bootstrap": true
}
```

Use `http://node-a:5000` and `http://node-b:5000` as their respective
`public_base_url` values. Configure `bootstrap_peers` with only the other node.
Set file ownership to the image user, currently UID/GID `1000:1000`.

`allow_empty_snapshot_bootstrap` is required only for the RT-2026-076
completeness regression. It is rejected outside non-production regtest and
must never appear in mainnet or appliance configuration.

## 6. Start And Baseline

```bash
docker compose --env-file .env build --no-cache node-a
docker compose --env-file .env up -d node-a node-b
until curl -fsS http://127.0.0.1:15001/api/network/summary >/dev/null; do sleep 1; done
until curl -fsS http://127.0.0.1:15002/api/network/summary >/dev/null; do sleep 1; done

for port in 15001 15002; do
  curl -fsS "http://127.0.0.1:${port}/api/network/summary" |
    jq '{nodeId,networkId,bitcoinNetwork,currentTipBlockHeight,
         currentTipBlockHash,currentStateId,candidateStateId,activeSnapshotId,
         peerCount,miningWorkSafe,bitcoinNotification}'
done
```

Expected baseline: `bitcoinNetwork=regtest`, synchronized RPC, both ZMQ topics
active, one private peer per node, and matching tip/state identifiers. Generate
one ordinary block and confirm both nodes advance exactly once:

```bash
docker compose exec -T bitcoin bitcoin-cli \
  -regtest -rpcuser="$RPC_USER" -rpcpassword="$RPC_PASSWORD" \
  -rpcwallet=lab generatetoaddress 1 "$LAB_PAYOUT_ADDRESS"
sleep 3
```

## 7. Defensive Release Checks

Using ordinary valid lab traffic, verify:

1. Share-first and notification-first valid block orderings each record one
   payment transition, never two.
2. Paid proof IDs are removed once and absent from later payout plans.
3. A node stopped across an ordinary boundary rejoins and converges from a
   complete proof-backed sibling bundle.
4. The explicit empty-bootstrap variant converges while enabled.
5. Restarting either node preserves identity, state, and paid lineage.
6. A one-block regtest reorganization follows the V2.2 rollback path and both
   nodes reconverge.

These are outcome checks, not instructions for malformed or hostile inputs.

## 8. Evidence And Reset

```bash
export RUN_DIR="$LAB_ROOT/artifacts/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$RUN_DIR"; chmod 700 "$RUN_DIR"
git -C "$GRIDPOOL_SOURCE" rev-parse HEAD > "$RUN_DIR/gridpool-commit.txt"
docker compose images --format json > "$RUN_DIR/images.json"
docker compose ps --format json > "$RUN_DIR/compose-ps.json"
docker compose logs --no-color > "$RUN_DIR/compose.log" 2>&1
curl -fsS http://127.0.0.1:15001/api/network/summary > "$RUN_DIR/node-a.json"
curl -fsS http://127.0.0.1:15002/api/network/summary > "$RUN_DIR/node-b.json"
sha256sum "$RUN_DIR"/* > "$RUN_DIR/SHA256SUMS"
```

Review artifacts for credentials before sharing. Reset only after preserving
evidence:

```bash
docker compose --env-file .env down -v --remove-orphans
rm -rf "$LAB_ROOT/node-a" "$LAB_ROOT/node-b"
mkdir -p "$LAB_ROOT/node-a" "$LAB_ROOT/node-b"
chmod 700 "$LAB_ROOT/node-a" "$LAB_ROOT/node-b"
```

Every candidate run must start from a documented clean state. Never reuse a
baseline state volume with a candidate image.
