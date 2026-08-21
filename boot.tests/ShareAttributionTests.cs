using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using boot_portal;
using boot_portal.Controllers;
using boot_portal.Models;
using boot_portal.Services;
using boot_portal.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;

namespace boot.tests;

[TestClass]
[DoNotParallelize]
public sealed class ShareAttributionTests
{
    private const string SampleSlotZeroAddress = "bc1qce93hy5rhg02s6aeu7mfdvxg76x66pqqtrvzs3";
    private const string AlternateAddress = "bc1qrwsx8fs0l6z7ugp5cvzy6lhss7jlyru3kg9s8y";
    private const string SamplePrevBlockHash = "00000000000000000002029d47c98d2ad5c020ce9a92af8ace14b882abfa1643";
    private const string OlderTipBlockHash = "0000000000000000000000000000000000000000000000000000000000012345";
    private const string SampleHeaderHex = "00804f274316faab82b814ce8aaf929ace20c0d52a8dc9479d02020000000000000000002e0c639c7934a697d14a314cea5da30f0c45660248d534db3cfb2036b5ac0d8a65a6e3696913021778491e84";
    private const string RecentBlockHeaderHex = "00a07b2daf1515873d86d8fba7a098689bcd958e6d2df870abe10100000000000000000077f88aefba92a3f434513218d7476aabaa35b9200cd339c6caea3db663ea1bfc9d355c6a9d36021724d435ae";
    private const string RecentBlockHash = "00000000000000000002122154787256060976bce119846233eee04fa0ac0fe2";
    private const string RecentBlockParentHash = "00000000000000000001e1ab70f82d6d8e95cd9b6898a0a7fbd8863d871515af";
    private const uint RecentBlockCompactTarget = 0x1702369d;
    private const string SampleCoinbaseHex = "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff2003e16d0e13426f6f742070726f746f636f6c0f626f6f74000709921015000000ffffffff06128e120000000000160014c64b1b9283ba1ea86bb9e7b696b0c8f68dad040004cc041000000000160014c64b1b9283ba1ea86bb9e7b696b0c8f68dad04000000000000000000106a0e9113b1ccf00d0000000000b9bb1952ad8b02000000001600141ba063a60ffe85ee2034c3044d7ef087a5f20f910000000000000000036a01000000000000000000266a24aa21a9edcddc611f6111ea75c5a265fba065e8eccb3d1ec8f954c738ea4586b3fffab1ce00000000";
    private static readonly List<string> SampleMerklePath =
    [
        "b6c40a03e40f9f35ff1a47dfc044a0b82dced05867abda7a3d77476f8d76ca8c",
        "e8474aac0f34d17bec62afaa624ebe64b36f9ce951daca4205dfcbb3061ce1a2",
        "e93d732b034381c7746dfb0a83ff7396336f8dd454fe5a7c7999f80e8c15b2d7",
        "0276e88a8933b31dc3f8b517bae033e18416754cca5f838320b6d3ab89be7b69",
        "254f86497567f0acd39d3a31948258459505bb0634fec20a27c6d7af3a3781c8",
        "fc970819c5af1e177e882851fe7c07b8206213422322d8571c51a9ef87a8d2e5",
        "1461f6060b70b6079c7b30ca17b0dde6657704da86b88e61844be9936be5157b"
    ];

    private static readonly IReadOnlyList<PayoutInfo> SampleExpectedWinners = BuildExpectedWinners(SampleCoinbaseHex);

    [TestMethod]
    public void LocalAdapterTokenDefaultsBesidePersistentStateInsteadOfReadOnlyAppDirectory()
    {
        string? previousStatePath = Environment.GetEnvironmentVariable("BOOT_PORTAL_STATE_PATH");
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"boot-adapter-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Environment.SetEnvironmentVariable(
                "BOOT_PORTAL_STATE_PATH",
                Path.Combine(tempDirectory, "pool_state.json"));
            var config = new PoolConfig { LocalAdapterTokenFile = "data/local-adapter.token" };

            _ = new LocalMiningAdapterAuth(config, NullLogger<LocalMiningAdapterAuth>.Instance);

            Assert.IsTrue(File.Exists(Path.Combine(tempDirectory, "local-adapter.token")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOOT_PORTAL_STATE_PATH", previousStatePath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void ValidateShareAttributesToSlotZeroInsteadOfClaimedMinerAddress()
    {
        var verifier = new BootShareVerifier();
        var forgedSubmission = new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "http"
        };

        BootShareValidationResult result = verifier.ValidateShare(
            forgedSubmission,
            SampleExpectedWinners,
            SamplePrevBlockHash);

        Assert.IsTrue(result.IsValid, result.RejectionReason);
        Assert.AreEqual(SampleSlotZeroAddress, result.MinerAddress);
        Assert.AreEqual(SampleSlotZeroAddress, result.Username);

        var claimedSubmission = new RecordedShareSubmission
        {
            MinerAddress = SampleSlotZeroAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "http"
        };

        BootShareValidationResult claimedResult = verifier.ValidateShare(
            claimedSubmission,
            SampleExpectedWinners,
            SamplePrevBlockHash);

        Assert.IsTrue(claimedResult.IsValid, claimedResult.RejectionReason);
        Assert.AreEqual(result.ShareId, claimedResult.ShareId, "ShareId should be independent of caller-supplied MinerAddress.");
    }

    [TestMethod]
    public void ValidateShareRejectsSlotZeroMutationWithoutHeaderRecompute()
    {
        var verifier = new BootShareVerifier();
        string mutatedCoinbaseHex = RewriteSlotZeroAddress(SampleCoinbaseHex, AlternateAddress);

        BootShareValidationResult result = verifier.ValidateShare(
            new RecordedShareSubmission
            {
                MinerAddress = AlternateAddress,
                Username = string.Empty,
                HeaderHex = SampleHeaderHex,
                CoinbaseHex = mutatedCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "http"
            },
            SampleExpectedWinners,
            SamplePrevBlockHash);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.RejectionReason ?? string.Empty, "merkle root");
    }

