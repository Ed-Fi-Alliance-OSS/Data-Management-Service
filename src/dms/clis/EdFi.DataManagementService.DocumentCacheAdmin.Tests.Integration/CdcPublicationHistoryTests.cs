// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture(false, Category = "PostgresqlIntegration")]
[TestFixture(true, Category = "MssqlIntegration")]
[Category("DatabaseIntegration")]
[Category("CdcPublicationHistory")]
[NonParallelizable]
public sealed class Given_CdcPublicationHistory_packaged_administration(bool mssql)
{
    private CdcPublicationHistoryFixture _fixture = null!;
    private readonly ConcurrentQueue<object> _evidence = new();
    private static readonly string[] _commands =
    [
        "activate-offline",
        "deactivate-offline",
        "recover-cache-ahead",
    ];

    [SetUp]
    public async Task SetUp()
    {
        _evidence.Clear();
        _fixture = new(mssql);
        await _fixture.InitializeAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            string path = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"cdc-history-{TestContext.CurrentContext.Test.ID}-{Guid.NewGuid():N}.json"
            );
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(_evidence, new JsonSerializerOptions { WriteIndented = true })
            );
            TestContext.AddTestAttachment(path, "Sanitized packaged command and concurrency observations");
        }
        finally
        {
            if (_fixture is not null)
            {
                await _fixture.DisposeAsync();
            }
        }
    }

    [Test]
    public async Task It_allows_all_three_commands_from_managed_non_CDC_creation()
    {
        foreach (string command in _commands)
        {
            await _fixture.PrepareAsync(command);
            string before = await _fixture.SnapshotAsync();
            JsonObject result = ReadResult(
                await _fixture.Harness.RunAsync(_fixture.Arguments(command)),
                command,
                0
            );
            result["mutated"]!.GetValue<bool>().Should().BeTrue();
            (await _fixture.SnapshotAsync()).Should().NotBe(before);
            (
                await _fixture.QueryAsync(
                    "SELECT \"ProjectionLifecycleState\",\"CacheAheadRecoveryRequired\" FROM dms.\"DocumentCacheState\""
                )
            )
                .Should()
                .Be(command == "deactivate-offline" ? "[[\"Disabled\",false]]" : "[[\"Tracking\",false]]");
            await using var session = await _fixture.AcquireAsync();
            (
                await session.ReadSourcePublicationHistoryAsync(
                    _fixture.Receipt.Target,
                    _fixture.Fingerprint,
                    CancellationToken.None
                )
            )
                .Transitions.Should()
                .ContainSingle()
                .Which.Status.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
        }
    }

    [TestCase("possible")]
    [TestCase("active")]
    [TestCase("historical")]
    [TestCase("unknown")]
    [TestCase("absent-root")]
    [TestCase("empty-root")]
    [TestCase("unreadable")]
    [TestCase("wrong-target")]
    [TestCase("wrong-deployment")]
    [TestCase("wrong-source")]
    [TestCase("missing-receipt")]
    [TestCase("missing-journal")]
    [TestCase("missing-history")]
    [TestCase("corrupt-history")]
    [TestCase("corrupt-journal")]
    public async Task It_rejects_untrusted_or_exposed_history_without_any_mutation(string scenario)
    {
        await ChangeEvidenceAsync(scenario);
        foreach (string command in _commands)
        {
            await _fixture.PrepareAsync(command);
            await AssertRejectedAsync(
                command,
                scenario is "possible" or "active" or "historical" ? scenario : "unknown"
            );
        }
    }

    [Test]
    public async Task It_preserves_historical_rejection_after_retiring_possible_exposure_with_a_surviving_source()
    {
        // Interrupted reservation has no external artifacts. Retire its journal after verifying that
        // no binding exists; full live connector/provider cleanup is qualified in T32.
        await using (var session = await _fixture.AcquireAsync())
        {
            await _fixture.ReserveAsync(session);
            Guid operation = Guid.NewGuid();
            await session.RecordIntentAsync(
                _fixture.Receipt.Target,
                _fixture.Receipt.WorkflowId,
                operation,
                CdcWorkflowEffect.Retire,
                [],
                CancellationToken.None
            );
            await session.ReconcileCompletionAsync(
                _fixture.Receipt.Target,
                _fixture.Receipt.WorkflowId,
                operation,
                (_, _) =>
                {
                    Directory
                        .GetFiles(_fixture.Root, "binding.json", SearchOption.AllDirectories)
                        .Should()
                        .BeEmpty();
                    return Task.FromResult<CdcTransportResult<CdcWorkflowCompletion>>(
                        new CdcTransportResult<CdcWorkflowCompletion>.Observed(
                            new CdcWorkflowCompletion.Reconciled()
                        )
                    );
                },
                CancellationToken.None
            );
            (
                await session.ReadSourcePublicationHistoryAsync(
                    _fixture.Receipt.Target,
                    _fixture.Fingerprint,
                    CancellationToken.None
                )
            )
                .Transitions[^1]
                .Status.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.Historical);
        }
        foreach (string command in _commands)
        {
            await _fixture.PrepareAsync(command);
            await AssertRejectedAsync(command, "historical");
        }
        await ChangeEvidenceAsync("empty-root");
        foreach (string command in _commands)
        {
            await _fixture.PrepareAsync(command);
            await AssertRejectedAsync(command);
        }
    }

    [TestCase("activate-offline")]
    [TestCase("deactivate-offline")]
    [TestCase("recover-cache-ahead")]
    public async Task It_rejects_after_reservation_wins_without_entering_the_provider_mutex_early(
        string command
    )
    {
        await _fixture.PrepareAsync(command);
        string before = await _fixture.SnapshotAsync();
        await using DbConnection mutex = await _fixture.HoldMutexAsync();
        DocumentCacheAdminCliRunningProcess process = null!;
        try
        {
            await using (var session = await _fixture.AcquireAsync())
            {
                process = _fixture.Harness.Start(_fixture.Arguments(command));
                await WaitAsync(() =>
                    Task.FromResult(_fixture.Harness.ConfigurationService.DataStoresRequestCount > 0)
                );
                (await process.TryWaitForExitAsync(TimeSpan.FromSeconds(1))).Should().BeNull();
                (await _fixture.MutexWaitersAsync())
                    .Should()
                    .Be("[[0]]", "the controller lock must be acquired before entering the provider mutex");
                await _fixture.ReserveAsync(session);
                _evidence.Enqueue(new { phase = "reservation-durable", at = DateTimeOffset.UtcNow });
            }
            await WaitAsync(async () => await _fixture.MutexWaitersAsync() == "[[1]]");
            await mutex.CloseAsync();
            AssertRejected(
                ReadResult(
                    await process.WaitForExitAsync(TimeSpan.FromSeconds(60)),
                    command,
                    DocumentCacheAdminExitCodes.RejectedNoMutation
                )
            );
            (await _fixture.SnapshotAsync()).Should().Be(before);
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync();
            }
        }
    }

    [TestCase("activate-offline")]
    [TestCase("deactivate-offline")]
    [TestCase("recover-cache-ahead")]
    public async Task It_holds_the_controller_lock_through_E18_mutation_when_administration_wins(
        string command
    )
    {
        await _fixture.PrepareAsync(command);
        await using DbConnection mutex = await _fixture.HoldMutexAsync();
        await using DbConnection cacheRows = await _fixture.HoldCacheRowsAsync();
        await using var process = _fixture.Harness.Start(_fixture.Arguments(command));
        await WaitAsync(async () => await _fixture.MutexWaitersAsync() == "[[1]]");
        _evidence.Enqueue(new { phase = "E18-waiting-on-provider-mutex", at = DateTimeOffset.UtcNow });
        var blocked = async () =>
        {
            await using var session = await _fixture.Store.AcquireAsync(
                TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None
            );
        };
        (await blocked.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.LockTimeout);
        await mutex.CloseAsync();
        await WaitAsync(async () => await _fixture.CacheDeleteWaitersAsync() == "[[1]]");
        _evidence.Enqueue(
            new { phase = "history-accepted-cache-delete-blocked", at = DateTimeOffset.UtcNow }
        );
        (await blocked.Should().ThrowAsync<CdcWorkflowStateException>())
            .Which.Failure.Should()
            .Be(CdcWorkflowStateFailure.LockTimeout);
        Task reservation = ReserveWhenAvailableAsync();
        reservation.IsCompleted.Should().BeFalse();
        await cacheRows.CloseAsync();
        JsonObject result = ReadResult(await process.WaitForExitAsync(TimeSpan.FromSeconds(60)), command, 0);
        result["mutated"]!.GetValue<bool>().Should().BeTrue();
        await reservation;
        await _fixture.PrepareAsync(command);
        await AssertRejectedAsync(command, "possible");

        async Task ReserveWhenAvailableAsync()
        {
            await using var session = await _fixture.AcquireAsync();
            // Acquiring this lock permits reservation only after the E18 command returned. Check the
            // actual database outcome at this boundary, rather than inferring order from process exit.
            string lifecycle = await _fixture.QueryAsync(
                "SELECT \"ProjectionLifecycleState\",\"CacheAheadRecoveryRequired\" FROM dms.\"DocumentCacheState\""
            );
            lifecycle
                .Should()
                .Be(command == "deactivate-offline" ? "[[\"Disabled\",false]]" : "[[\"Tracking\",false]]");
            _evidence.Enqueue(
                new { phase = "E18-mutation-visible-before-reservation", at = DateTimeOffset.UtcNow }
            );
            await _fixture.ReserveAsync(session);
        }
    }

    private async Task AssertRejectedAsync(string command, string expectedHistory = "unknown")
    {
        if (expectedHistory != "unknown")
        {
            await using var session = await _fixture.AcquireAsync();
            var history = await session.ReadSourcePublicationHistoryAsync(
                _fixture.Receipt.Target,
                _fixture.Fingerprint,
                CancellationToken.None
            );
            history
                .Transitions[^1]
                .Status.Should()
                .Be(Enum.Parse<DocumentCacheDownstreamPublicationStatus>(expectedHistory, true));
            _evidence.Enqueue(new { phase = "valid-retained-history", status = expectedHistory });
        }
        string stateBefore = _fixture.StateFilesSnapshot();
        string before = await _fixture.SnapshotAsync();
        AssertRejected(
            ReadResult(
                await _fixture.Harness.RunAsync(_fixture.Arguments(command)),
                command,
                DocumentCacheAdminExitCodes.RejectedNoMutation
            )
        );
        _fixture
            .StateFilesSnapshot()
            .Should()
            .Be(stateBefore, "rejecting administration must not repair deployment provenance");
        string after = await _fixture.SnapshotAsync();
        after
            .Should()
            .Be(before, "canonical, cache, work, lifecycle, latch and source rows must be unchanged");
        _evidence.Enqueue(
            new
            {
                phase = "rejection-state-comparison",
                command,
                before,
                after,
            }
        );
    }

    private static void AssertRejected(JsonObject result)
    {
        result["classification"]!.GetValue<string>().Should().Be("downstreamHistoryPresentOrUnknown");
        result["mutated"]!.GetValue<bool>().Should().BeFalse();
    }

    private JsonObject ReadResult(DocumentCacheAdminCliProcessResult result, string command, int exitCode)
    {
        // Keep raw process output out of failed assertions/artifacts: it can contain environment data.
        result.StandardOutput.Should().NotContain(_fixture.Harness.SecretFromEnvironment);
        JsonObject json = JsonNode.Parse(result.StandardOutput)!.AsObject();
        _evidence.Enqueue(
            new
            {
                command,
                at = DateTimeOffset.UtcNow,
                result = json,
            }
        );
        result.ExitCode.Should().Be(exitCode, "packaged command {0}: {1}", command, json.ToJsonString());
        return json;
    }

    private async Task ChangeEvidenceAsync(string scenario)
    {
        if (scenario == "unknown")
        {
            _fixture.Harness.RemovePublicationHistoryConfiguration();
            return;
        }
        if (scenario == "possible")
        {
            await using var session = await _fixture.AcquireAsync();
            await _fixture.ReserveAsync(session);
            return;
        }
        if (scenario is "absent-root" or "empty-root")
        {
            Directory.Delete(_fixture.Root, true);
            if (scenario == "empty-root")
            {
                Directory.CreateDirectory(_fixture.Root);
            }
            return;
        }
        string history = _fixture.HistoryPath;
        string journal = _fixture.JournalPath;
        switch (scenario)
        {
            case "missing-history":
                File.Delete(history);
                break;
            case "missing-journal":
                File.Delete(journal);
                break;
            case "unreadable":
                // A directory at the expected file path produces an actual failed file read on all OSes.
                File.Delete(history);
                Directory.CreateDirectory(history);
                break;
            case "corrupt-history":
                await File.WriteAllTextAsync(history, "{");
                break;
            case "corrupt-journal":
                await File.WriteAllTextAsync(journal, "{");
                break;
            case "missing-receipt":
                JsonObject journalNode = JsonNode.Parse(await File.ReadAllTextAsync(journal))!.AsObject();
                JsonArray operations = journalNode["operations"]!.AsArray();
                operations.RemoveAt(0);
                await File.WriteAllTextAsync(journal, journalNode.ToJsonString());
                break;
            case "wrong-target":
                JsonObject historyNode = JsonNode.Parse(await File.ReadAllTextAsync(history))!.AsObject();
                historyNode["creationTarget"]!["dataStoreId"] = "2";
                await File.WriteAllTextAsync(history, historyNode.ToJsonString());
                break;
            case "wrong-deployment":
                _fixture.Harness.ConfigurePublicationHistory(_fixture.Root, "other-deployment");
                break;
            case "wrong-source":
                await _fixture.ExecuteAsync(
                    mssql
                        ? "UPDATE dms.DataStoreIdentity SET SourceIdentity=NEWID()"
                        : "UPDATE dms.\"DataStoreIdentity\" SET \"SourceIdentity\"=gen_random_uuid()"
                );
                await _fixture.RefreshFingerprintAsync();
                break;
            default:
                // Retained-state inputs for rejecting statuses. No fake history service or successful
                // provider reconciliation is installed in the packaged application.
                JsonObject node = JsonNode.Parse(await File.ReadAllTextAsync(history))!.AsObject();
                JsonArray transitions = node["transitions"]!.AsArray();
                transitions.Add(
                    new JsonObject { ["status"] = scenario, ["recordedAt"] = DateTimeOffset.UtcNow }
                );
                await File.WriteAllTextAsync(history, node.ToJsonString());
                break;
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for a live packaged-host concurrency boundary.");
    }
}
