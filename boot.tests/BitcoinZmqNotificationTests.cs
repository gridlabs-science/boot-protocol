using boot_portal.HostedServices;
using boot_portal.Models;
using boot_portal.Services;
using boot_portal.Utils;

namespace boot.tests;

[TestClass]
public sealed class BitcoinZmqNotificationTests
{
    [TestMethod]
    public void DuplicateHashAndRawBlockNotificationsAreAcceptedOnlyOnce()
    {
        var notifications = new RecentBitcoinBlockNotifications(TimeSpan.FromSeconds(30));
        DateTime receivedUtc = DateTime.UtcNow;

        Assert.IsTrue(notifications.TryAccept("block-a", receivedUtc));
        Assert.IsFalse(notifications.TryAccept("block-a", receivedUtc.AddMilliseconds(10)));
    }

    [TestMethod]
    public void InterleavedDuplicateNotificationsDoNotReplayEarlierBlocks()
    {
        var notifications = new RecentBitcoinBlockNotifications(TimeSpan.FromSeconds(30));
        DateTime receivedUtc = DateTime.UtcNow;

        Assert.IsTrue(notifications.TryAccept("block-a", receivedUtc));
        Assert.IsTrue(notifications.TryAccept("block-b", receivedUtc.AddMilliseconds(1)));
        Assert.IsFalse(notifications.TryAccept("block-a", receivedUtc.AddMilliseconds(2)));
        Assert.IsFalse(notifications.TryAccept("block-b", receivedUtc.AddMilliseconds(3)));
    }

    [TestMethod]
    public void HashCanBeAcceptedAgainAfterDuplicateWindow()
    {
        var notifications = new RecentBitcoinBlockNotifications(TimeSpan.FromSeconds(30));
        DateTime receivedUtc = DateTime.UtcNow;

        Assert.IsTrue(notifications.TryAccept("block-a", receivedUtc));
        Assert.IsTrue(notifications.TryAccept("block-a", receivedUtc.AddSeconds(31)));
    }

    [TestMethod]
    public void RawBlockParserReadsBip34CoinbaseHeight()
    {
        byte[] block = new byte[80 + 1 + 4 + 1 + 36 + 1 + 4 + 4 + 1 + 4];
        int offset = 80;
        block[offset++] = 1; // transaction count
        offset += 4; // transaction version
        block[offset++] = 1; // input count
        offset += 36; // null coinbase prevout
        block[offset++] = 4; // scriptSig length
        block[offset++] = 3; // push the three-byte height
        block[offset++] = 0xd3;
        block[offset++] = 0xa3;
        block[offset++] = 0x0e; // 959443, little-endian script number
        offset += 4; // sequence
        block[offset++] = 0; // output count
        offset += 4; // locktime

        Assert.IsTrue(BitcoinBlockParser.TryReadCoinbaseHeight(block, out long height));
        Assert.AreEqual(959443L, height);
    }

    [TestMethod]
    public void RawBlockParserRejectsMissingCoinbaseHeight()
    {
        Assert.IsFalse(BitcoinBlockParser.TryReadCoinbaseHeight(new byte[81], out _));
    }

    [TestMethod]
    public void SequenceTrackerClassifiesNormalGapDuplicateResetAndWrap()
    {
        var tracker = new BitcoinZmqSequenceTracker();

        Assert.AreEqual(BitcoinZmqSequenceObservation.First, tracker.Observe(10));
        Assert.AreEqual(BitcoinZmqSequenceObservation.Normal, tracker.Observe(11));
        Assert.AreEqual(BitcoinZmqSequenceObservation.Gap, tracker.Observe(14));
        Assert.AreEqual(2L, tracker.SequenceGapCount);
        Assert.AreEqual(BitcoinZmqSequenceObservation.Duplicate, tracker.Observe(14));
        Assert.AreEqual(1L, tracker.DuplicateCount);
        Assert.AreEqual(BitcoinZmqSequenceObservation.Reset, tracker.Observe(2));
        Assert.AreEqual(1L, tracker.ResetCount);

        var wrapping = new BitcoinZmqSequenceTracker();
        wrapping.Observe(uint.MaxValue);
        Assert.AreEqual(BitcoinZmqSequenceObservation.Wrap, wrapping.Observe(0));
        Assert.AreEqual(1L, wrapping.WrapCount);
    }