    [TestMethod]
    public void ValidateShareRejectsTruncatedGridPoolCoinbaseAsFirmwareCompatibilityIssue()
    {
        var verifier = new BootShareVerifier();
        string truncatedCoinbaseHex = BuildCoinbaseWithWinnerPrefix(SampleCoinbaseHex, positiveWinnerCount: 1);
        string updatedHeaderHex = RewriteHeaderMerkleRoot(SampleHeaderHex, truncatedCoinbaseHex);

        BootShareValidationResult result = verifier.ValidateShare(
            new RecordedShareSubmission
            {
                MinerAddress = SampleSlotZeroAddress,
                Username = string.Empty,
                HeaderHex = updatedHeaderHex,
                CoinbaseHex = truncatedCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "http"
            },
            SampleExpectedWinners,
            SamplePrevBlockHash);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.RejectionReason ?? string.Empty, "Coinbase appears truncated by miner firmware/DATUM coinbase-size selection");
        StringAssert.Contains(result.RejectionReason ?? string.Empty, "matched 1 of 2 required GridPool payout outputs");
    }

    [TestMethod]
    public void ValidateShareRejectsMutatedWinnerScriptAsGenericMismatch()
    {
        var verifier = new BootShareVerifier();
        string mutatedCoinbaseHex = BuildCoinbaseWithMutatedFirstWinnerScript(SampleCoinbaseHex);
        string updatedHeaderHex = RewriteHeaderMerkleRoot(SampleHeaderHex, mutatedCoinbaseHex);

        BootShareValidationResult result = verifier.ValidateShare(
            new RecordedShareSubmission
            {
                MinerAddress = SampleSlotZeroAddress,
                Username = string.Empty,
                HeaderHex = updatedHeaderHex,
                CoinbaseHex = mutatedCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "http"
            },
            SampleExpectedWinners,
            SamplePrevBlockHash);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.RejectionReason ?? string.Empty, "Coinbase winners payouts do not match");
        Assert.IsFalse((result.RejectionReason ?? string.Empty).Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ValidateShareRejectsSingleRecipientCoinbaseAsSoloFallback()
    {
        var verifier = new BootShareVerifier();
        string fallbackCoinbaseHex = BuildCoinbaseWithOnlySlotZero(SampleCoinbaseHex);
        string updatedHeaderHex = RewriteHeaderMerkleRoot(SampleHeaderHex, fallbackCoinbaseHex);

        BootShareValidationResult result = verifier.ValidateShare(
            new RecordedShareSubmission
            {
                MinerAddress = SampleSlotZeroAddress,
                Username = string.Empty,
                HeaderHex = updatedHeaderHex,
                CoinbaseHex = fallbackCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "http"
            },
            SampleExpectedWinners,
            SamplePrevBlockHash);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.RejectionReason ?? string.Empty, "non-Boot single-recipient template");
        Assert.IsFalse((result.RejectionReason ?? string.Empty).Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task HttpShareWithForgedMinerAddressIsAcceptedAndAttributedToSlotZeroAsync()
    {
        using var harness = TestHarness.Create();
        var response = await harness.MiningController.SubmitShare(new ShareSubmissionDto
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash
        });

        JsonObject payload = ParseObjectResult(response, StatusCodes.Status200OK);

        Assert.AreEqual("accepted", payload["status"]?.GetValue<string>());
        List<PayoutInfo> onDeckList = harness.StateService.GetOnDeckList();
        Assert.AreEqual(1, onDeckList.Count);
        Assert.AreEqual(SampleSlotZeroAddress, onDeckList[0].Address);
    }

    [TestMethod]
    public async Task HttpShareWithMissingBodyIsRejectedCleanlyAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult response = await harness.MiningController.SubmitShare(null);
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        Assert.AreEqual("Missing share payload", payload["reason"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task HttpShareValidationIgnoresConfiguredGridPoolCoinbaseTagAsync()
    {
        using var harness = TestHarness.Create();
        harness.Config.CoinbaseTag = "Grid Pool";

        IActionResult response = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status200OK);

        Assert.AreEqual("accepted", payload["status"]?.GetValue<string>());
        Assert.AreEqual(SampleSlotZeroAddress, harness.StateService.GetOnDeckList()[0].Address);
        BootLocalMiningSourceSummaryDto httpSource = harness.StateService.GetNetworkStatus()
            .LocalMiningSources.Single(source => source.Source == "http");
        Assert.AreEqual(1, httpSource.ActiveMinerCount);
        Assert.AreEqual("insufficient-data", httpSource.EstimationMethod);
    }

    [TestMethod]
    public async Task HttpShareValidationAllowsEmptyConfiguredCoinbaseTagAsync()
    {
        using var harness = TestHarness.Create();
        harness.Config.CoinbaseTag = string.Empty;

        IActionResult response = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status200OK);

        Assert.AreEqual("accepted", payload["status"]?.GetValue<string>());
        Assert.AreEqual(SampleSlotZeroAddress, harness.StateService.GetOnDeckList()[0].Address);
    }

    [TestMethod]
    public async Task PublicShareSourceHeaderLabelsHydrapoolWithoutChangingValidationAsync()
    {
        using var harness = TestHarness.Create();
        harness.MiningController.Request.Headers["X-GridPool-Mining-Source"] = "hydrapool";

        IActionResult response = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status200OK);
        BootLocalMiningSourceSummaryDto source = harness.StateService.GetNetworkStatus()
            .LocalMiningSources.Single(item => item.Source == "hydrapool");

        Assert.AreEqual("accepted", payload["status"]?.GetValue<string>());
        Assert.AreEqual(1, source.ActiveMinerCount);
        Assert.AreEqual("insufficient-data", source.EstimationMethod);
    }

    [TestMethod]
    public async Task DatumSharePopulatesPerAddressLocalHashrateSummaryAsync()
    {
        using var harness = TestHarness.Create();

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = "worker-a",
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        }, "datum-block");

        Assert.IsTrue(result.Accepted, result.RejectionReason);

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.AreEqual(0, status.LocalDatumMinerCount);
        Assert.AreEqual(0, status.LocalDatumMiners.Count);

        BootLocalDatumMinerSeriesDto lookup = harness.StateService.GetLocalDatumMinerSummaries(SampleSlotZeroAddress, 1);
        Assert.AreEqual(1, lookup.TotalTrackedMiners);
        Assert.AreEqual(1, lookup.ReturnedCount);
        Assert.AreEqual(SampleSlotZeroAddress, lookup.Miners[0].Address);
        Assert.AreEqual("worker-a", lookup.Miners[0].Username);
        Assert.AreEqual(1, lookup.Miners[0].TotalAcceptedShareCount);
        Assert.AreEqual(1, lookup.Miners[0].CurrentRoundAcceptedShareCount);
        Assert.IsTrue(lookup.Miners[0].CurrentRoundBestDifficulty > 0);
    }

    [TestMethod]
    public async Task DeclaredTargetCannotAuthorizeRoundRotationWithoutLocalBitcoinConfirmationAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: SamplePrevBlockHash);
        harness.Config.BitcoinNotificationMode = BitcoinNotificationModes.AttachedNode;
        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();
        string[] winnersBefore = harness.StateService.GetWinnersList().Select(winner => winner.Address).ToArray();
        string easyTargetHeader = RewriteHeaderCompactTarget(SampleHeaderHex, 0x2100ffff);
        BootShareHeaderEvaluationResult declaredTarget = new BootShareVerifier().EvaluateHeaderDifficulty(new RecordedShareSubmission
        {
            HeaderHex = easyTargetHeader,
            CoinbaseHex = SampleCoinbaseHex,
            PrevBlockHash = SamplePrevBlockHash
        });

        RoundRotationResult result = await harness.StateService.RotateToNextRoundAsync(
            declaredTarget.BlockHash,
            "untrusted-declared-target",
            manual: false,
            blockHeight: 945001);

        BootNetworkStatusDto after = harness.StateService.GetNetworkStatus();
        Assert.IsTrue(declaredTarget.IsValid, declaredTarget.RejectionReason);
        Assert.IsTrue(declaredTarget.IsBlock, "The attacker-controlled easy compact target classifies the header as a candidate.");
        Assert.IsFalse(result.Rotated);
        Assert.AreEqual("GridPool block is not confirmed by the local Bitcoin active chain", result.Reason);
        Assert.AreEqual(before.CurrentStateId, after.CurrentStateId);
        Assert.AreEqual(before.CurrentRoundNumber, after.CurrentRoundNumber);
        Assert.AreEqual(before.LastPaidSnapshotId, after.LastPaidSnapshotId);
        CollectionAssert.AreEqual(winnersBefore, harness.StateService.GetWinnersList().Select(winner => winner.Address).ToArray());
    }

    [TestMethod]
    public async Task LocalBitcoinConfirmationPromotesStoredCandidateExactlyOnceAsync()
    {
        BootShareProof storedCandidate = CreateFakeProof("stored-block-candidate", 100, SampleSlotZeroAddress);
        storedCandidate.HeaderHex = RecentBlockHeaderHex;
        storedCandidate.PrevBlockHash = RecentBlockParentHash;
        using var harness = TestHarness.Create(
            currentTipBlockHash: RecentBlockParentHash,
            onDeckProofs: [storedCandidate]);
        harness.Config.BitcoinNotificationMode = BitcoinNotificationModes.AttachedNode;
        BootStateBundle seededCandidate = harness.StateService.GetStateBundle(
            harness.StateService.GetNetworkStatus().CandidateStateId)!;
        Assert.AreEqual(1, seededCandidate.WorkSetProofs.Count);
        Assert.AreEqual(RecentBlockHeaderHex, seededCandidate.WorkSetProofs[0].HeaderHex);
        Assert.IsTrue(harness.StateService.ObserveLocalChainTipHeader(
            RecentBlockHeaderHex,
            "rpc-reconcile",
            DateTime.UtcNow,
            945001));
        Assert.AreEqual(
            RecentBlockHash,
            harness.StateService.GetNetworkEvents(eventType: "local-chain-tip-header").Events[0].BlockHash);
        BootNetworkStatusDto confirmed = await harness.StateService.ObserveChainTipAsync(
            RecentBlockHash,
            "rpc-reconcile",
            945001);
        int roundAfterConfirmation = confirmed.CurrentRoundNumber;

        BootNetworkStatusDto duplicate = await harness.StateService.ObserveChainTipAsync(
            RecentBlockHash,
            "rpc-reconcile",
            945001);
        Assert.AreEqual(
            RecentBlockHash,
            confirmed.LastGridPoolBlockHash,
            string.Join(", ", harness.StateService.GetNetworkEvents().Events.Select(item => $"{item.EventType}:{item.Message}")));
        Assert.AreEqual(roundAfterConfirmation, duplicate.CurrentRoundNumber);
        Assert.AreEqual(confirmed.CurrentStateId, duplicate.CurrentStateId);
    }

    [TestMethod]
    public async Task PeerShareWithForgedMinerAddressIsAcceptedAndAttributedToSlotZeroAsync()
    {
        using var harness = TestHarness.Create();
        var response = await harness.PeerController.SubmitPeerShare(new PeerShareAnnouncement
        {
            SenderEndpoint = "https://peer.example",
            ProtocolVersion = harness.Config.BootProtocolVersion,
            NetworkId = harness.Config.BootNetworkId,
            Share = new BootShareProof
            {
                ShareId = string.Empty,
                MinerAddress = AlternateAddress,
                Username = string.Empty,
                HeaderHex = SampleHeaderHex,
                CoinbaseHex = SampleCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "peer"
            }
        });

        JsonObject payload = ParseObjectResult(response, StatusCodes.Status200OK);

        Assert.AreEqual("accepted", payload["status"]?.GetValue<string>());
        List<PayoutInfo> onDeckList = harness.StateService.GetOnDeckList();
        Assert.AreEqual(1, onDeckList.Count);
        Assert.AreEqual(SampleSlotZeroAddress, onDeckList[0].Address);
    }

    [TestMethod]
    public async Task PeerShareWithMissingBodyIsRejectedCleanlyAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult response = await harness.PeerController.SubmitPeerShare(null);
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        Assert.AreEqual("Missing share payload", payload["reason"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task PeerShareOnUnknownParentIsRejectedWithoutAdvancingTipAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);

        IActionResult response = await harness.PeerController.SubmitPeerShare(CreateSamplePeerAnnouncement(harness.Config));
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        StringAssert.StartsWith(
            payload["reason"]?.GetValue<string>() ?? string.Empty,
            "Share builds on the wrong parent block");

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.AreEqual(OlderTipBlockHash, status.CurrentTipBlockHash);
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task ProoflessNewerCurrentStateIsRejectedWithoutMutatingPayoutStateAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);
        List<PayoutInfo> remoteWinners = SampleExpectedWinners.Select(ClonePayout).ToList();
        remoteWinners[0].Difficulty += 1024;

        bool adopted = await harness.StateService.TryAdoptCurrentStateAsync(
            new BootStateBundle
            {
                StateId = "remote-current-state",
                Kind = "current",
                CurrentRoundNumber = 2,
                ProtocolVersion = harness.Config.BootProtocolVersion,
                ConsensusVersion = harness.Config.BootProtocolVersion,
                StateBundleSchemaVersion = BootProtocolVersions.StateBundleSchemaVersion,
                HttpApiVersion = BootProtocolVersions.HttpApiVersion,
                PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
                UdpRelayVersion = BootProtocolVersions.UdpRelayVersion,
                ReleaseVersion = harness.StateService.GetLocalVersionInfo().ReleaseVersion,
                VersionInfo = harness.StateService.GetLocalVersionInfo(),
                NetworkId = harness.Config.BootNetworkId,
                LockedByBlockHash = SamplePrevBlockHash,
                LockedByBlockHeight = 945001,
                CreatedAtUtc = DateTime.UtcNow,
                TotalDifficulty = remoteWinners.Sum(x => x.Difficulty),
                WinnersList = remoteWinners
            },
            SamplePrevBlockHash,
            945001,
            "https://peer.example");

        Assert.IsFalse(adopted);
        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.AreEqual("seed-current", status.CurrentStateId);
        Assert.AreEqual(1, status.CurrentRoundNumber);
        CollectionAssert.AreEqual(
            SampleExpectedWinners.Select(winner => winner.Address).ToArray(),
            harness.StateService.GetWinnersList().Select(winner => winner.Address).ToArray());
    }

    [TestMethod]
    public async Task ProoflessSameRoundCurrentStateDoesNotOverrideEstablishedStateAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);
        List<PayoutInfo> remoteWinners = SampleExpectedWinners.Select(ClonePayout).ToList();
        remoteWinners[0].Difficulty += 1000000;

        bool adopted = await harness.StateService.TryAdoptCurrentStateAsync(
            new BootStateBundle
            {
                StateId = "remote-proofless-stronger-state",
                Kind = "current",
                CurrentRoundNumber = 1,
                ProtocolVersion = harness.Config.BootProtocolVersion,
                ConsensusVersion = harness.Config.BootProtocolVersion,
                StateBundleSchemaVersion = BootProtocolVersions.StateBundleSchemaVersion,
                HttpApiVersion = BootProtocolVersions.HttpApiVersion,
                PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
                UdpRelayVersion = BootProtocolVersions.UdpRelayVersion,
                ReleaseVersion = harness.StateService.GetLocalVersionInfo().ReleaseVersion,
                VersionInfo = harness.StateService.GetLocalVersionInfo(),
                NetworkId = harness.Config.BootNetworkId,
                LockedByBlockHash = OlderTipBlockHash,
                LockedByBlockHeight = 945000,
                CreatedAtUtc = DateTime.UtcNow,
                TotalDifficulty = remoteWinners.Sum(x => x.Difficulty),
                WinnersList = remoteWinners
            },
            OlderTipBlockHash,
            945000,
            "https://peer.example");

        Assert.IsFalse(adopted);
        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.AreEqual("seed-current", status.CurrentStateId);
        Assert.AreEqual(1, status.CurrentRoundNumber);
    }

    [TestMethod]
    public async Task ProoflessBootstrapCannotInstallRemoteWinnersOrPaidLineageAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: SamplePrevBlockHash);
        List<PayoutInfo> attackerWinners = SampleExpectedWinners.Select(ClonePayout).ToList();
        foreach (PayoutInfo payout in attackerWinners)
        {
            payout.Address = AlternateAddress;
            payout.Username = AlternateAddress;
        }
        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();
        string[] winnersBefore = harness.StateService.GetWinnersList().Select(winner => winner.Address).ToArray();

        bool adopted = await harness.StateService.TryBootstrapCurrentStateAsync(
            new BootStateBundle
            {
                StateId = new string('4', 64),
                Kind = "current",
                CurrentRoundNumber = 1_000_000,
                ProtocolVersion = harness.Config.BootProtocolVersion,
                ConsensusVersion = harness.Config.BootProtocolVersion,
                StateBundleSchemaVersion = BootProtocolVersions.StateBundleSchemaVersion,
                HttpApiVersion = BootProtocolVersions.HttpApiVersion,
                PeerTransportVersion = BootProtocolVersions.PeerTransportVersion,
                UdpRelayVersion = BootProtocolVersions.UdpRelayVersion,
                ReleaseVersion = harness.StateService.GetLocalVersionInfo().ReleaseVersion,
                VersionInfo = harness.StateService.GetLocalVersionInfo(),
                NetworkId = harness.Config.BootNetworkId,
                LockedByBlockHash = SamplePrevBlockHash,
                LockedByBlockHeight = 945000,
                WinnersList = attackerWinners,
                ShareProofs = [],
                PaidSnapshotId = "attacker-paid-lineage",
                PaidSnapshotProofIds = ["attacker-proof"]
            },
            SamplePrevBlockHash,
            945000,
            "https://malicious-peer.example");

        BootNetworkStatusDto after = harness.StateService.GetNetworkStatus();
        Assert.IsFalse(adopted);
        Assert.AreEqual(before.CurrentStateId, after.CurrentStateId);
        Assert.AreEqual(before.CurrentRoundNumber, after.CurrentRoundNumber);
        Assert.AreEqual(before.LastPaidSnapshotId, after.LastPaidSnapshotId);
        CollectionAssert.AreEqual(
            winnersBefore,
            harness.StateService.GetWinnersList().Select(winner => winner.Address).ToArray());
    }

    [TestMethod]
    public async Task ProofBackedRemoteStateCannotRewriteUnverifiedPaidLineageAsync()
    {
        byte[] header = Convert.FromHexString(SampleHeaderHex);
        var proofSet = new List<BootShareProof>();
        for (uint nonce = 0; nonce < 16; nonce++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), nonce);
            proofSet.Add(CreateValidatedProof(
                Convert.ToHexString(header).ToLowerInvariant(),
                SamplePrevBlockHash,
                "seed-current"));
        }

        using var remoteHarness = TestHarness.Create(
            currentTipBlockHash: SamplePrevBlockHash,
            onDeckProofs: proofSet);

        RoundRotationResult rotation = await remoteHarness.StateService.RotateToNextRoundAsync(
            OlderTipBlockHash,
            "test-rotation",
            manual: false,
            blockHeight: 945001,
            localBitcoinActiveChainConfirmed: true);
        BootStateBundle remoteBundle = rotation.LockedStateBundle!;
        Assert.IsTrue(remoteBundle.WorkSetProofs.Count > 0);

        using var localHarness = TestHarness.Create(
            currentTipBlockHash: OlderTipBlockHash,
            currentRoundNumber: remoteBundle.CurrentRoundNumber);
        bool adopted = await localHarness.StateService.TryAdoptCurrentStateAsync(
            remoteBundle,
            OlderTipBlockHash,
            945001,
            "https://peer.example");

        Assert.IsFalse(adopted);
        BootNetworkStatusDto status = localHarness.StateService.GetNetworkStatus();
        Assert.AreEqual("seed-current", status.CurrentStateId);
        Assert.AreEqual(remoteBundle.CurrentRoundNumber, status.CurrentRoundNumber);
    }

    [TestMethod]
    public async Task DatumShareCannotTeachNodeAnUnvalidatedFreshParentAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        }, "datum-block");

        Assert.IsFalse(result.Accepted);
        StringAssert.Contains(result.RejectionReason, "outside the active Bitcoin tip");

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.AreEqual(OlderTipBlockHash, status.CurrentTipBlockHash);
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);

        Thread.Sleep(1200);
        PoolState persisted = JsonSerializer.Deserialize<PoolState>(File.ReadAllText(harness.StatePath))!;
        CollectionAssert.Contains(persisted.AcceptedParentBlockHashes, OlderTipBlockHash);
        CollectionAssert.DoesNotContain(persisted.AcceptedParentBlockHashes, SamplePrevBlockHash);
    }

    [TestMethod]
    public async Task AcceptedShareRelayPayloadIncludesFullProofAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: SamplePrevBlockHash);

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        }, "datum-block");

        Assert.IsTrue(result.Accepted, result.RejectionReason);
        Assert.IsTrue(result.AffectedOnDeck);
        Assert.IsTrue(harness.StateService.AcceptedShares.TryRead(out BootShareProof? relayProof));
        Assert.AreEqual(SampleHeaderHex, relayProof!.HeaderHex);
        Assert.AreEqual(160, relayProof.HeaderHex.Length);
        Assert.AreEqual(SampleCoinbaseHex, relayProof.CoinbaseHex);
        CollectionAssert.AreEqual(SampleMerklePath.ToList(), relayProof.MerklePath);
        Assert.AreEqual(SamplePrevBlockHash, relayProof.PrevBlockHash);
        Assert.IsFalse(string.IsNullOrWhiteSpace(relayProof.ShareId));
    }

    [TestMethod]
    public async Task PulseProofDoesNotMutateWorkSetButRelaysTelemetryProofAsync()
    {
        BootShareProof higherDifficultyProof = CreateFakeProof("seed-high-proof", 1_000_000_000d);
        using var harness = TestHarness.Create(
            currentTipBlockHash: SamplePrevBlockHash,
            sharedWinnerSlotCount: 1,
            workSetReserveMultiplier: 1,
            onDeckProofs: [higherDifficultyProof]);
        harness.Config.EnablePulseProofs = true;
        harness.Config.PulseMinDifficulty = 1;
        harness.Config.PulseRelayTtl = 1;

        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();
        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        }, "datum-block");
        BootNetworkStatusDto after = harness.StateService.GetNetworkStatus();

        Assert.IsTrue(result.Accepted, result.RejectionReason);
        Assert.IsTrue(result.PulseAccepted);
        Assert.AreEqual(BootProofClasses.Pulse, result.ProofClass);
        Assert.IsFalse(result.AffectedConsensusState);
        Assert.IsFalse(result.AffectedOnDeck);
        Assert.AreEqual(before.CandidateStateId, after.CandidateStateId);
        Assert.AreEqual(before.WorkSetCount, after.WorkSetCount);
        Assert.AreEqual(higherDifficultyProof.ShareId, harness.StateService.GetStateBundle(after.CandidateStateId)!.WorkSetProofs[0].ShareId);
        Assert.IsTrue(harness.StateService.AcceptedShares.TryRead(out BootShareProof? relayProof));
        Assert.AreEqual(BootProofClasses.Pulse, relayProof!.ProofClass);
        Assert.AreEqual(BootRelayStages.Validated, relayProof.RelayStage);
        Assert.AreEqual(1, relayProof.RelayTtl);
    }

    [TestMethod]
    public async Task LocalDatumPulseRateLimitDoesNotRejectMinerShareAsync()
    {
        BootShareProof higherDifficultyProof = CreateFakeProof("seed-high-proof", 1_000_000_000d);
        using var harness = TestHarness.Create(
            currentTipBlockHash: SamplePrevBlockHash,
            sharedWinnerSlotCount: 1,
            workSetReserveMultiplier: 1,
            onDeckProofs: [higherDifficultyProof]);
        harness.Config.EnablePulseProofs = true;
        harness.Config.PulseMinDifficulty = 1;
        harness.Config.PulseRelayTtl = 1;
        harness.Config.PulseMaxPerPeerPerMinute = 1;
        harness.Config.PulseMaxPerSourceAddressPerMinute = 1;

        var submission = new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        };

        ShareRecordingResult first = await harness.StateService.SubmitShareAsync(submission, "datum-block");
        ShareRecordingResult second = await harness.StateService.SubmitShareAsync(submission, "datum-block");

        Assert.IsTrue(first.Accepted, first.RejectionReason);
        Assert.IsFalse(second.Accepted);
        Assert.AreEqual("Duplicate share", second.RejectionReason);
    }

    [TestMethod]
    public async Task PeerPulseRateLimitStillRejectsExcessRelayProofsAsync()
    {
        BootShareProof higherDifficultyProof = CreateFakeProof("seed-high-proof", 1_000_000_000d);
        using var harness = TestHarness.Create(
            currentTipBlockHash: SamplePrevBlockHash,
            sharedWinnerSlotCount: 1,
            workSetReserveMultiplier: 1,
            onDeckProofs: [higherDifficultyProof]);
        harness.Config.EnablePulseProofs = true;
        harness.Config.PulseMinDifficulty = 1;
        harness.Config.PulseRelayTtl = 1;
        harness.Config.PulseMaxPerPeerPerMinute = 1;
        harness.Config.PulseMaxPerSourceAddressPerMinute = 1;

        var submission = new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "peer:test-node:websocket",
            RelayTtl = 1
        };

        ShareRecordingResult first = await harness.StateService.SubmitShareAsync(submission, "peer-block");
        ShareRecordingResult second = await harness.StateService.SubmitShareAsync(submission, "peer-block");

        Assert.IsTrue(first.Accepted, first.RejectionReason);
        Assert.IsFalse(second.Accepted);
        Assert.AreEqual("Pulse rate limited", second.RejectionReason);
    }

    [TestMethod]
    public async Task OptimisticRelayEmitsBeforeValidatedRelayWhenEnabledAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: SamplePrevBlockHash);
        harness.Config.EnableOptimisticShareRelay = true;
        harness.Config.MinOptimisticRelayDifficulty = 1;

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        }, "datum-block");

        Assert.IsTrue(result.Accepted, result.RejectionReason);
        Assert.IsTrue(result.AffectedOnDeck);
        Assert.IsTrue(harness.StateService.AcceptedShares.TryRead(out BootShareProof? optimisticProof));
        Assert.IsTrue(harness.StateService.AcceptedShares.TryRead(out BootShareProof? validatedProof));
        Assert.AreEqual(BootRelayStages.Optimistic, optimisticProof!.RelayStage);
        Assert.AreEqual(BootRelayStages.Validated, validatedProof!.RelayStage);
        Assert.AreEqual(optimisticProof.ShareId, validatedProof.ShareId);
        Assert.IsTrue(optimisticProof.DifficultyCheckedUtc.HasValue);
        Assert.IsTrue(validatedProof.ValidationCompletedUtc.HasValue);
    }

    [TestMethod]
    public void UdpShareCodecPreservesPulseProofMetadata()
    {
        BootShareProof proof = CreateValidatedProof(SampleHeaderHex, SamplePrevBlockHash, "seed-current");
        proof.ProofClass = BootProofClasses.Pulse;
        proof.RelayStage = BootRelayStages.Optimistic;
        proof.RelayTtl = 2;

        bool encoded = BootPeerUdpShareCodec.TryEncode(proof, new PoolConfig(), out byte[] payload, out string encodeReason);
        Assert.IsTrue(encoded, encodeReason);

        bool decoded = BootPeerUdpShareCodec.TryDecode(payload, new PoolConfig(), out RecordedShareSubmission share, out string decodeReason);
        Assert.IsTrue(decoded, decodeReason);
        Assert.AreEqual(BootProofClasses.Pulse, share.ProofClass);
        Assert.AreEqual(BootRelayStages.Optimistic, share.RelayStage);
        Assert.AreEqual(2, share.RelayTtl);
        Assert.AreEqual(proof.HeaderHex, share.HeaderHex);
        Assert.AreEqual(proof.CoinbaseHex, share.CoinbaseHex);
    }

    [TestMethod]
    public void UdpChainTipCodecRoundTripsRawHeaderAndHeight()
    {
        string blockHash = BitcoinHashes.ComputeBlockHashFromHeader(SampleHeaderHex);
        var announcement = new BootChainTipAnnouncement
        {
            HeaderHex = SampleHeaderHex,
            BlockHash = blockHash,
            BlockHeight = 945123
        };

        bool encoded = BootPeerUdpChainTipCodec.TryEncode(announcement, out byte[] payload, out string encodeReason);
        Assert.IsTrue(encoded, encodeReason);
        Assert.IsTrue(BootPeerUdpChainTipCodec.LooksLikeChainTip(payload));

        bool decoded = BootPeerUdpChainTipCodec.TryDecode(payload, out BootChainTipAnnouncement decodedAnnouncement, out string decodeReason);
        Assert.IsTrue(decoded, decodeReason);
        Assert.AreEqual(SampleHeaderHex, decodedAnnouncement.HeaderHex);
        Assert.AreEqual(blockHash, decodedAnnouncement.BlockHash);
        Assert.AreEqual(945123, decodedAnnouncement.BlockHeight);
    }

    [TestMethod]
    public void UdpChainTipCodecRejectsHeaderHashMismatch()
    {
        bool encoded = BootPeerUdpChainTipCodec.TryEncode(new BootChainTipAnnouncement
        {
            HeaderHex = SampleHeaderHex,
            BlockHash = new string('0', 64)
        }, out _, out string reason);

        Assert.IsFalse(encoded);
        Assert.AreEqual("header-hash-mismatch", reason);
    }

    [TestMethod]
    public void LocalRawHeaderObservationPublishesOneCoordinatedRelayAndTelemetry()
    {
        using var harness = TestHarness.Create();
        DateTime observedUtc = DateTime.UtcNow.AddMilliseconds(-25);

        bool accepted = harness.StateService.ObserveLocalChainTipHeader(
            RecentBlockHeaderHex,
            "test-rawblock",
            observedUtc,
            945123);

        Assert.IsTrue(accepted);
        Assert.IsTrue(harness.StateService.ChainTipAnnouncements.TryRead(out BootChainTipAnnouncement? sessionAnnouncement));
        Assert.AreEqual(RecentBlockHeaderHex, sessionAnnouncement!.HeaderHex);
        Assert.IsTrue(sessionAnnouncement.RelayQueuedUtc >= observedUtc);

        BootNetworkEventSeriesDto events = harness.StateService.GetNetworkEvents(eventType: "local-chain-tip-header");
        Assert.AreEqual(1, events.Events.Count);
        Assert.AreEqual(observedUtc, events.Events[0].TimestampUtc);
        Assert.AreEqual("bitcoin-zmq-rawblock", events.Events[0].Transport);
        Assert.AreEqual(80, events.Events[0].PayloadBytes);
    }

    [TestMethod]
    public async Task PeerHeaderObservationIsMeasurementOnlyAndDoesNotAdvanceTipAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);
        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();
        string announcedHash = BitcoinHashes.ComputeBlockHashFromHeader(RecentBlockHeaderHex);

        await harness.StateService.ObservePeerChainTipAsync(
            new BootChainTipAnnouncement
            {
                HeaderHex = RecentBlockHeaderHex,
                BlockHash = announcedHash,
                BlockHeight = 945123,
                Source = "remote-zmq"
            },
            "https://peer.example",
            "peer-node",
            "udp",
            138,
            DateTime.UtcNow);

        BootNetworkStatusDto after = harness.StateService.GetNetworkStatus();
        Assert.AreEqual(before.CurrentTipBlockHash, after.CurrentTipBlockHash);
        Assert.AreEqual(before.CurrentStateId, after.CurrentStateId);
        Assert.AreEqual(before.CandidateStateId, after.CandidateStateId);

        BootNetworkEventSeriesDto events = harness.StateService.GetNetworkEvents(eventType: "peer-chain-tip");
        Assert.AreEqual(1, events.Events.Count);
        Assert.AreEqual(announcedHash, events.Events[0].BlockHash);
        Assert.AreEqual("udp", events.Events[0].Transport);
        Assert.IsNull(events.Events[0].RelayLatencyMs);
    }

    [TestMethod]
    public async Task PeerHeaderFreezesProvisionalSnapshotUntilLocalConfirmationAsync()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress)
        ];
        using var harness = TestHarness.Create(
            currentTipBlockHash: RecentBlockParentHash,
            currentTipBlockHeight: 958645,
            currentTipCompactTarget: RecentBlockCompactTarget,
            sharedWinnerSlotCount: 2,
            onDeckProofs: seedProofs,
            enablePeerTipStaleProtection: true,
            peerTipGraceSeconds: 30);

        string activeBefore = harness.StateService.GetNetworkStatus().ActiveSnapshotId;
        BootNetworkStatusDto provisional = await harness.StateService.ObservePeerChainTipAsync(
            new BootChainTipAnnouncement
            {
                HeaderHex = RecentBlockHeaderHex,
                BlockHash = RecentBlockHash,
                BlockHeight = 958646,
                Source = "remote-zmq"
            },
            "https://peer.example",
            "peer-node",
            "udp",
            138,
            DateTime.UtcNow);

        Assert.AreEqual(RecentBlockParentHash, provisional.CurrentTipBlockHash);
        Assert.AreEqual(activeBefore, provisional.ActiveSnapshotId);
        Assert.AreEqual(RecentBlockHash, provisional.ProvisionalTipBlockHash);
        Assert.AreEqual(2, provisional.ProvisionalSnapshotProofCount);
        Assert.IsTrue(provisional.MiningWorkSafe);

        harness.StateService.ObserveLocalChainTipHeader(RecentBlockHeaderHex, "test-local", DateTime.UtcNow, 958646);
        BootNetworkStatusDto confirmed = await harness.StateService.ObserveChainTipAsync(
            RecentBlockHash,
            "test-local",
            958646);

        Assert.AreEqual(RecentBlockHash, confirmed.CurrentTipBlockHash);
        Assert.IsNull(confirmed.ProvisionalTipBlockHash);
        Assert.AreEqual(2, confirmed.ActiveSnapshotProofCount);
        Assert.AreNotEqual(activeBefore, confirmed.ActiveSnapshotId);
        Assert.IsTrue(confirmed.MiningWorkSafe);
    }

    [TestMethod]
    public async Task ProvisionalPeerTipPausesFreshWorkAfterGraceAsync()
    {
        using var harness = TestHarness.Create(
            currentTipBlockHash: RecentBlockParentHash,
            currentTipBlockHeight: 958645,
            currentTipCompactTarget: RecentBlockCompactTarget,
            enablePeerTipStaleProtection: true,
            peerTipGraceSeconds: 1);

        await harness.StateService.ObservePeerChainTipAsync(
            new BootChainTipAnnouncement
            {
                HeaderHex = RecentBlockHeaderHex,
                BlockHash = RecentBlockHash,
                BlockHeight = 958646,
                Source = "remote-zmq"
            },
            "https://peer.example",
            "peer-node",
            "udp",
            138,
            DateTime.UtcNow);

        await Task.Delay(1200);
        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.IsFalse(status.MiningWorkSafe);
        Assert.IsTrue(status.LocalBitcoinLagging);
        Assert.IsInstanceOfType<ConflictObjectResult>(harness.MiningController.GetSv2WorkSelection());
    }

    [TestMethod]
    public async Task DatumShareOnFreshParentWithInvalidCoinbaseReportsRetryFailureAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = RewriteSlotZeroAddress(SampleCoinbaseHex, AlternateAddress),
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash,
            Source = "datum"
        }, "datum-block");

        Assert.IsFalse(result.Accepted);
        StringAssert.Contains(result.RejectionReason ?? string.Empty, "merkle root");

        BootNetworkEventSeriesDto events = harness.StateService.GetNetworkEvents(eventType: "fresh-parent-retry-failed");
        Assert.AreEqual(1, events.Events.Count);
        StringAssert.Contains(events.Events[0].Message ?? string.Empty, "merkle root");
        Assert.AreEqual(SamplePrevBlockHash, events.Events[0].BlockHash);
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task HttpShareOnFreshParentIsRejectedUntilTipIsKnownAsync()
    {
        using var harness = TestHarness.Create(currentTipBlockHash: OlderTipBlockHash);

        IActionResult response = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        StringAssert.StartsWith(
            payload["reason"]?.GetValue<string>() ?? string.Empty,
            "Share builds on the wrong parent block");
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);
        Assert.AreEqual(OlderTipBlockHash, harness.StateService.GetNetworkStatus().CurrentTipBlockHash);
    }

    [TestMethod]
    public async Task LocalSubmitFollowedByPeerReplayReturnsDuplicateWithoutChangingCandidateStateAsync()
    {
        using var harness = TestHarness.Create();
        IActionResult firstResponse = await harness.MiningController.SubmitShare(new ShareSubmissionDto
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash
        });
        JsonObject firstPayload = ParseObjectResult(firstResponse, StatusCodes.Status200OK);
        Assert.AreEqual("accepted", firstPayload["status"]?.GetValue<string>());

        BootNetworkStatusDto stateAfterFirst = harness.StateService.GetNetworkStatus();
        IActionResult secondResponse = await harness.PeerController.SubmitPeerShare(new PeerShareAnnouncement
        {
            SenderEndpoint = "https://peer.example",
            ProtocolVersion = harness.Config.BootProtocolVersion,
            NetworkId = harness.Config.BootNetworkId,
            Share = new BootShareProof
            {
                ShareId = "forged-legacy-id",
                MinerAddress = "bc1qtotallydifferent000000000000000000000000000",
                Username = "forged-user",
                HeaderHex = SampleHeaderHex,
                CoinbaseHex = SampleCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "peer"
            }
        });

        JsonObject secondPayload = ParseObjectResult(secondResponse, StatusCodes.Status200OK);
        BootNetworkStatusDto stateAfterSecond = harness.StateService.GetNetworkStatus();

        Assert.AreEqual("duplicate", secondPayload["status"]?.GetValue<string>());
        Assert.AreEqual(stateAfterFirst.CandidateStateId, stateAfterSecond.CandidateStateId);
        Assert.AreEqual(1, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task LocalSubmitFollowedByDuplicateLocalSubmitReturnsDuplicateWithoutChangingCandidateStateAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult firstResponse = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject firstPayload = ParseObjectResult(firstResponse, StatusCodes.Status200OK);
        Assert.AreEqual("accepted", firstPayload["status"]?.GetValue<string>());

        BootNetworkStatusDto stateAfterFirst = harness.StateService.GetNetworkStatus();

        IActionResult secondResponse = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject secondPayload = ParseObjectResult(secondResponse, StatusCodes.Status200OK);
        BootNetworkStatusDto stateAfterSecond = harness.StateService.GetNetworkStatus();

        Assert.AreEqual("duplicate", secondPayload["status"]?.GetValue<string>());
        Assert.AreEqual(stateAfterFirst.CandidateStateId, stateAfterSecond.CandidateStateId);
        Assert.AreEqual(1, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task PeerSubmitFollowedByDuplicatePeerSubmitReturnsDuplicateWithoutChangingCandidateStateAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult firstResponse = await harness.PeerController.SubmitPeerShare(CreateSamplePeerAnnouncement(harness.Config));
        JsonObject firstPayload = ParseObjectResult(firstResponse, StatusCodes.Status200OK);
        Assert.AreEqual("accepted", firstPayload["status"]?.GetValue<string>());

        BootNetworkStatusDto stateAfterFirst = harness.StateService.GetNetworkStatus();

        IActionResult secondResponse = await harness.PeerController.SubmitPeerShare(CreateSamplePeerAnnouncement(harness.Config));
        JsonObject secondPayload = ParseObjectResult(secondResponse, StatusCodes.Status200OK);
        BootNetworkStatusDto stateAfterSecond = harness.StateService.GetNetworkStatus();

        Assert.AreEqual("duplicate", secondPayload["status"]?.GetValue<string>());
        Assert.AreEqual(stateAfterFirst.CandidateStateId, stateAfterSecond.CandidateStateId);
        Assert.AreEqual(1, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task PeerSubmitWithWrongNetworkIsRejectedBeforeInsertionAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult response = await harness.PeerController.SubmitPeerShare(CreateSamplePeerAnnouncement(
            harness.Config,
            protocolVersion: harness.Config.BootProtocolVersion,
            networkId: "wrong-network"));

        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        StringAssert.StartsWith(payload["reason"]?.GetValue<string>() ?? string.Empty, "network id mismatch");
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task PeerSubmitWithWrongProtocolVersionIsRejectedBeforeInsertionAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult response = await harness.PeerController.SubmitPeerShare(CreateSamplePeerAnnouncement(
            harness.Config,
            protocolVersion: harness.Config.BootProtocolVersion + 1,
            networkId: harness.Config.BootNetworkId));

        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        StringAssert.StartsWith(payload["reason"]?.GetValue<string>() ?? string.Empty, "consensus version mismatch");
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task DuplicateBlockRotationIsIgnoredAfterFirstApplyAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult shareResponse = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject sharePayload = ParseObjectResult(shareResponse, StatusCodes.Status200OK);
        Assert.AreEqual("accepted", sharePayload["status"]?.GetValue<string>());

        const string blockHash = "0000000000000000000000000000000000000000000000000000000000abc123";

        RoundRotationResult firstRotation = await harness.StateService.RotateToNextRoundAsync(
            blockHash,
            "test-block",
            manual: false,
            blockHeight: 945001,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsTrue(firstRotation.Rotated);
        string stateAfterFirstRotation = firstRotation.NetworkStatus.CurrentStateId;
        int roundAfterFirstRotation = firstRotation.NetworkStatus.CurrentRoundNumber;

        RoundRotationResult secondRotation = await harness.StateService.RotateToNextRoundAsync(
            blockHash,
            "test-block",
            manual: false,
            blockHeight: 945001,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsFalse(secondRotation.Rotated);
        Assert.AreEqual("Block already applied", secondRotation.Reason);
        Assert.AreEqual(stateAfterFirstRotation, secondRotation.NetworkStatus.CurrentStateId);
        Assert.AreEqual(roundAfterFirstRotation, secondRotation.NetworkStatus.CurrentRoundNumber);
    }

    [TestMethod]
    public void V1StyleStateMigratesToV2SnapshotReserveWithoutDroppingWorkSetProofs()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100),
            CreateFakeProof("proof-b", 50)
        ];

        using var harness = TestHarness.Create(
            onDeckProofs: seedProofs,
            seedMetadataProtocolVersion: 1);

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        BootStateBundle activeBundle = harness.StateService.GetStateBundle(status.CurrentStateId)!;

        Assert.AreEqual(BootProtocolVersions.ConsensusVersion, status.ProtocolVersion);
        Assert.AreEqual(seedProofs.Length, status.WorkSetCount);
        Assert.AreEqual("seed-current", status.ActiveSnapshotId);
        Assert.AreEqual(seedProofs.Length, harness.StateService.GetOnDeckList().Count);
        Assert.IsTrue(activeBundle.SnapshotContexts.Any(context => context.SnapshotId == status.ActiveSnapshotId));
        Assert.IsTrue(activeBundle.WorkSetProofs.All(proof => proof.PayoutSnapshotId == status.ActiveSnapshotId));
    }

    [TestMethod]
    public void PeerTransportMismatchAllowsHttpFallbackWhenConsensusAndSchemaMatch()
    {
        using var harness = TestHarness.Create();
        BootNetworkStatusDto remote = harness.StateService.GetNetworkStatus();
        remote.PeerTransportVersion = BootProtocolVersions.PeerTransportVersion + 1;
        remote.VersionInfo.PeerTransportVersion = remote.PeerTransportVersion;

        BootVersionCompatibilityDto compatibility = harness.StateService.EvaluatePeerCompatibility(remote);

        Assert.IsTrue(compatibility.CanSyncState);
        Assert.AreEqual("compatible-with-transport-fallback", compatibility.Status);
        StringAssert.Contains(compatibility.Reason, "using HTTP fallback");
    }

    [TestMethod]
    public async Task CandidateStateWithMissingStateBundleSchemaIsRejectedBeforeImportAsync()
    {
        using var remoteHarness = TestHarness.Create(workSetReserveMultiplier: 1);
        ShareRecordingResult seedResult = await remoteHarness.StateService.SubmitShareAsync(
            new RecordedShareSubmission
            {
                MinerAddress = SampleSlotZeroAddress,
                Username = string.Empty,
                HeaderHex = SampleHeaderHex,
                CoinbaseHex = SampleCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "datum"
            },
            "datum-block");
        Assert.IsTrue(seedResult.Accepted, seedResult.RejectionReason);

        BootNetworkStatusDto remoteStatus = remoteHarness.StateService.GetNetworkStatus();
        BootStateBundle currentBundle = remoteHarness.StateService.GetStateBundle(remoteStatus.CurrentStateId)!;
        BootStateBundle candidateBundle = remoteHarness.StateService.GetStateBundle(remoteStatus.CandidateStateId)!;
        candidateBundle.StateBundleSchemaVersion = 0;
        candidateBundle.VersionInfo.StateBundleSchemaVersion = 0;

        using var localHarness = TestHarness.Create(
            currentTipBlockHash: remoteStatus.CurrentTipBlockHash,
            currentRoundNumber: remoteStatus.CurrentRoundNumber,
            currentStateId: remoteStatus.CurrentStateId,
            winnersList: currentBundle.WinnersList,
            activeSnapshotId: currentBundle.ActiveSnapshotId,
            activeSnapshotProofIds: currentBundle.ActiveSnapshotProofIds,
            snapshotContexts: currentBundle.SnapshotContexts,
            workSetReserveMultiplier: 1);

        bool imported = await localHarness.StateService.TryImportCandidateStateAsync(
            candidateBundle,
            "https://peer.example");

        Assert.IsFalse(imported);
        Assert.AreNotEqual(remoteStatus.CandidateStateId, localHarness.StateService.GetNetworkStatus().CandidateStateId);
    }

    [TestMethod]
    public void V2CandidateStateSelectionRequiresStrictlyStrongerWorkSetUnlessStateIdAlreadyMatches()
    {
        Assert.IsTrue(BootCandidateStateSelection.ShouldImportCandidate(
            remoteTotalDifficulty: 101,
            localTotalDifficulty: 100,
            remoteStateId: "remote-stronger",
            localCandidateStateId: "local-candidate"));

        Assert.IsFalse(BootCandidateStateSelection.ShouldImportCandidate(
            remoteTotalDifficulty: 100,
            localTotalDifficulty: 100,
            remoteStateId: "remote-equal",
            localCandidateStateId: "local-candidate"));

        Assert.IsFalse(BootCandidateStateSelection.ShouldImportCandidate(
            remoteTotalDifficulty: 99,
            localTotalDifficulty: 100,
            remoteStateId: "remote-weaker",
            localCandidateStateId: "local-candidate"));

        Assert.IsTrue(BootCandidateStateSelection.ShouldImportCandidate(
            remoteTotalDifficulty: 100,
            localTotalDifficulty: 100,
            remoteStateId: "same-candidate",
            localCandidateStateId: "same-candidate"));
    }

    [TestMethod]
    public async Task CandidateImportIgnoresLatePreviousParentProofsAfterLocalSnapshotBoundaryAsync()
    {
        string newTip = "0000000000000000000000000000000000000000000000000000000000f00d01";
        BootShareProof staleProof = CreateValidatedProof(SampleHeaderHex, SamplePrevBlockHash, "seed-current");

        using var remoteHarness = TestHarness.Create(
            currentTipBlockHash: newTip,
            onDeckProofs: [staleProof],
            snapshotContexts: [CreateSnapshotContext("seed-current", SampleExpectedWinners)]);
        BootNetworkStatusDto remoteStatus = remoteHarness.StateService.GetNetworkStatus();
        BootStateBundle candidateBundle = remoteHarness.StateService.GetStateBundle(remoteStatus.CandidateStateId)!;

        using var localHarness = TestHarness.Create(
            currentTipBlockHash: newTip,
            onDeckProofs: [],
            snapshotContexts: [CreateSnapshotContext("seed-current", SampleExpectedWinners)]);

        bool imported = await localHarness.StateService.TryImportCandidateStateAsync(
            candidateBundle,
            "https://peer.example");

        Assert.IsFalse(imported);
        Assert.AreEqual(0, localHarness.StateService.GetNetworkStatus().WorkSetCount);
    }

    [TestMethod]
    [DataRow(21)]
    [DataRow(22)]
    public async Task DirectIngressRejectsNewPreviousParentProofAfterFinalizedBoundaryAsync(int protocolVersion)
    {
        using var harness = TestHarness.Create(protocolVersion: protocolVersion);
        await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000d1ec71",
            "local-bitcoin",
            945001);

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(
            CreateSampleRecordedShare("http"),
            "test");

        Assert.IsFalse(result.Accepted);
        StringAssert.Contains(result.RejectionReason, "outside the active Bitcoin tip");
        Assert.AreEqual(0, harness.StateService.GetNetworkStatus().WorkSetCount);
    }

    [TestMethod]
    public async Task DirectIngressPreservesAlreadyKnownPreBoundaryLineageAsync()
    {
        BootShareProof known = CreateValidatedProof(SampleHeaderHex, SamplePrevBlockHash, "seed-current");
        using var harness = TestHarness.Create(onDeckProofs: [known]);
        await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000d1ec72",
            "local-bitcoin",
            945001);

        ShareRecordingResult result = await harness.StateService.SubmitShareAsync(
            CreateSampleRecordedShare("http", "seed-current"),
            "test");

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("Duplicate share", result.RejectionReason);
        Assert.AreEqual(1, harness.StateService.GetNetworkStatus().WorkSetCount);
    }

    [TestMethod]
    public async Task V22SiblingBoundaryRaceConvergesWithoutBranchVotingAndV21DoesNotAutoUnionAsync()
    {
        (BootShareProof low, BootShareProof high) = CreateNonceRankedProofPair("seed-current");
        string boundary = "0000000000000000000000000000000000000000000000000000000000b02201";
        BootPayoutSnapshotContext predecessor = CreateSnapshotContext("seed-current", SampleExpectedWinners);

        using var alice = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            onDeckProofs: [low],
            snapshotContexts: [predecessor]);
        using var bob = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            onDeckProofs: [low, high],
            snapshotContexts: [predecessor]);
        await alice.StateService.ObserveChainTipAsync(boundary, "local-bitcoin", 945001);
        await bob.StateService.ObserveChainTipAsync(boundary, "local-bitcoin", 945001);
        BootStateBundle aliceSibling = alice.StateService.GetStateBundle(alice.StateService.GetNetworkStatus().CurrentStateId)!;
        BootStateBundle bobSibling = bob.StateService.GetStateBundle(bob.StateService.GetNetworkStatus().CurrentStateId)!;

        Assert.IsTrue(await alice.StateService.TryAdoptCurrentStateAsync(bobSibling, boundary, 945001, "bob"));
        Assert.IsTrue(await bob.StateService.TryAdoptCurrentStateAsync(aliceSibling, boundary, 945001, "alice"));
        CollectionAssert.AreEqual(
            alice.StateService.GetWinnersList().Select(payout => payout.Address).ToArray(),
            bob.StateService.GetWinnersList().Select(payout => payout.Address).ToArray());
        Assert.AreEqual(high.ShareId, alice.StateService.GetStateBundle(alice.StateService.GetNetworkStatus().CurrentStateId)!.ActiveSnapshotProofIds.Single());
        Assert.IsTrue(alice.StateService.GetNetworkStatus().ReconciliationCounters.PayoutChanges > 0);

        using var v21Alice = TestHarness.Create(
            protocolVersion: 21,
            sharedWinnerSlotCount: 1,
            onDeckProofs: [low],
            snapshotContexts: [predecessor]);
        using var v21Bob = TestHarness.Create(
            protocolVersion: 21,
            sharedWinnerSlotCount: 1,
            onDeckProofs: [low, high],
            snapshotContexts: [predecessor]);
        await v21Alice.StateService.ObserveChainTipAsync(boundary, "local-bitcoin", 945001);
        await v21Bob.StateService.ObserveChainTipAsync(boundary, "local-bitcoin", 945001);
        BootStateBundle v21Sibling = v21Bob.StateService.GetStateBundle(v21Bob.StateService.GetNetworkStatus().CurrentStateId)!;
        Assert.IsFalse(await v21Alice.StateService.TryAdoptCurrentStateAsync(v21Sibling, boundary, 945001, "v21-bob"));
        Assert.AreNotEqual(v21Alice.StateService.GetNetworkStatus().ActiveSnapshotId, v21Bob.StateService.GetNetworkStatus().ActiveSnapshotId);
    }

    [TestMethod]
    public async Task V22HeightActivationUsesV21BelowHeightAndMsrAtAndAboveHeightAsync()
    {
        BootShareProof proof = CreateValidatedProof(SampleHeaderHex, SamplePrevBlockHash, "seed-current");
        BootPayoutSnapshotContext predecessor = CreateSnapshotContext("seed-current", SampleExpectedWinners);
        using var harness = TestHarness.Create(
            currentTipBlockHeight: 945000,
            onDeckProofs: [proof],
            snapshotContexts: [predecessor],
            v22ActivationBlockHeight: 945002);

        BootNetworkStatusDto below = await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000a45001",
            "local-bitcoin",
            945001);
        Assert.AreEqual(21, below.ConsensusVersion);
        Assert.AreEqual(22, below.SoftwareConsensusVersion);
        Assert.AreEqual(1L, below.BlocksToV22Activation);
        Assert.AreEqual(BootProtocolVersions.V21StateBundleSchemaVersion, below.StateBundleSchemaVersion);
        Assert.AreEqual(string.Empty, below.ActiveSnapshotFamilyId);
        BootStateBundle belowBundle = harness.StateService.GetStateBundle(below.CurrentStateId)!;
        Assert.AreEqual(21, belowBundle.ConsensusVersion);

        BootNetworkStatusDto activated = await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000a45002",
            "local-bitcoin",
            945002);
        Assert.AreEqual(22, activated.ConsensusVersion);
        Assert.AreEqual(0L, activated.BlocksToV22Activation);
        Assert.AreEqual(BootProtocolVersions.StateBundleSchemaVersion, activated.StateBundleSchemaVersion);
        Assert.AreNotEqual(string.Empty, activated.ActiveSnapshotFamilyId);
        Assert.IsNotNull(harness.StateService.GetStateBundle(activated.CurrentStateId)!.SnapshotFamilyMember);
    }

    [TestMethod]
    public void V22ActivationHeightZeroIsImmediateAndMissingTipHeightFailsClosedOtherwise()
    {
        using var immediate = TestHarness.Create(seedUnknownTipHeight: true, v22ActivationBlockHeight: 0);
        using var missingHeight = TestHarness.Create(
            seedUnknownTipHeight: true,
            v22ActivationBlockHeight: 959500);
        using var persistedCurrentTipSeedsTrusted = TestHarness.Create(
            currentTipBlockHeight: 959499,
            seedUnknownTrustedTip: true,
            v22ActivationBlockHeight: 959500);

        Assert.AreEqual(22, immediate.StateService.GetNetworkStatus().ConsensusVersion);
        Assert.AreEqual(21, missingHeight.StateService.GetNetworkStatus().ConsensusVersion);
        Assert.IsNull(missingHeight.StateService.GetNetworkStatus().CurrentTipBlockHeight);
        Assert.IsNull(missingHeight.StateService.GetNetworkStatus().BlocksToV22Activation);
        // After restart, a persisted current tip with height is treated as the trusted local tip so
        // activation countdown/activation can proceed without waiting for the next ZMQ block.
        Assert.AreEqual(21, persistedCurrentTipSeedsTrusted.StateService.GetNetworkStatus().ConsensusVersion);
        Assert.AreEqual(959499L, persistedCurrentTipSeedsTrusted.StateService.GetNetworkStatus().V22ActivationTipBlockHeight);
        Assert.AreEqual(1L, persistedCurrentTipSeedsTrusted.StateService.GetNetworkStatus().BlocksToV22Activation);
    }

    [TestMethod]
    public void PeerCompatibilityFollowsActiveConsensusBeforeAndAfterV22Height()
    {
        using var upgradedPre = TestHarness.Create(currentTipBlockHeight: 945000, v22ActivationBlockHeight: 945001);
        using var legacy = TestHarness.Create(currentTipBlockHeight: 945000, protocolVersion: 21);
        using var upgradedPost = TestHarness.Create(currentTipBlockHeight: 945001, v22ActivationBlockHeight: 945001);
        using var upgradedPostPeer = TestHarness.Create(currentTipBlockHeight: 945001, v22ActivationBlockHeight: 945001);

        Assert.IsTrue(upgradedPre.StateService.EvaluatePeerCompatibility(legacy.StateService.GetNetworkStatus()).CanSyncState);
        Assert.IsTrue(legacy.StateService.EvaluatePeerCompatibility(upgradedPre.StateService.GetNetworkStatus()).CanSyncState);
        Assert.IsFalse(upgradedPost.StateService.EvaluatePeerCompatibility(legacy.StateService.GetNetworkStatus()).CanSyncState);
        Assert.IsFalse(legacy.StateService.EvaluatePeerCompatibility(upgradedPost.StateService.GetNetworkStatus()).CanSyncState);
        Assert.IsTrue(upgradedPost.StateService.EvaluatePeerCompatibility(upgradedPostPeer.StateService.GetNetworkStatus()).CanSyncState);
    }

    [TestMethod]
    public async Task V22OneBlockReorgCreatesIsolatedReplacementFamilyAndRestoresLineageAsync()
    {
        BootShareProof proof = CreateValidatedProof(SampleHeaderHex, SamplePrevBlockHash, "seed-current");
        BootPayoutSnapshotContext predecessor = CreateSnapshotContext("seed-current", SampleExpectedWinners);
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            onDeckProofs: [proof],
            snapshotContexts: [predecessor]);
        string removedBoundary = "0000000000000000000000000000000000000000000000000000000000a00501";
        string replacementBoundary = "0000000000000000000000000000000000000000000000000000000000b00501";

        BootNetworkStatusDto removed = await harness.StateService.ObserveChainTipAsync(
            removedBoundary,
            "local-bitcoin",
            945001);
        BootNetworkStatusDto replacement = await harness.StateService.ObserveChainTipAsync(
            replacementBoundary,
            "local-bitcoin-reorg",
            945001);

        Assert.AreNotEqual(removed.ActiveSnapshotFamilyId, replacement.ActiveSnapshotFamilyId);
        Assert.AreNotEqual(removed.ActiveSnapshotId, replacement.ActiveSnapshotId);
        Assert.AreEqual(replacementBoundary, replacement.CurrentTipBlockHash);
        Assert.AreEqual(1, replacement.WorkSetCount);
        Assert.AreEqual(2, replacement.CurrentRoundNumber);
    }

    [TestMethod]
    public async Task V22TwoBlockReorgRollsBackRemovedFamiliesBeforeReplacementAsync()
    {
        BootShareProof proof = CreateValidatedProof(SampleHeaderHex, SamplePrevBlockHash, "seed-current");
        BootPayoutSnapshotContext predecessor = CreateSnapshotContext("seed-current", SampleExpectedWinners);
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            onDeckProofs: [proof],
            snapshotContexts: [predecessor]);
        string removedFirst = "0000000000000000000000000000000000000000000000000000000000a00601";
        string removedSecond = "0000000000000000000000000000000000000000000000000000000000a00602";
        string replacementFirst = "0000000000000000000000000000000000000000000000000000000000b00601";

        await harness.StateService.ObserveChainTipAsync(removedFirst, "local-bitcoin", 945001);
        BootNetworkStatusDto removed = await harness.StateService.ObserveChainTipAsync(
            removedSecond,
            "local-bitcoin",
            945002);
        BootNetworkStatusDto replacement = await harness.StateService.ObserveChainTipAsync(
            replacementFirst,
            "local-bitcoin-reorg",
            945001);

        Assert.AreNotEqual(removed.ActiveSnapshotFamilyId, replacement.ActiveSnapshotFamilyId);
        Assert.AreEqual(replacementFirst, replacement.CurrentTipBlockHash);
        Assert.AreEqual(945001L, replacement.CurrentTipBlockHeight);
        Assert.AreEqual(1, replacement.WorkSetCount);
        Assert.AreEqual(2, replacement.CurrentRoundNumber);
    }

    [TestMethod]
    public async Task V22ReserveOnlySiblingAdditionDoesNotChangeActivePayoutSnapshotAsync()
    {
        (BootShareProof low, BootShareProof high) = CreateNonceRankedProofPair("seed-current");
        string boundary = "0000000000000000000000000000000000000000000000000000000000b02203";
        BootPayoutSnapshotContext predecessor = CreateSnapshotContext("seed-current", SampleExpectedWinners);
        using var local = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            onDeckProofs: [high],
            snapshotContexts: [predecessor]);
        using var remote = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            onDeckProofs: [high, low],
            snapshotContexts: [predecessor]);
        await local.StateService.ObserveChainTipAsync(boundary, "local-bitcoin", 945001);
        await remote.StateService.ObserveChainTipAsync(boundary, "local-bitcoin", 945001);
        string activeBefore = local.StateService.GetNetworkStatus().ActiveSnapshotId;
        BootStateBundle sibling = remote.StateService.GetStateBundle(remote.StateService.GetNetworkStatus().CurrentStateId)!;

        Assert.IsTrue(await local.StateService.TryAdoptCurrentStateAsync(sibling, boundary, 945001, "remote"));

        BootNetworkStatusDto after = local.StateService.GetNetworkStatus();
        Assert.AreEqual(activeBefore, after.ActiveSnapshotId);
        Assert.AreEqual(2, after.SnapshotFamilyUnionProofCount);
        Assert.AreEqual(1L, after.ReconciliationCounters.UnionAdditions);
        Assert.AreEqual(0L, after.ReconciliationCounters.PayoutChanges);
    }

    [TestMethod]
    public async Task CandidateImportMergesCurrentParentDivergentSnapshotProofsAsync()
    {
        string newTip = "0000000000000000000000000000000000000000000000000000000000f00d02";
        string currentParentHeader = RewriteHeaderPrevBlockHash(SampleHeaderHex, newTip);
        BootShareProof currentParentProof = CreateValidatedProof(currentParentHeader, newTip, "remote-snapshot");

        using var remoteHarness = TestHarness.Create(
            currentTipBlockHash: newTip,
            onDeckProofs: [currentParentProof],
            snapshotContexts: [CreateSnapshotContext("remote-snapshot", SampleExpectedWinners, newTip)]);
        BootNetworkStatusDto remoteStatus = remoteHarness.StateService.GetNetworkStatus();
        BootStateBundle candidateBundle = remoteHarness.StateService.GetStateBundle(remoteStatus.CandidateStateId)!;

        using var localHarness = TestHarness.Create(
            currentTipBlockHash: newTip,
            onDeckProofs: [],
            snapshotContexts: [CreateSnapshotContext("seed-current", SampleExpectedWinners, newTip)]);

        bool imported = await localHarness.StateService.TryImportCandidateStateAsync(
            candidateBundle,
            "https://peer.example");

        Assert.IsTrue(imported);
        BootNetworkStatusDto status = localHarness.StateService.GetNetworkStatus();
        Assert.AreEqual(1, status.WorkSetCount);
        Assert.AreEqual(localHarness.StateService.GetStateBundle(status.CandidateStateId)!.WorkSetProofs[0].ShareId, currentParentProof.ShareId);
    }

    [TestMethod]
    public async Task CandidateImportRejectsCurrentParentProofFromDifferentActiveSnapshotAsync()
    {
        string currentTip = "0000000000000000000000000000000000000000000000000000000000f00d03";

        string exclusionaryCoinbase = RewriteSlotZeroAddress(SampleCoinbaseHex, AlternateAddress);
        exclusionaryCoinbase = BuildCoinbaseWithWinnerPrefix(exclusionaryCoinbase, positiveWinnerCount: 1);
        exclusionaryCoinbase = BuildCoinbaseWithMutatedFirstWinnerScript(exclusionaryCoinbase);
        IReadOnlyList<PayoutInfo> exclusionaryWinners = BuildExpectedWinners(exclusionaryCoinbase);
        Assert.AreEqual(1, exclusionaryWinners.Count);
        Assert.AreEqual(AlternateAddress, BitcoinScript.NormalizeAddress(exclusionaryWinners[0].Address));

        string exclusionaryHeader = RewriteHeaderMerkleRoot(SampleHeaderHex, exclusionaryCoinbase);
        exclusionaryHeader = RewriteHeaderPrevBlockHash(exclusionaryHeader, currentTip);
        BootShareProof attackerProof = CreateValidatedProofForSnapshot(
            exclusionaryHeader,
            exclusionaryCoinbase,
            currentTip,
            "attacker-exclusionary-snapshot",
            exclusionaryWinners);
        Assert.AreEqual(AlternateAddress, attackerProof.MinerAddress);

        BootPayoutSnapshotContext attackerContext = CreateSnapshotContext(
            "attacker-exclusionary-snapshot",
            exclusionaryWinners,
            currentTip);
        using var attackerHarness = TestHarness.Create(
            currentTipBlockHash: currentTip,
            winnersList: exclusionaryWinners,
            onDeckProofs: [attackerProof],
            snapshotContexts: [attackerContext],
            activeSnapshotId: attackerContext.SnapshotId,
            workSetReserveMultiplier: 1);
        BootNetworkStatusDto attackerStatus = attackerHarness.StateService.GetNetworkStatus();
        BootStateBundle attackerCandidate = attackerHarness.StateService.GetStateBundle(attackerStatus.CandidateStateId)!;

        BootPayoutSnapshotContext inclusiveContext = CreateSnapshotContext(
            "inclusive-snapshot",
            SampleExpectedWinners,
            currentTip);
        using var inclusiveHarness = TestHarness.Create(
            currentTipBlockHash: currentTip,
            currentStateId: inclusiveContext.SnapshotId,
            winnersList: SampleExpectedWinners,
            snapshotContexts: [inclusiveContext],
            activeSnapshotId: inclusiveContext.SnapshotId,
            workSetReserveMultiplier: 1);

        bool imported = await inclusiveHarness.StateService.TryImportCandidateStateAsync(
            attackerCandidate,
            "https://selective-peer.example");

        Assert.IsFalse(imported, "Candidate state IDs are anchored to the active snapshot and must prevent cross-active-state credit.");
        BootNetworkStatusDto unchangedStatus = inclusiveHarness.StateService.GetNetworkStatus();
        Assert.AreEqual(inclusiveContext.SnapshotId, unchangedStatus.ActiveSnapshotId);
        Assert.AreEqual(0, unchangedStatus.WorkSetCount);
    }

    [TestMethod]
    public void WorkSetAdmissionDifficultyUsesReserveFloorWhenFull()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress)
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 2,
            workSetReserveMultiplier: 1,
            onDeckProofs: seedProofs);

        double admissionDifficulty = harness.StateService.GetWorkSetAdmissionDifficulty();

        Assert.AreEqual(Math.BitIncrement(50d), admissionDifficulty);
    }

    [TestMethod]
    public void DatumTelemetryShareUpdatesLocalHashrateWithoutMutatingWorkSet()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress)
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 2,
            workSetReserveMultiplier: 1,
            onDeckProofs: seedProofs);
        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();

        ShareRecordingResult result = new();
        DateTime startedUtc = DateTime.UtcNow;
        for (int i = 0; i < 8; i++)
        {
            result = harness.StateService.RecordDatumTelemetryShare(
                AlternateAddress,
                $"{AlternateAddress}.worker",
                difficulty: 10 + i,
                timestampUtc: startedUtc.AddMilliseconds(i));
        }
        BootNetworkStatusDto after = harness.StateService.GetNetworkStatus();

        Assert.IsTrue(result.Accepted);
        Assert.IsFalse(result.AffectedOnDeck);
        Assert.AreEqual(before.WorkSetCount, after.WorkSetCount);
        Assert.AreEqual(before.CandidateStateId, after.CandidateStateId);
        Assert.AreEqual(8, after.LocalDatumDiagnostics.TotalSubmissions);
        Assert.AreEqual(8, after.LocalDatumDiagnostics.AcceptedCount);
        Assert.AreEqual(0, after.LocalDatumDiagnostics.AcceptedOnDeckCount);
        Assert.AreEqual(0, after.LocalDatumDiagnostics.RejectedCount);
        Assert.IsTrue(after.LocalDatumMiners.Any(miner =>
            string.Equals(miner.Address, AlternateAddress, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void LocalMiningTelemetryReportsSourcesAndDeduplicatesRetriedWindows()
    {
        using var harness = TestHarness.Create();
        DateTime windowEndUtc = DateTime.UtcNow;
        DateTime windowStartUtc = windowEndUtc.AddMinutes(-10);
        double fiftyThWorkDifficulty = 50d * 1_000_000_000_000d * 600d / 4294967296d;
        var batch = new LocalMiningTelemetryBatchDto
        {
            Entries =
            [
                new LocalMiningTelemetryEntryDto
                {
                    ChannelId = "channel-1",
                    PayoutAddress = AlternateAddress,
                    Username = "worker-a",
                    WindowStartUtc = windowStartUtc,
                    WindowEndUtc = windowEndUtc,
                    AcceptedShareCount = 20,
                    AcceptedWorkDifficulty = fiftyThWorkDifficulty,
                    BestDifficulty = 1_000_000
                }
            ]
        };

        LocalMiningTelemetryResultDto first = harness.StateService.RecordLocalMiningTelemetryBatch(batch, "ckpool");
        LocalMiningTelemetryResultDto retry = harness.StateService.RecordLocalMiningTelemetryBatch(batch, "ckpool");
        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        BootLocalMiningSourceSummaryDto source = status.LocalMiningSources.Single(item => item.Source == "ckpool");

        Assert.AreEqual(1, first.AcceptedEntries);
        Assert.AreEqual(0, retry.AcceptedEntries);
        Assert.AreEqual(1, source.ActiveMinerCount);
        Assert.AreEqual(20L, source.RecentAcceptedShareCount);
        Assert.AreEqual("reported-work", source.EstimationMethod);
        Assert.IsTrue(source.CurrentHashrateThs is > 45 and < 55, source.CurrentHashrateDisplay);
        Assert.IsTrue(status.LocalMiningHashrateThs is > 45 and < 55, status.LocalMiningHashrateDisplay);
    }

    [TestMethod]
    public void LocalHashrateEstimatorIgnoresWindowOpeningLuckyShare()
    {
        using var harness = TestHarness.Create();
        DateTime startedUtc = DateTime.UtcNow.AddMinutes(-10);
        harness.StateService.RecordDatumTelemetryShare(
            AlternateAddress,
            "worker-a",
            difficulty: 1_000_000_000_000,
            timestampUtc: startedUtc);
        for (int i = 1; i <= 8; i++)
        {
            harness.StateService.RecordDatumTelemetryShare(
                AlternateAddress,
                "worker-a",
                difficulty: 1_000,
                timestampUtc: startedUtc.AddSeconds(i * 60));
        }

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        BootLocalMiningSourceSummaryDto source = status.LocalMiningSources.Single(item => item.Source == "datum");

        Assert.AreEqual("proof-order-statistic", source.EstimationMethod);
        Assert.IsTrue(source.CurrentHashrateThs is > 0 and < 1, source.CurrentHashrateDisplay);
    }

    [TestMethod]
    public void LocalMiningClientApiGaugeOverridesProofEstimate()
    {
        using var harness = TestHarness.Create();
        harness.StateService.RecordDatumTelemetryShare(
            AlternateAddress,
            "worker-a",
            difficulty: 1_000_000_000_000,
            timestampUtc: DateTime.UtcNow.AddMinutes(-5));

        harness.StateService.RecordLocalMiningSourceGauge(
            "datum",
            hashrateThs: 3.25,
            activeMinerCount: 1,
            observedUtc: DateTime.UtcNow);

        BootLocalMiningSourceSummaryDto source = harness.StateService.GetNetworkStatus()
            .LocalMiningSources.Single(item => item.Source == "datum");

        Assert.AreEqual("client-api", source.EstimationMethod);
        Assert.AreEqual(3.25, source.CurrentHashrateThs);
        Assert.AreEqual(1, source.ActiveMinerCount);
    }

    [TestMethod]
    public void LocalMiningTelemetryRetentionIsIndependentPerSource()
    {
        using var harness = TestHarness.Create();
        DateTime startedUtc = DateTime.UtcNow.AddMinutes(-5);

        harness.StateService.RecordLocalMiningTelemetryBatch(
            BuildTelemetryBatch("ck-channel", startedUtc, startedUtc.AddSeconds(1), 100),
            "ckpool");

        for (int i = 0; i < 520; i++)
        {
            harness.StateService.RecordLocalMiningTelemetryBatch(
                BuildTelemetryBatch(
                    $"hydra-{i}",
                    startedUtc.AddSeconds(i + 2),
                    startedUtc.AddSeconds(i + 3),
                    10),
                "hydrapool");
        }

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.IsTrue(status.LocalMiningSources.Any(source => source.Source == "ckpool"));
        Assert.IsTrue(status.LocalMiningSources.Any(source => source.Source == "hydrapool"));
    }

    [TestMethod]
    public void LocalMiningTelemetryNormalizesOffsetTimestampsToUtc()
    {
        using var harness = TestHarness.Create();
        DateTime windowEndLocal = DateTime.UtcNow.AddSeconds(-1).ToLocalTime();
        LocalMiningTelemetryBatchDto batch = BuildTelemetryBatch(
            "ck-offset",
            windowEndLocal.AddSeconds(-10),
            windowEndLocal,
            100);

        harness.StateService.RecordLocalMiningTelemetryBatch(batch, "ckpool");

        Assert.AreEqual(DateTimeKind.Utc, batch.Entries[0].WindowStartUtc.Kind);
        Assert.AreEqual(DateTimeKind.Utc, batch.Entries[0].WindowEndUtc.Kind);
        Assert.IsTrue(harness.StateService.GetNetworkStatus()
            .LocalMiningSources.Any(source => source.Source == "ckpool"));
    }

    private static LocalMiningTelemetryBatchDto BuildTelemetryBatch(
        string channelId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        double acceptedDifficulty)
    {
        return new LocalMiningTelemetryBatchDto
        {
            Entries =
            [
                new LocalMiningTelemetryEntryDto
                {
                    ChannelId = channelId,
                    PayoutAddress = AlternateAddress,
                    Username = channelId,
                    WindowStartUtc = windowStartUtc,
                    WindowEndUtc = windowEndUtc,
                    AcceptedShareCount = 1,
                    AcceptedWorkDifficulty = acceptedDifficulty,
                    BestDifficulty = acceptedDifficulty
                }
            ]
        };
    }

    [TestMethod]
    public async Task BitcoinBlockSnapshotUpdatesActiveWinnersWithoutRemovingWorkSetProofsAsync()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress)
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 2,
            onDeckProofs: seedProofs);

        string previousSnapshotId = harness.StateService.GetNetworkStatus().ActiveSnapshotId;
        BootNetworkStatusDto status = await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000aaa001",
            "test-chain-tip",
            945001);

        Assert.AreEqual(seedProofs.Length, status.WorkSetCount);
        Assert.AreEqual(seedProofs.Length, status.ActiveSnapshotProofCount);
        Assert.AreNotEqual(previousSnapshotId, status.ActiveSnapshotId);
        CollectionAssert.AreEqual(
            new[] { SampleSlotZeroAddress, AlternateAddress },
            harness.StateService.GetWinnersList().Select(payout => payout.Address).ToArray());
        Assert.AreEqual(seedProofs.Length, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task TeamHashrateEstimateUsesWorkSetAgeAcrossBitcoinBlockSnapshotsAsync()
    {
        DateTime proofStartUtc = DateTime.UtcNow.AddHours(-1);
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 300_000_000, SampleSlotZeroAddress, timestampUtc: proofStartUtc),
            CreateFakeProof("proof-b", 200_000_000, AlternateAddress, timestampUtc: proofStartUtc.AddMinutes(5)),
            CreateFakeProof("proof-c", 100_000_000, SampleSlotZeroAddress, timestampUtc: proofStartUtc.AddMinutes(10))
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 3,
            onDeckProofs: seedProofs);

        await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000aaa201",
            "test-chain-tip",
            945101);

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();

        Assert.IsTrue(status.CurrentRoundElapsedSeconds is < 60,
            "The visible snapshot age should still reflect the most recent Bitcoin-block snapshot.");
        Assert.IsTrue(status.CurrentRoundObservedHashrateThs is > 250 and < 600,
            $"Expected Work Set age to anchor team hashrate near hundreds of TH/s, got {status.CurrentRoundObservedHashrateDisplay}.");
    }

    [TestMethod]
    public async Task GridPoolPaymentRemovesOnlyPaidSnapshotProofsAndKeepsReserveProofsAsync()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress),
            CreateFakeProof("proof-c", 25, SampleSlotZeroAddress)
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            workSetReserveMultiplier: 3,
            onDeckProofs: seedProofs);

        await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000aaa101",
            "test-chain-tip",
            945001);

        RoundRotationResult rotation = await harness.StateService.RotateToNextRoundAsync(
            "0000000000000000000000000000000000000000000000000000000000aaa102",
            "test-gridpool-block",
            manual: false,
            blockHeight: 945002,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsTrue(rotation.Rotated, rotation.Reason);
        Assert.AreEqual(2, rotation.NetworkStatus.WorkSetCount);
        Assert.AreEqual(1, rotation.NetworkStatus.ActiveSnapshotProofCount);
        Assert.AreEqual(rotation.LockedStateBundle!.PaidSnapshotProofIds.Count, 1);
        Assert.AreEqual("proof-a", rotation.LockedStateBundle.PaidSnapshotProofIds[0]);
        CollectionAssert.AreEqual(
            new[] { "proof-b", "proof-c" },
            rotation.LockedStateBundle.WorkSetProofs.Select(proof => proof.ShareId).ToArray());
        Assert.AreEqual(AlternateAddress, harness.StateService.GetWinnersList()[0].Address);
    }

    [TestMethod]
    public async Task GridPoolPaymentUsesCoinbaseProvenSiblingSnapshotInsteadOfCurrentActiveSnapshotAsync()
    {
        BootShareProof proofA = CreateFakeProof("proof-a", 100, SampleSlotZeroAddress, "snapshot-a");
        BootShareProof proofB = CreateFakeProof("proof-b", 90, AlternateAddress, "snapshot-b");
        BootPayoutSnapshotContext contextA = CreateSnapshotContext("snapshot-a", [ClonePayout(SampleExpectedWinners[0])]);
        contextA.ProofIds = [proofA.ShareId];
        BootPayoutSnapshotContext contextB = CreateSnapshotContext("snapshot-b", [ClonePayout(SampleExpectedWinners[0])]);
        contextB.ProofIds = [proofB.ShareId];

        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            winnersList: contextB.WinnersList,
            onDeckProofs: [proofA, proofB],
            snapshotContexts: [contextA, contextB],
            activeSnapshotId: contextB.SnapshotId,
            activeSnapshotProofIds: contextB.ProofIds);

        RoundRotationResult rotation = await harness.StateService.RotateToNextRoundAsync(
            "0000000000000000000000000000000000000000000000000000000000c02202",
            "validated-gridpool-block",
            manual: false,
            blockHeight: 945001,
            provenSnapshotId: contextA.SnapshotId,
            localBitcoinActiveChainConfirmed: true);

        Assert.AreEqual(contextA.SnapshotId, rotation.LockedStateBundle!.PaidSnapshotId);
        CollectionAssert.AreEqual(new[] { proofA.ShareId }, rotation.LockedStateBundle.PaidSnapshotProofIds.ToArray());
        CollectionAssert.AreEqual(new[] { proofB.ShareId }, rotation.LockedStateBundle.WorkSetProofs.Select(proof => proof.ShareId).ToArray());
    }

    [TestMethod]
    public async Task ConsecutiveGridPoolBlocksWalkDeeperIntoReserveAsync()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress),
            CreateFakeProof("proof-c", 25, SampleSlotZeroAddress)
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 1,
            workSetReserveMultiplier: 3,
            onDeckProofs: seedProofs);

        await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000bbb101",
            "test-chain-tip",
            945001);
        await harness.StateService.RotateToNextRoundAsync(
            "0000000000000000000000000000000000000000000000000000000000bbb102",
            "test-gridpool-block",
            manual: false,
            blockHeight: 945002,
            localBitcoinActiveChainConfirmed: true);
        RoundRotationResult secondPayment = await harness.StateService.RotateToNextRoundAsync(
            "0000000000000000000000000000000000000000000000000000000000bbb103",
            "test-gridpool-block",
            manual: false,
            blockHeight: 945003,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsTrue(secondPayment.Rotated, secondPayment.Reason);
        Assert.AreEqual(1, secondPayment.NetworkStatus.WorkSetCount);
        Assert.AreEqual("proof-b", secondPayment.LockedStateBundle!.PaidSnapshotProofIds[0]);
        Assert.AreEqual("proof-c", secondPayment.LockedStateBundle.WorkSetProofs[0].ShareId);
        Assert.AreEqual(SampleSlotZeroAddress, harness.StateService.GetWinnersList()[0].Address);
    }

    [TestMethod]
    public async Task SupportFeeSnapshotsUseCanonicalSlotAndFeeFreeSnapshotsUseAllProofSlotsAsync()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, SampleSlotZeroAddress),
            CreateFakeProof("proof-c", 25, SampleSlotZeroAddress)
        ];

        using var feeHarness = TestHarness.Create(
            sharedWinnerSlotCount: 3,
            supportFeeEnabled: true,
            onDeckProofs: seedProofs);
        await feeHarness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000ccc101",
            "test-chain-tip",
            945001);

        List<PayoutInfo> feeWinners = feeHarness.StateService.GetWinnersList();
        ulong expectedSlotValue = BootProtocolStateService.GetCurrentBlockSubsidySats(feeHarness.Config.BitcoinNetwork) /
                                  (ulong)feeHarness.Config.TotalPayoutSlotCount;
        Assert.AreEqual(3, feeWinners.Count);
        Assert.AreEqual(AlternateAddress, feeWinners[0].Address);
        Assert.AreEqual("Grid Labs support", feeWinners[0].Username);
        Assert.AreEqual(expectedSlotValue, feeWinners[0].Value);
        Assert.AreEqual(100, feeWinners[1].Difficulty);
        Assert.AreEqual(50, feeWinners[2].Difficulty);

        using var feeFreeHarness = TestHarness.Create(
            sharedWinnerSlotCount: 3,
            supportFeeEnabled: false,
            onDeckProofs: seedProofs);
        await feeFreeHarness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000ccc201",
            "test-chain-tip",
            945001);

        List<PayoutInfo> feeFreeWinners = feeFreeHarness.StateService.GetWinnersList();
        Assert.AreEqual(3, feeFreeWinners.Count);
        Assert.AreEqual(100, feeFreeWinners[0].Difficulty);
        Assert.AreEqual(50, feeFreeWinners[1].Difficulty);
        Assert.AreEqual(25, feeFreeWinners[2].Difficulty);
        Assert.IsFalse(string.Equals(feeFreeWinners[0].Username, "Grid Labs support", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CoinbaseOutputsCanBeServedUncondensedForFirmwareStressTesting()
    {
        var winners = new List<PayoutInfo>
        {
            new() { Address = SampleSlotZeroAddress, Username = "a", Value = 1000, Difficulty = 100 },
            new() { Address = SampleSlotZeroAddress, Username = "a", Value = 1000, Difficulty = 90 },
            new() { Address = AlternateAddress, Username = "b", Value = 1000, Difficulty = 80 }
        };

        using var harness = TestHarness.Create(winnersList: winners);

        List<PayoutInfo> condensed = harness.StateService.GetCoinbaseOutputs();
        Assert.AreEqual(2, condensed.Count);
        Assert.AreEqual(2000UL, condensed.Single(payout => payout.Address == SampleSlotZeroAddress).Value);

        harness.Config.CoinbaseUncondensedOutputsEnabled = true;
        List<PayoutInfo> uncondensed = harness.StateService.GetCoinbaseOutputs();

        Assert.AreEqual(3, uncondensed.Count);
        Assert.AreEqual(2, uncondensed.Count(payout => payout.Address == SampleSlotZeroAddress));
        Assert.IsTrue(uncondensed.All(payout => payout.Value == 1000));
    }

    [TestMethod]
    public void Sv2WorkSelectionReturnsConsensusSerializedCoinbaseOutputs()
    {
        var winners = new List<PayoutInfo>
        {
            new() { Address = SampleSlotZeroAddress, Username = "a", Value = 1000, Difficulty = 100 },
            new() { Address = SampleSlotZeroAddress, Username = "a", Value = 2000, Difficulty = 90 },
            new() { Address = AlternateAddress, Username = "b", Value = 3000, Difficulty = 80 }
        };

        using var harness = TestHarness.Create(winnersList: winners);

        Sv2WorkSelectionDto response = harness.StateService.GetSv2WorkSelectionResponse();
        Sv2WorkSelectionDto repeated = harness.StateService.GetSv2WorkSelectionResponse();

        Assert.AreEqual(1, response.SchemaVersion);
        Assert.AreEqual(64, response.PlanId.Length);
        Assert.AreEqual(response.PlanId, repeated.PlanId);
        Assert.AreEqual("coinbase-only", response.Mode);
        Assert.AreEqual(Math.Max(1d, harness.Config.PulseMinDifficulty), response.MinimumPulseDifficulty);
        Assert.AreEqual(harness.Config.BootNetworkId, response.NetworkId);
        Assert.AreEqual(harness.Config.BitcoinNetwork, response.BitcoinNetwork);
        Assert.AreEqual(harness.Config.BootProtocolVersion, response.ProtocolVersion);
        Assert.AreEqual("seed-current", response.ActiveSnapshotId);
        Assert.AreEqual(2, response.CoinbaseOutputCount);
        Assert.AreEqual(2, response.CoinbaseOutputs.Count);

        string expectedHex = "02" + string.Concat(response.CoinbaseOutputs.Select(output => output.OutputHex));
        Assert.AreEqual(expectedHex, response.CoinbaseTxOutputsHex);
        Assert.AreEqual(Convert.FromHexString(expectedHex).Length, response.CoinbaseTxOutputsBytes);
        Assert.AreEqual(3000UL, response.CoinbaseOutputs.Single(output => output.Address == SampleSlotZeroAddress).Value);
        Assert.AreEqual(3000UL, response.CoinbaseOutputs.Single(output => output.Address == AlternateAddress).Value);

        Assert.IsInstanceOfType<OkObjectResult>(harness.MiningController.GetWorkPlan());

        harness.Config.CoinbaseUncondensedOutputsEnabled = true;
        Sv2WorkSelectionDto uncondensed = harness.StateService.GetSv2WorkSelectionResponse();
        Assert.AreNotEqual(response.PlanId, uncondensed.PlanId);
    }

    [TestMethod]
    public void BitcoinTransactionSerializationUsesCompactSizeForLargeSv2OutputSets()
    {
        byte[] script = BitcoinScript.AddressToScriptPubKey(SampleSlotZeroAddress, BitcoinScript.Mainnet);
        var outputs = Enumerable.Range(0, 300)
            .Select(_ => (Value: 1UL, ScriptPubKey: script))
            .ToList();

        byte[] serialized = BitcoinTransactionSerialization.SerializeTxOutputs(outputs);

        Assert.AreEqual(0xfd, serialized[0]);
        Assert.AreEqual(300, BinaryPrimitives.ReadUInt16LittleEndian(serialized.AsSpan(1, 2)));
        Assert.AreEqual(3 + (8 + 1 + script.Length) * 300, serialized.Length);
    }

    [TestMethod]
    public async Task SupportFeePaymentRemovesOnlyActuallyPaidSharedProofsAsync()
    {
        BootShareProof[] seedProofs =
        [
            CreateFakeProof("proof-a", 100, SampleSlotZeroAddress),
            CreateFakeProof("proof-b", 50, AlternateAddress),
            CreateFakeProof("proof-c", 25, SampleSlotZeroAddress)
        ];
        using var harness = TestHarness.Create(
            sharedWinnerSlotCount: 3,
            supportFeeEnabled: true,
            workSetReserveMultiplier: 3,
            onDeckProofs: seedProofs);

        BootNetworkStatusDto snapshot = await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000ccc301",
            "test-chain-tip",
            945001);
        Assert.AreEqual(2, snapshot.ActiveSnapshotProofCount);

        RoundRotationResult payment = await harness.StateService.RotateToNextRoundAsync(
            "0000000000000000000000000000000000000000000000000000000000ccc302",
            "test-gridpool-block",
            manual: false,
            blockHeight: 945002,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsTrue(payment.Rotated, payment.Reason);
        CollectionAssert.AreEqual(
            new[] { "proof-a", "proof-b" },
            payment.LockedStateBundle!.PaidSnapshotProofIds.ToArray());
        CollectionAssert.AreEqual(
            new[] { "proof-c" },
            payment.LockedStateBundle.WorkSetProofs.Select(proof => proof.ShareId).ToArray());
    }

    [TestMethod]
    public async Task ShareMinedAgainstRetainedOldSnapshotContextValidatesAfterLaterSnapshotsAsync()
    {
        using var harness = TestHarness.Create(
            workSetReserveMultiplier: 1,
            onDeckProofs: [CreateFakeProof("context-anchor", 100, SampleSlotZeroAddress, "seed-current")]);
        string oldSnapshotId = harness.StateService.GetNetworkStatus().ActiveSnapshotId;

        for (int index = 1; index <= 40; index++)
        {
            await harness.StateService.ObserveChainTipAsync(
                $"00000000000000000000000000000000000000000000000000000000ddd{index:x4}",
                "test-chain-tip",
                945000 + index);
        }

        ShareSubmissionDto oldSnapshotShare = CreateSampleShareDto();
        oldSnapshotShare.PayoutSnapshotId = oldSnapshotId;
        IActionResult response = await harness.MiningController.SubmitShare(oldSnapshotShare);
        JsonObject payload = ParseObjectResult(response, StatusCodes.Status200OK);

        Assert.AreEqual("accepted", payload["status"]?.GetValue<string>());
        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        Assert.AreEqual(2, status.WorkSetCount);
    }

    [TestMethod]
    public async Task CandidateBundleIncludesSnapshotContextsForAllUnpaidWorkSetProofsAsync()
    {
        using var harness = TestHarness.Create(
            workSetReserveMultiplier: 1,
            onDeckProofs: [CreateFakeProof("context-anchor", 100, SampleSlotZeroAddress, "seed-current")]);

        for (int index = 1; index <= 40; index++)
        {
            await harness.StateService.ObserveChainTipAsync(
                $"00000000000000000000000000000000000000000000000000000000eee{index:x4}",
                "test-chain-tip",
                946000 + index);
        }

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        BootStateBundle? bundle = harness.StateService.GetStateBundle(status.CandidateStateId);

        Assert.IsNotNull(bundle);
        HashSet<string> contextIds = bundle.SnapshotContexts
            .Select(context => context.SnapshotId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> missingContextIds = bundle.WorkSetProofs
            .Select(proof => proof.PayoutSnapshotId ?? string.Empty)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !contextIds.Contains(id))
            .ToList();
        Assert.AreEqual(0, missingContextIds.Count, $"Missing bundled context(s): {string.Join(", ", missingContextIds)}");
    }

    [TestMethod]
    public void LoadDropsUnrecoverableWorkSetProofsMissingSnapshotContext()
    {
        BootShareProof unrecoverableProof = CreateFakeProof(
            "solo-fallback-contextless",
            100,
            SampleSlotZeroAddress,
            "missing-solo-context");
        unrecoverableProof.CoinbaseHex = BuildCoinbaseWithOnlySlotZero(SampleCoinbaseHex);

        using var harness = TestHarness.Create(
            workSetReserveMultiplier: 1,
            onDeckProofs: [unrecoverableProof]);

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        BootStateBundle? bundle = harness.StateService.GetStateBundle(status.CandidateStateId);

        Assert.AreEqual(0, status.WorkSetCount);
        Assert.IsNotNull(bundle);
        Assert.AreEqual(0, bundle.WorkSetProofs.Count);
    }

    [TestMethod]
    public void LoadDropsWorkSetProofsThatDoNotValidateAgainstRecoveredSnapshotContext()
    {
        const string staleSnapshotId = "stale-recovered-context";
        string fallbackCoinbaseHex = BuildCoinbaseWithOnlySlotZero(SampleCoinbaseHex);
        BootShareProof staleProof = CreateFakeProof(
            "stale-proof-with-context",
            100,
            SampleSlotZeroAddress,
            staleSnapshotId);
        staleProof.CoinbaseHex = fallbackCoinbaseHex;
        staleProof.HeaderHex = RewriteHeaderMerkleRoot(SampleHeaderHex, fallbackCoinbaseHex);

        var staleContext = new BootPayoutSnapshotContext
        {
            SnapshotId = staleSnapshotId,
            CurrentRoundNumber = 1,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            PayoutVariant = "recovered-from-local-proof",
            WinnersList = SampleExpectedWinners.Select(ClonePayout).ToList(),
            FeeFreeWinnersList = SampleExpectedWinners.Select(ClonePayout).ToList(),
            ProofIds = [staleProof.ShareId]
        };

        using var harness = TestHarness.Create(
            workSetReserveMultiplier: 1,
            onDeckProofs: [staleProof],
            snapshotContexts: [staleContext]);

        BootNetworkStatusDto status = harness.StateService.GetNetworkStatus();
        BootStateBundle? bundle = harness.StateService.GetStateBundle(status.CandidateStateId);

        Assert.AreEqual(0, status.WorkSetCount);
        Assert.IsNotNull(bundle);
        Assert.AreEqual(0, bundle.WorkSetProofs.Count);
    }

    [TestMethod]
    public async Task PeerCanImportCandidateStateWithLongLivedReserveProofsAsync()
    {
        using var remoteHarness = TestHarness.Create(workSetReserveMultiplier: 1);
        ShareRecordingResult seedResult = await remoteHarness.StateService.SubmitShareAsync(
            new RecordedShareSubmission
            {
                MinerAddress = SampleSlotZeroAddress,
                Username = string.Empty,
                HeaderHex = SampleHeaderHex,
                CoinbaseHex = SampleCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "datum"
            },
            "datum-block");
        Assert.IsTrue(seedResult.Accepted, seedResult.RejectionReason);

        for (int index = 1; index <= 40; index++)
        {
            await remoteHarness.StateService.ObserveChainTipAsync(
                $"00000000000000000000000000000000000000000000000000000000abc{index:x4}",
                "test-chain-tip",
                947000 + index);
        }

        BootNetworkStatusDto remoteStatus = remoteHarness.StateService.GetNetworkStatus();
        BootStateBundle currentBundle = remoteHarness.StateService.GetStateBundle(remoteStatus.CurrentStateId)!;
        BootStateBundle candidateBundle = remoteHarness.StateService.GetStateBundle(remoteStatus.CandidateStateId)!;

        using var localHarness = TestHarness.Create(
            currentTipBlockHash: remoteStatus.CurrentTipBlockHash,
            currentRoundNumber: remoteStatus.CurrentRoundNumber,
            currentStateId: remoteStatus.CurrentStateId,
            winnersList: currentBundle.WinnersList,
            activeSnapshotId: currentBundle.ActiveSnapshotId,
            activeSnapshotProofIds: currentBundle.ActiveSnapshotProofIds,
            snapshotContexts: currentBundle.SnapshotContexts,
            workSetReserveMultiplier: 1);

        bool imported = await localHarness.StateService.TryImportCandidateStateAsync(
            candidateBundle,
            "https://peer.example");

        Assert.IsTrue(imported);
        Assert.AreEqual(remoteStatus.CandidateStateId, localHarness.StateService.GetNetworkStatus().CandidateStateId);
    }

    [TestMethod]
    public async Task LowerHeightChainTipObservationIsIgnoredWithoutRegressingTipAsync()
    {
        using var harness = TestHarness.Create();
        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();

        BootNetworkStatusDto after = await harness.StateService.ObserveChainTipAsync(
            "0000000000000000000000000000000000000000000000000000000000bad001",
            "test-stale-tip",
            before.CurrentTipBlockHeight - 1);

        Assert.AreEqual(before.CurrentTipBlockHash, after.CurrentTipBlockHash);
        Assert.AreEqual(before.CurrentTipBlockHeight, after.CurrentTipBlockHeight);

        BootNetworkEventSeriesDto staleEvents = harness.StateService.GetNetworkEvents(eventType: "chain-tip-stale");
        Assert.AreEqual(1, staleEvents.Events.Count);
        Assert.AreEqual(before.CurrentTipBlockHeight - 1, staleEvents.Events[0].BlockHeight);
    }

    [TestMethod]
    public async Task LowerHeightRoundRotationIsIgnoredWithoutAdvancingRoundAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult shareResponse = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject sharePayload = ParseObjectResult(shareResponse, StatusCodes.Status200OK);
        Assert.AreEqual("accepted", sharePayload["status"]?.GetValue<string>());

        BootNetworkStatusDto before = harness.StateService.GetNetworkStatus();
        RoundRotationResult rotation = await harness.StateService.RotateToNextRoundAsync(
            "0000000000000000000000000000000000000000000000000000000000bad002",
            "test-stale-block",
            manual: false,
            blockHeight: before.CurrentTipBlockHeight - 1,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsFalse(rotation.Rotated);
        Assert.AreEqual("Stale block notification", rotation.Reason);
        Assert.AreEqual(before.CurrentRoundNumber, rotation.NetworkStatus.CurrentRoundNumber);
        Assert.AreEqual(before.CurrentTipBlockHash, rotation.NetworkStatus.CurrentTipBlockHash);
        Assert.AreEqual(before.CurrentTipBlockHeight, rotation.NetworkStatus.CurrentTipBlockHeight);
        Assert.AreEqual(1, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task RotationPreservesImmediatePreviousParentInAcceptedParentSetAsync()
    {
        using var harness = TestHarness.Create();

        IActionResult shareResponse = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject sharePayload = ParseObjectResult(shareResponse, StatusCodes.Status200OK);
        Assert.AreEqual("accepted", sharePayload["status"]?.GetValue<string>());

        const string newBlockHash = "0000000000000000000000000000000000000000000000000000000000def456";
        RoundRotationResult rotation = await harness.StateService.RotateToNextRoundAsync(
            newBlockHash,
            "test-block",
            manual: false,
            blockHeight: 945002,
            localBitcoinActiveChainConfirmed: true);

        Assert.IsTrue(rotation.Rotated);

        Thread.Sleep(1200);

        PoolState persisted = JsonSerializer.Deserialize<PoolState>(File.ReadAllText(harness.StatePath))!;
        CollectionAssert.Contains(persisted.AcceptedParentBlockHashes, SamplePrevBlockHash);
        CollectionAssert.Contains(persisted.AcceptedParentBlockHashes, newBlockHash);
    }

    [TestMethod]
    public async Task SlotZeroMutationWithRecomputedHeaderButLowPowIsRejectedAsync()
    {
        using var harness = TestHarness.Create();
        (string mutatedHeaderHex, string mutatedCoinbaseHex) = BuildLowDifficultySlotZeroMutation(
            SampleHeaderHex,
            SampleCoinbaseHex,
            SampleMerklePath,
            AlternateAddress);

        IActionResult response = await harness.MiningController.SubmitShare(new ShareSubmissionDto
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = mutatedHeaderHex,
            CoinbaseHex = mutatedCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash
        });

        JsonObject payload = ParseObjectResult(response, StatusCodes.Status400BadRequest);

        Assert.AreEqual("rejected", payload["status"]?.GetValue<string>());
        Assert.AreEqual("Low difficulty", payload["reason"]?.GetValue<string>());
        Assert.AreEqual(0, harness.StateService.GetOnDeckList().Count);
    }

    [TestMethod]
    public async Task ShareAdviceEndpointReportsOnDeckEntryFloorAsync()
    {
        using var harness = TestHarness.Create(sharedWinnerSlotCount: 1, workSetReserveMultiplier: 1);

        IActionResult shareResponse = await harness.MiningController.SubmitShare(CreateSampleShareDto());
        JsonObject sharePayload = ParseObjectResult(shareResponse, StatusCodes.Status200OK);
        double acceptedDifficulty = sharePayload["difficulty"]!.GetValue<double>();

        IActionResult adviceResponse = harness.MiningController.GetShareAdvice();
        JsonObject advice = ParseObjectResult(adviceResponse, StatusCodes.Status200OK);

        Assert.AreEqual(1, advice["SharedWinnerSlotCount"]!.GetValue<int>());
        Assert.AreEqual(1, advice["OnDeckCount"]!.GetValue<int>());
        Assert.AreEqual(0, advice["OpenOnDeckSlots"]!.GetValue<int>());
        Assert.IsTrue(advice["OnDeckIsFull"]!.GetValue<bool>());
        Assert.IsTrue(advice["RequiresStrictlyGreaterThanFloor"]!.GetValue<bool>());
        Assert.AreEqual(acceptedDifficulty, advice["CurrentOnDeckFloorDifficulty"]!.GetValue<double>(), acceptedDifficulty * 0.0000001);
        Assert.IsTrue(advice["MinimumDifficultyToEnterOnDeck"]!.GetValue<double>() > acceptedDifficulty);
    }

    [TestMethod]
    public void DatumSessionTelemetryTracksLifecycleAndFiltersActiveSessions()
    {
        using var harness = TestHarness.Create();
        DateTime startedUtc = DateTime.UtcNow;

        harness.StateService.RecordDatumSessionOpened("session-1", "127.0.0.1:5001", startedUtc);
        harness.StateService.RecordDatumSessionProtocol("session-1", "datum", startedUtc.AddMilliseconds(1));
        harness.StateService.RecordDatumSessionHello("session-1", "signing-key-1", "encrypt-key-1", startedUtc.AddMilliseconds(2));
        harness.StateService.RecordDatumSessionPayoutLock("session-1", AlternateAddress, startedUtc.AddMilliseconds(3));
        harness.StateService.RecordDatumSessionCoinbaserFetch("session-1", startedUtc.AddMilliseconds(4));
        harness.StateService.RecordDatumSessionRefreshRequest("session-1", startedUtc.AddMilliseconds(5));
        harness.StateService.RecordDatumSessionShareOutcome("session-1", accepted: true, affectedOnDeck: true, startedUtc.AddMilliseconds(6));
        harness.StateService.RecordDatumSessionShareOutcome("session-1", accepted: false, affectedOnDeck: false, startedUtc.AddMilliseconds(7));
        harness.StateService.CompleteDatumSession(
            "session-1",
            "client-disconnected-no-data",
            "Client closed DATUM session before sending a full header.",
            serverInitiated: false,
            serverCloseEventType: null,
            timestampUtc: startedUtc.AddSeconds(30));

        harness.StateService.RecordDatumSessionOpened("session-2", "127.0.0.1:5002", startedUtc.AddSeconds(1));
        harness.StateService.RecordDatumSessionProtocol("session-2", "datum", startedUtc.AddSeconds(1).AddMilliseconds(1));

        BootDatumSessionSeriesDto allSessions = harness.StateService.GetDatumSessions(windowKey: "1h", limit: 10, protocol: "datum");
        Assert.AreEqual(2, allSessions.TotalEvents);

        BootDatumSessionTelemetry closedSession = allSessions.Events.Single(item => item.SessionId == "session-1");
        Assert.IsTrue(closedSession.HandshakeCompleted);
        Assert.AreEqual(1, closedSession.HelloCount);
        Assert.AreEqual(1, closedSession.CoinbaserFetchCount);
        Assert.AreEqual(1, closedSession.RefreshRequestCount);
        Assert.AreEqual(2, closedSession.ShareResponseCount);
        Assert.AreEqual(1, closedSession.AcceptedShareCount);
        Assert.AreEqual(1, closedSession.RejectedShareCount);
        Assert.AreEqual(1, closedSession.AffectedOnDeckCount);
        Assert.AreEqual(BitcoinScript.NormalizeAddress(AlternateAddress), closedSession.LockedPayoutAddress);
        Assert.AreEqual("client-disconnected-no-data", closedSession.CloseDisposition);
        Assert.IsFalse(closedSession.ServerInitiatedClose);
        Assert.IsTrue(closedSession.DurationMs >= 30000 - 1);
        Assert.IsTrue(closedSession.IdleBeforeCloseMs >= 29990);

        BootDatumSessionSeriesDto activeSessions = harness.StateService.GetDatumSessions(windowKey: "1h", limit: 10, active: true);
        Assert.AreEqual(1, activeSessions.TotalEvents);
        Assert.AreEqual("session-2", activeSessions.Events[0].SessionId);
        Assert.IsNull(activeSessions.Events[0].ClosedUtc);
    }

    private static ShareSubmissionDto CreateSampleShareDto()
    {
        return new ShareSubmissionDto
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PrevBlockHash = SamplePrevBlockHash
        };
    }

    private static RecordedShareSubmission CreateSampleRecordedShare(string source, string? payoutSnapshotId = null)
    {
        return new RecordedShareSubmission
        {
            MinerAddress = AlternateAddress,
            Username = string.Empty,
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PayoutSnapshotId = payoutSnapshotId,
            PrevBlockHash = SamplePrevBlockHash,
            Source = source
        };
    }

    private static (BootShareProof Low, BootShareProof High) CreateNonceRankedProofPair(string payoutSnapshotId)
    {
        var proofs = new List<BootShareProof>();
        byte[] header = Convert.FromHexString(SampleHeaderHex);
        for (uint nonce = 0; nonce < 256; nonce++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), nonce);
            proofs.Add(CreateValidatedProof(
                Convert.ToHexString(header).ToLowerInvariant(),
                SamplePrevBlockHash,
                payoutSnapshotId));
        }

        BootShareProof low = proofs.OrderBy(proof => proof.Difficulty).ThenBy(proof => proof.ShareId, StringComparer.Ordinal).First();
        BootShareProof high = proofs.OrderByDescending(proof => proof.Difficulty).ThenBy(proof => proof.ShareId, StringComparer.Ordinal).First();
        Assert.AreNotEqual(low.ShareId, high.ShareId);
        Assert.IsTrue(high.Difficulty > low.Difficulty);
        return (low, high);
    }

    private static PeerShareAnnouncement CreateSamplePeerAnnouncement(
        PoolConfig config,
        int? protocolVersion = null,
        string? networkId = null)
    {
        return new PeerShareAnnouncement
        {
            SenderEndpoint = "https://peer.example",
            ProtocolVersion = protocolVersion ?? config.BootProtocolVersion,
            NetworkId = networkId ?? config.BootNetworkId,
            Share = new BootShareProof
            {
                ShareId = string.Empty,
                MinerAddress = AlternateAddress,
                Username = string.Empty,
                HeaderHex = SampleHeaderHex,
                CoinbaseHex = SampleCoinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = SamplePrevBlockHash,
                Source = "peer"
            }
        };
    }

    private static BootShareProof CreateFakeProof(
        string shareId,
        double difficulty,
        string minerAddress = SampleSlotZeroAddress,
        string? payoutSnapshotId = null,
        DateTime? timestampUtc = null)
    {
        return new BootShareProof
        {
            ShareId = shareId,
            MinerAddress = BitcoinScript.NormalizeAddress(minerAddress),
            Username = minerAddress,
            ScriptPubKeyHex = BitcoinScript.AddressToScriptPubKeyHex(minerAddress),
            HeaderHex = SampleHeaderHex,
            CoinbaseHex = SampleCoinbaseHex,
            MerklePath = SampleMerklePath.ToList(),
            PayoutSnapshotId = payoutSnapshotId,
            PrevBlockHash = SamplePrevBlockHash,
            Difficulty = difficulty,
            DiffString = ClientHandler.FormatDifficulty(difficulty),
            Source = "test-seed",
            Timestamp = timestampUtc ?? DateTime.UtcNow.AddSeconds(-difficulty)
        };
    }

    private static BootShareProof CreateValidatedProof(
        string headerHex,
        string prevBlockHash,
        string? payoutSnapshotId = null)
    {
        return CreateValidatedProofForSnapshot(
            headerHex,
            SampleCoinbaseHex,
            prevBlockHash,
            payoutSnapshotId,
            SampleExpectedWinners);
    }

    private static BootShareProof CreateValidatedProofForSnapshot(
        string headerHex,
        string coinbaseHex,
        string prevBlockHash,
        string? payoutSnapshotId,
        IReadOnlyList<PayoutInfo> expectedWinners)
    {
        var verifier = new BootShareVerifier();
        BootShareValidationResult validation = verifier.ValidateShare(
            new RecordedShareSubmission
            {
                MinerAddress = SampleSlotZeroAddress,
                Username = SampleSlotZeroAddress,
                HeaderHex = headerHex,
                CoinbaseHex = coinbaseHex,
                MerklePath = SampleMerklePath.ToList(),
                PrevBlockHash = prevBlockHash,
                Source = "test"
            },
            expectedWinners,
            prevBlockHash);
        Assert.IsTrue(validation.IsValid, validation.RejectionReason);
        return new BootShareProof
        {
            ShareId = validation.ShareId,
            MinerAddress = validation.MinerAddress,
            Username = validation.Username,
            ScriptPubKeyHex = validation.ScriptPubKeyHex,
            HeaderHex = validation.HeaderHex,
            CoinbaseHex = validation.CoinbaseHex,
            MerklePath = validation.MerklePath.ToList(),
            PayoutSnapshotId = payoutSnapshotId,
            PrevBlockHash = validation.PrevBlockHash,
            Difficulty = validation.Difficulty,
            DiffString = ClientHandler.FormatDifficulty(validation.Difficulty),
            Source = "test-seed",
            Timestamp = DateTime.UtcNow
        };
    }

    private static BootPayoutSnapshotContext CreateSnapshotContext(
        string snapshotId,
        IReadOnlyList<PayoutInfo> winners,
        string? lockedByBlockHash = SamplePrevBlockHash)
    {
        return new BootPayoutSnapshotContext
        {
            SnapshotId = snapshotId,
            CurrentRoundNumber = 1,
            LockedByBlockHash = lockedByBlockHash,
            LockedByBlockHeight = 945000,
            CreatedAtUtc = DateTime.UtcNow,
            WinnersList = winners.Select(ClonePayout).ToList(),
            FeeFreeWinnersList = winners.Select(ClonePayout).ToList()
        };
    }

    private static JsonObject ParseObjectResult(IActionResult actionResult, int expectedStatusCode)
    {
        var objectResult = actionResult as ObjectResult;
        Assert.IsNotNull(objectResult, $"Expected ObjectResult, got {actionResult.GetType().Name}.");
        Assert.AreEqual(expectedStatusCode, objectResult.StatusCode ?? expectedStatusCode);
        return JsonNode.Parse(JsonSerializer.Serialize(objectResult.Value))!.AsObject();
    }

    private static IReadOnlyList<PayoutInfo> BuildExpectedWinners(string coinbaseHex)
    {
        var outputs = BitcoinTransactionParser.ParseOutputs(Convert.FromHexString(coinbaseHex));
        return outputs
            .Skip(1)
            .Select(output => new PayoutInfo
            {
                Value = output.Value,
                Address = BitcoinScript.ScriptToAddress(output.ScriptPubKey)
            })
            .Where(output => !string.Equals(output.Address, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static PayoutInfo ClonePayout(PayoutInfo payout)
    {
        return new PayoutInfo
        {
            Value = payout.Value,
            Address = payout.Address,
            Username = payout.Username,
            Difficulty = payout.Difficulty,
            DiffString = payout.DiffString
        };
    }

    private static string RewriteSlotZeroAddress(string coinbaseHex, string replacementAddress)
    {
        byte[] transaction = Convert.FromHexString(coinbaseHex);
        byte[] replacementScript = BitcoinScript.AddressToScriptPubKey(replacementAddress);
        int offset = 0;

        offset += 4; // version
        bool hasWitness = transaction[offset] == 0x00 && transaction[offset + 1] != 0x00;
        if (hasWitness)
        {
            offset += 2;
        }

        ulong inputCount = ReadVarInt(transaction, ref offset);
        for (ulong index = 0; index < inputCount; index++)
        {
            offset += 32; // prev txid
            offset += 4;  // prev index
            ulong scriptLength = ReadVarInt(transaction, ref offset);
            offset += checked((int)scriptLength);
            offset += 4; // sequence
        }

        _ = ReadVarInt(transaction, ref offset); // output count
        offset += 8; // first output value
        ulong firstScriptLength = ReadVarInt(transaction, ref offset);
        Assert.AreEqual((ulong)replacementScript.Length, firstScriptLength, "Test helper expects equal-length slot-0 scripts.");
        Array.Copy(replacementScript, 0, transaction, offset, replacementScript.Length);

        return Convert.ToHexString(transaction).ToLowerInvariant();
    }

    private static string BuildCoinbaseWithWinnerPrefix(string coinbaseHex, int positiveWinnerCount)
    {
        (byte[] prefix, List<SerializedTxOutput> outputs, byte[] suffix) = ReadSerializedOutputs(coinbaseHex);
        var selected = new List<SerializedTxOutput> { outputs[0] };
        int copiedPositiveWinners = 0;

        foreach (SerializedTxOutput output in outputs.Skip(1))
        {
            if (output.Value > 0)
            {
                if (copiedPositiveWinners < positiveWinnerCount)
                {
                    selected.Add(output);
                    copiedPositiveWinners++;
                }

                continue;
            }

            selected.Add(output);
        }

        Assert.AreEqual(positiveWinnerCount, copiedPositiveWinners, "Test helper could not find enough positive winner outputs.");
        return RebuildCoinbase(prefix, selected, suffix);
    }

    private static string BuildCoinbaseWithOnlySlotZero(string coinbaseHex)
    {
        (byte[] prefix, List<SerializedTxOutput> outputs, byte[] suffix) = ReadSerializedOutputs(coinbaseHex);
        return RebuildCoinbase(prefix, [outputs[0]], suffix);
    }

    private static string BuildCoinbaseWithMutatedFirstWinnerScript(string coinbaseHex)
    {
        (byte[] prefix, List<SerializedTxOutput> outputs, byte[] suffix) = ReadSerializedOutputs(coinbaseHex);
        byte[] replacementScript = BitcoinScript.AddressToScriptPubKey(AlternateAddress);
        bool mutated = false;
        var rewritten = outputs.Select((output, index) =>
        {
            if (!mutated && index > 0 && output.Value > 0)
            {
                mutated = true;
                byte[] bytes = output.Bytes.ToArray();
                int offset = 8;
                ulong scriptLength = ReadVarInt(bytes, ref offset);
                Assert.AreEqual((ulong)replacementScript.Length, scriptLength, "Test helper expects equal-length output scripts.");
                Array.Copy(replacementScript, 0, bytes, offset, replacementScript.Length);
                return new SerializedTxOutput(output.Value, bytes);
            }

            return output;
        }).ToList();

        Assert.IsTrue(mutated, "Test helper could not find a winner output to mutate.");
        return RebuildCoinbase(prefix, rewritten, suffix);
    }

    private static (byte[] Prefix, List<SerializedTxOutput> Outputs, byte[] Suffix) ReadSerializedOutputs(string coinbaseHex)
    {
        byte[] transaction = Convert.FromHexString(coinbaseHex);
        int offset = 0;

        offset += 4; // version
        bool hasWitness = transaction[offset] == 0x00 && transaction[offset + 1] != 0x00;
        if (hasWitness)
        {
            offset += 2;
        }

        ulong inputCount = ReadVarInt(transaction, ref offset);
        for (ulong index = 0; index < inputCount; index++)
        {
            offset += 32; // prev txid
            offset += 4; // prev index
            ulong scriptLength = ReadVarInt(transaction, ref offset);
            offset += checked((int)scriptLength);
            offset += 4; // sequence
        }

        int outputCountOffset = offset;
        ulong outputCount = ReadVarInt(transaction, ref offset);
        var outputs = new List<SerializedTxOutput>();
        for (ulong index = 0; index < outputCount; index++)
        {
            int outputStart = offset;
            ulong value = ReadUInt64(transaction, ref offset);
            ulong scriptLength = ReadVarInt(transaction, ref offset);
            offset += checked((int)scriptLength);
            outputs.Add(new SerializedTxOutput(value, transaction[outputStart..offset].ToArray()));
        }

        return (transaction[..outputCountOffset].ToArray(), outputs, transaction[offset..].ToArray());
    }

    private static string RebuildCoinbase(byte[] prefix, IReadOnlyList<SerializedTxOutput> outputs, byte[] suffix)
    {
        using var stream = new MemoryStream();
        stream.Write(prefix);
        stream.Write(EncodeVarInt((ulong)outputs.Count));
        foreach (SerializedTxOutput output in outputs)
        {
            stream.Write(output.Bytes);
        }

        stream.Write(suffix);
        return Convert.ToHexString(stream.ToArray()).ToLowerInvariant();
    }

    private static byte[] EncodeVarInt(ulong value)
    {
        if (value < 0xfd)
        {
            return [(byte)value];
        }

        if (value <= ushort.MaxValue)
        {
            byte[] encoded = new byte[3];
            encoded[0] = 0xfd;
            BinaryPrimitives.WriteUInt16LittleEndian(encoded.AsSpan(1), (ushort)value);
            return encoded;
        }

        if (value <= uint.MaxValue)
        {
            byte[] encoded = new byte[5];
            encoded[0] = 0xfe;
            BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(1), (uint)value);
            return encoded;
        }

        byte[] extended = new byte[9];
        extended[0] = 0xff;
        BinaryPrimitives.WriteUInt64LittleEndian(extended.AsSpan(1), value);
        return extended;
    }

    private static string RewriteHeaderMerkleRoot(string headerHex, string coinbaseHex)
    {
        byte[] headerBytes = Convert.FromHexString(headerHex);
        byte[] coinbaseHash = DoubleSha256(Convert.FromHexString(coinbaseHex));
        byte[] merkleRoot = ComputeMerkleRoot(coinbaseHash, SampleMerklePath.Select(Convert.FromHexString).ToList());
        Array.Copy(merkleRoot, 0, headerBytes, 36, 32);
        return Convert.ToHexString(headerBytes).ToLowerInvariant();
    }

    private static string RewriteHeaderPrevBlockHash(string headerHex, string prevBlockHash)
    {
        byte[] headerBytes = Convert.FromHexString(headerHex);
        byte[] internalHashBytes = Convert.FromHexString(BitcoinHashes.ReverseHexByteOrder(prevBlockHash));
        Array.Copy(internalHashBytes, 0, headerBytes, 4, 32);
        return Convert.ToHexString(headerBytes).ToLowerInvariant();
    }

    private static string RewriteHeaderCompactTarget(string headerHex, uint compactTarget)
    {
        byte[] headerBytes = Convert.FromHexString(headerHex);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(72, 4), compactTarget);
        return Convert.ToHexString(headerBytes).ToLowerInvariant();
    }

    private static (string HeaderHex, string CoinbaseHex) BuildLowDifficultySlotZeroMutation(
        string headerHex,
        string coinbaseHex,
        IReadOnlyList<string> merklePath,
        string replacementAddress)
    {
        string mutatedCoinbaseHex = RewriteSlotZeroAddress(coinbaseHex, replacementAddress);
        byte[] headerBytes = Convert.FromHexString(headerHex);
        byte[] coinbaseHash = DoubleSha256(Convert.FromHexString(mutatedCoinbaseHex));
        byte[] merkleRoot = ComputeMerkleRoot(coinbaseHash, merklePath.Select(Convert.FromHexString).ToList());
        Array.Copy(merkleRoot, 0, headerBytes, 36, 32);

        for (uint nonce = 0; nonce < 1024; nonce++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(76, 4), nonce);
            if (ComputeDifficulty(headerBytes) < 1)
            {
                return (Convert.ToHexString(headerBytes).ToLowerInvariant(), mutatedCoinbaseHex);
            }
        }

        Assert.Fail("Failed to produce a low-difficulty mutated header.");
        return (string.Empty, string.Empty);
    }

    private sealed record SerializedTxOutput(ulong Value, byte[] Bytes);

    private static byte[] ComputeMerkleRoot(byte[] coinbaseHash, IReadOnlyList<byte[]> merkleBranches)
    {
        byte[] current = coinbaseHash;
        foreach (byte[] branch in merkleBranches)
        {
            current = DoubleSha256(current.Concat(branch).ToArray());
        }

        return current;
    }

    private static byte[] DoubleSha256(byte[] bytes)
    {
        return SHA256.HashData(SHA256.HashData(bytes));
    }

    private static double ComputeDifficulty(byte[] headerBytes)
    {
        byte[] headerHash = DoubleSha256(headerBytes);
        BigInteger difficultyOneTarget = DecodeCompactTarget(0x1d00ffff);
        BigInteger hashValue = ToPositiveBigInteger(headerHash);
        return hashValue.IsZero ? double.MaxValue : (double)difficultyOneTarget / (double)hashValue;
    }

    private static BigInteger DecodeCompactTarget(uint compact)
    {
        int exponent = (int)(compact >> 24);
        uint mantissa = compact & 0x007fffff;
        BigInteger target = mantissa;
        int shift = 8 * (exponent - 3);
        if (shift >= 0)
        {
            target <<= shift;
        }
        else
        {
            target >>= -shift;
        }

        return target;
    }

    private static BigInteger ToPositiveBigInteger(byte[] littleEndianBytes)
    {
        byte[] extended = new byte[littleEndianBytes.Length + 1];
        Array.Copy(littleEndianBytes, extended, littleEndianBytes.Length);
        return new BigInteger(extended);
    }

    private static ulong ReadVarInt(byte[] buffer, ref int offset)
    {
        byte prefix = buffer[offset++];
        return prefix switch
        {
            < 0xFD => prefix,
            0xFD => ReadUInt16(buffer, ref offset),
            0xFE => ReadUInt32(buffer, ref offset),
            _ => ReadUInt64(buffer, ref offset)
        };
    }

    private static ushort ReadUInt16(byte[] buffer, ref int offset)
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
        offset += 2;
        return value;
    }

    private static uint ReadUInt32(byte[] buffer, ref int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static ulong ReadUInt64(byte[] buffer, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
        offset += 8;
        return value;
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly string? _previousStatePath;
        private readonly string? _previousHistoryPath;
        private readonly string _tempDirectory;

        public PoolConfig Config { get; }
        public BootProtocolStateService StateService { get; }
        public MiningApiController MiningController { get; }
        public BootPeerController PeerController { get; }
        public string StatePath => Path.Combine(_tempDirectory, "pool_state.json");

        private TestHarness(
            string tempDirectory,
            string? previousStatePath,
            string? previousHistoryPath,
            PoolConfig config,
            BootProtocolStateService stateService)
        {
            _tempDirectory = tempDirectory;
            _previousStatePath = previousStatePath;
            _previousHistoryPath = previousHistoryPath;
            Config = config;
            StateService = stateService;
            MiningController = new MiningApiController(config, stateService, NullLogger<MiningApiController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            PeerController = new BootPeerController(
                config,
                stateService,
                CreatePeerSessionManager(config, stateService),
                NullLogger<BootPeerController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public static TestHarness Create(
            string? currentTipBlockHash = null,
            long? currentTipBlockHeight = null,
            uint? currentTipCompactTarget = null,
            int currentRoundNumber = 1,
            string currentStateId = "seed-current",
            int? sharedWinnerSlotCount = null,
            int? workSetReserveMultiplier = null,
            bool supportFeeEnabled = false,
            IReadOnlyList<PayoutInfo>? winnersList = null,
            IReadOnlyList<BootShareProof>? onDeckProofs = null,
            IReadOnlyList<BootPayoutSnapshotContext>? snapshotContexts = null,
            string? activeSnapshotId = null,
            IReadOnlyList<string>? activeSnapshotProofIds = null,
            int? protocolVersion = null,
            int? seedMetadataProtocolVersion = null,
            long v22ActivationBlockHeight = 0,
            bool seedUnknownTipHeight = false,
            bool seedUnknownTrustedTip = false,
            bool enablePeerTipStaleProtection = false,
            int peerTipGraceSeconds = 3)
        {
            string? previousStatePath = Environment.GetEnvironmentVariable("BOOT_PORTAL_STATE_PATH");
            string? previousHistoryPath = Environment.GetEnvironmentVariable("BOOT_PORTAL_HISTORY_PATH");
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"boot-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            string statePath = Path.Combine(tempDirectory, "pool_state.json");
            string historyPath = Path.Combine(tempDirectory, "pool_state.history.json");
            Environment.SetEnvironmentVariable("BOOT_PORTAL_STATE_PATH", statePath);
            Environment.SetEnvironmentVariable("BOOT_PORTAL_HISTORY_PATH", historyPath);

            var config = new PoolConfig
            {
                BootNetworkId = "testnet",
                BootProtocolVersion = protocolVersion ?? BootProtocolVersions.ConsensusVersion,
                V22ActivationBlockHeight = v22ActivationBlockHeight,
                WinnersListSize = sharedWinnerSlotCount ?? Math.Max(8, SampleExpectedWinners.Count),
                PoolPayoutScript = SampleSlotZeroAddress,
                GridLabsSupportFeeEnabled = supportFeeEnabled,
                WorkSetReserveMultiplier = workSetReserveMultiplier ?? 3,
                EnablePeerTipStaleProtection = enablePeerTipStaleProtection,
                PeerTipGraceSeconds = peerTipGraceSeconds,
                // Fixed historical headers in these tests must not expire as wall-clock time advances.
                PeerTipMaxHeaderAgeSeconds = 31_536_000
            };

            var seedState = new PoolState
            {
                Metadata = new BootProtocolMetadata
                {
                    NetworkId = config.BootNetworkId,
                    ProtocolVersion = seedMetadataProtocolVersion ?? config.BootProtocolVersion
                },
                CurrentStateId = currentStateId,
                CandidateStateId = "seed-candidate",
                CurrentRoundNumber = currentRoundNumber,
                CurrentTipBlockHash = currentTipBlockHash ?? SamplePrevBlockHash,
                CurrentTipBlockHeight = seedUnknownTipHeight ? null : currentTipBlockHeight ?? 945000,
                TrustedLocalTipBlockHash = seedUnknownTipHeight || seedUnknownTrustedTip
                    ? null
                    : currentTipBlockHash ?? SamplePrevBlockHash,
                TrustedLocalTipBlockHeight = seedUnknownTipHeight || seedUnknownTrustedTip
                    ? null
                    : currentTipBlockHeight ?? 945000,
                CurrentTipCompactTarget = currentTipCompactTarget,
                AcceptedParentBlockHashes = [currentTipBlockHash ?? SamplePrevBlockHash],
                ActiveSnapshotId = activeSnapshotId ?? currentStateId,
                ActiveSnapshotProofIds = activeSnapshotProofIds?.ToList() ?? [],
                SnapshotContexts = snapshotContexts?.Select(CloneSnapshotContext).ToList() ?? [],
                WinnersList = (winnersList ?? SampleExpectedWinners).Select(ClonePayout).ToList(),
                OnDeckList = [],
                OnDeckProofs = onDeckProofs?.Select(CloneProof).ToList() ?? []
            };
            File.WriteAllText(statePath, JsonSerializer.Serialize(seedState));

            var stateService = new BootProtocolStateService(
                config,
                new BootShareVerifier(),
                new NoOpHubContext(),
                NullLogger<BootProtocolStateService>.Instance);

            return new TestHarness(tempDirectory, previousStatePath, previousHistoryPath, config, stateService);
        }

        private static BootPeerSessionManager CreatePeerSessionManager(PoolConfig config, BootProtocolStateService stateService)
        {
            var identity = new BootPeerIdentity(
                Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }),
                Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport }));

            return new BootPeerSessionManager(
                config,
                stateService,
                identity,
                NullLogger<BootPeerSessionManager>.Instance);
        }

        public void Dispose()
        {
            Thread.Sleep(1200);
            Environment.SetEnvironmentVariable("BOOT_PORTAL_STATE_PATH", _previousStatePath);
            Environment.SetEnvironmentVariable("BOOT_PORTAL_HISTORY_PATH", _previousHistoryPath);
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        private static PayoutInfo ClonePayout(PayoutInfo payout)
        {
            return new PayoutInfo
            {
                Value = payout.Value,
                Address = payout.Address,
                Username = payout.Username,
                Difficulty = payout.Difficulty,
                DiffString = payout.DiffString
            };
        }

        private static BootShareProof CloneProof(BootShareProof proof)
        {
            return new BootShareProof
            {
                ShareId = proof.ShareId,
                MinerAddress = proof.MinerAddress,
                Username = proof.Username,
                ScriptPubKeyHex = proof.ScriptPubKeyHex,
                HeaderHex = proof.HeaderHex,
                CoinbaseHex = proof.CoinbaseHex,
                MerklePath = proof.MerklePath.ToList(),
                PayoutSnapshotId = proof.PayoutSnapshotId,
                PrevBlockHash = proof.PrevBlockHash,
                Difficulty = proof.Difficulty,
                DiffString = proof.DiffString,
                Source = proof.Source,
                Timestamp = proof.Timestamp
            };
        }

        private static BootPayoutSnapshotContext CloneSnapshotContext(BootPayoutSnapshotContext context)
        {
            return new BootPayoutSnapshotContext
            {
                SnapshotId = context.SnapshotId,
                FamilyId = context.FamilyId,
                PreviousSnapshotId = context.PreviousSnapshotId,
                CurrentRoundNumber = context.CurrentRoundNumber,
                LockedByBlockHash = context.LockedByBlockHash,
                LockedByBlockHeight = context.LockedByBlockHeight,
                CreatedAtUtc = context.CreatedAtUtc,
                SupportFeeEnabled = context.SupportFeeEnabled,
                PayoutVariant = context.PayoutVariant,
                ProofIds = context.ProofIds.ToList(),
                WinnersList = context.WinnersList.Select(ClonePayout).ToList(),
                FeeFreeWinnersList = context.FeeFreeWinnersList.Select(ClonePayout).ToList()
            };
        }
    }

    private sealed class NoOpHubContext : IHubContext<PoolStatsHub>
    {
        public IHubClients Clients { get; } = new NoOpHubClients();
        public IGroupManager Groups { get; } = new NoOpGroupManager();
    }

    private sealed class NoOpHubClients : IHubClients, IHubClients<IClientProxy>
    {
        private static readonly IClientProxy Proxy = new NoOpClientProxy();

        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName) => Proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private class NoOpClientProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