    [TestMethod]
    public void LegacyNotificationSourceMapsToNewModes()
    {
        Assert.AreEqual(
            BitcoinNotificationModes.AttachedNode,
            BitcoinNotificationModes.Resolve(new PoolConfig { NotificationSource = "BitcoinZmq" }));
        Assert.AreEqual(
            BitcoinNotificationModes.ExternalFallback,
            BitcoinNotificationModes.Resolve(new PoolConfig { NotificationSource = "MempoolSpace" }));
        Assert.AreEqual(
            BitcoinNotificationModes.ExternalFallback,
            BitcoinNotificationModes.Resolve(new PoolConfig
            {
                NotificationSource = "BitcoinZmq",
                BitcoinNotificationMode = BitcoinNotificationModes.ExternalFallback
            }));
    }

    [TestMethod]
    public void ExternalFallbackDoesNotRequireLocalRpc()
    {
        var health = new BitcoinNotificationHealth(new PoolConfig
        {
            BitcoinNotificationMode = BitcoinNotificationModes.ExternalFallback
        });

        Assert.IsTrue(health.IsMiningSafe(DateTime.UtcNow, out string reason));
        Assert.AreEqual(string.Empty, reason);
        Assert.AreEqual("external-observer", health.Snapshot(DateTime.UtcNow).AuthorityClass);
    }

    [TestMethod]
    public void AttachedNodeRequiresSynchronizedRpcAndRedactsErrors()
    {
        var health = new BitcoinNotificationHealth(new PoolConfig
        {
            BitcoinNotificationMode = BitcoinNotificationModes.AttachedNode,
            BitcoinRpcUrl = "http://bitcoin:8332",
            BitcoinRpcLagGraceSeconds = 1
        });
        DateTime nowUtc = DateTime.UtcNow.AddSeconds(2);
        health.RecordRpcFailure(
            "failed http://alice:secret@bitcoin:8332/wallet/private?token=secret",
            nowUtc.AddSeconds(-2));

        Assert.IsFalse(health.IsMiningSafe(nowUtc, out string reason));
        Assert.IsFalse(reason.Contains("secret", StringComparison.OrdinalIgnoreCase));

        health.RecordRpcSuccess(100, 101, "block-a", false, 0.99, nowUtc);
        Assert.IsFalse(health.IsMiningSafe(nowUtc, out _));

        health.RecordRpcSuccess(101, 101, "block-b", false, 1.0, nowUtc);
        Assert.IsTrue(health.IsMiningSafe(nowUtc, out _));
        BootBitcoinNotificationDto snapshot = health.Snapshot(nowUtc);
        Assert.IsTrue(snapshot.Rpc.Synced);
        Assert.AreEqual(101L, snapshot.Rpc.BestHeight);
    }

    [TestMethod]
    public void AttachedNodeReportsDuplicateZmqPublishersWithoutFailingReadiness()
    {
        var health = new BitcoinNotificationHealth(new PoolConfig
        {
            BitcoinNotificationMode = BitcoinNotificationModes.AttachedNode,
            BitcoinRpcUrl = "http://bitcoin:8332",
            BitcoinZmqEndpoint = "tcp://bitcoin:28332",
            BitcoinZmqRawBlockEndpoint = string.Empty
        });
        DateTime nowUtc = DateTime.UtcNow;
        health.RecordZmqSubscriberStarted("hashblock", "tcp://bitcoin:28332");
        health.RecordRpcSuccess(100, 100, "block-100", false, 1.0, nowUtc);
        health.RecordAdvertisedZmqPublishers(
        [
            new BitcoinZmqPublisher("pubhashblock", "tcp://0.0.0.0:28332"),
            new BitcoinZmqPublisher("pubhashblock", "tcp://0.0.0.0:29332")
        ]);

        BootBitcoinNotificationDto snapshot = health.Snapshot(nowUtc);

        Assert.IsTrue(snapshot.MiningSafe);
        Assert.IsTrue(snapshot.DegradedReason.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(2, snapshot.ZmqTopics.Single(topic => topic.Topic == "hashblock").PublisherCount);
    }

    [TestMethod]
    public void RepeatedRpcFailuresCannotRenewMiningSafetyGracePeriod()
    {
        var health = new BitcoinNotificationHealth(new PoolConfig
        {
            BitcoinNotificationMode = BitcoinNotificationModes.AttachedNode,
            BitcoinRpcUrl = "http://bitcoin:8332",
            BitcoinRpcLagGraceSeconds = 1
        });
        DateTime afterGrace = DateTime.UtcNow.AddSeconds(2);

        health.RecordRpcFailure("warmup", afterGrace.AddMilliseconds(-500));
        health.RecordRpcFailure("still warming up", afterGrace);

        Assert.IsFalse(health.IsMiningSafe(afterGrace, out string reason));
        StringAssert.Contains(reason, "unreachable");
    }
}
