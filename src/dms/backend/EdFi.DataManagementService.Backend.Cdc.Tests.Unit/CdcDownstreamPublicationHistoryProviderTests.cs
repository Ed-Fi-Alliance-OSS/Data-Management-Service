// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
public class Given_CdcDownstreamPublicationHistoryProvider(CdcProvider provider)
{
    private string _root = null!;
    private LocalCdcWorkflowJournalStore _store = null!;
    private CdcDownstreamPublicationHistoryProvider _history = null!;
    private CdcManagedProvisioningResult _created = null!;
    private CdcTargetIdentity Target => CdcWorkflowJournalTestData.Target with { Provider = provider };
    private static DocumentCacheTargetKey Key => DocumentCacheTargetKey.Create(string.Empty, 1);
    private static DocumentCachePhysicalSourceFingerprint Fingerprint =>
        new(CdcWorkflowJournalTestData.Fingerprint);
    private string HistoryPath =>
        Path.Combine(_root, "source-history", "local", new string('a', 64) + ".json");
    private string JournalPath => Path.Combine(_root, "workflows", "local", "instance", "1.json");

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cdc-bridge-{Guid.NewGuid():N}");
        _store = new(_root);
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(Fingerprint.Value);
        _created = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(Target, provisioner);
        _history = new(_store, "local", provider, TimeProvider.System, TimeSpan.FromSeconds(2));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private static DocumentCacheAdministrativeCommandRunnerRequest Request(
        DocumentCacheAdministrativeCommand command = DocumentCacheAdministrativeCommand.OfflineActivation
    ) => new(command, DocumentCacheAdministrativeTargetKey.FromTargetKey(Key));

    private Task<LocalCdcWorkflowJournalStore.Session> AcquireAsync() =>
        _store.AcquireAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), CancellationToken.None);

    private async Task<DocumentCacheAdministrativeCommandResult> EvaluateAsync()
    {
        var observation = await _history.ObserveAsync(Key, Fingerprint);
        var proof = DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
            Key,
            Fingerprint,
            observation
        );
        return new(
            Request().Command,
            Request().TargetKey,
            proof.Classification,
            downstreamPublicationStatus: observation.Status,
            diagnostics: proof.Diagnostics
        );
    }

    private Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync() =>
        _history.ExecuteAsync(Request(), EvaluateAsync, CancellationToken.None);

    [Test]
    public async Task It_accepts_complete_trusted_creation_history_through_the_existing_E18_proof_evaluator()
    {
        var result = await ExecuteAsync();
        result.Classification.Should().Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        result.DownstreamPublicationStatus.Should().Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
    }

    [Test]
    public async Task It_returns_unknown_without_a_current_locked_execution_even_when_history_is_valid()
    {
        (await _history.ObserveAsync(Key, Fingerprint))
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Unknown);
        await ExecuteAsync();
        (await _history.ObserveAsync(Key, Fingerprint))
            .Status.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Unknown);
    }

    [TestCase("missing")]
    [TestCase("different-source")]
    [TestCase("different-target")]
    public async Task It_requires_the_current_target_and_source_to_match_trusted_history(string mismatch)
    {
        await _history.ExecuteAsync(
            Request(),
            async () =>
            {
                var observation = await _history.ObserveAsync(
                    mismatch == "different-target" ? DocumentCacheTargetKey.Create(string.Empty, 2) : Key,
                    mismatch switch
                    {
                        "missing" => null,
                        "different-source" => new("sha256:" + new string('b', 64)),
                        _ => Fingerprint,
                    }
                );
                observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
                return new(
                    Request().Command,
                    Request().TargetKey,
                    DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown
                );
            },
            CancellationToken.None
        );
    }

    [TestCase("missing-history")]
    [TestCase("missing-journal")]
    [TestCase("corrupt-history")]
    [TestCase("corrupt-journal")]
    [TestCase("unsupported-version")]
    [TestCase("wrong-workflow")]
    [TestCase("wrong-target")]
    [TestCase("wrong-provider")]
    [TestCase("wrong-source")]
    [TestCase("reused")]
    [TestCase("unsafe-permissions")]
    public async Task It_maps_untrusted_provenance_to_unknown_without_diagnostic_leaks(string failure)
    {
        JsonObject history = JsonNode.Parse(await File.ReadAllTextAsync(HistoryPath))!.AsObject();
        switch (failure)
        {
            case "missing-history":
                File.Delete(HistoryPath);
                break;
            case "missing-journal":
                File.Delete(JournalPath);
                break;
            case "corrupt-history":
                await File.WriteAllTextAsync(HistoryPath, "secret-sentinel");
                break;
            case "corrupt-journal":
                await File.WriteAllTextAsync(JournalPath, "secret-sentinel");
                break;
            case "unsupported-version":
                history["version"] = 999;
                break;
            case "wrong-workflow":
                history["workflowId"] = Guid.NewGuid();
                break;
            case "wrong-target":
                history["creationTarget"]!["dataStoreId"] = "2";
                break;
            case "wrong-provider":
                history["creationTarget"]!["provider"] =
                    provider == CdcProvider.Postgresql ? "SqlServer" : "Postgresql";
                break;
            case "wrong-source":
                history["physicalSourceFingerprint"] = "sha256:" + new string('b', 64);
                break;
            case "reused":
                history["creationReceipt"]!["outcome"] = "Reused";
                break;
            case "unsafe-permissions":
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        HistoryPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead
                    );
                }
                break;
        }
        if (
            failure
            is "unsupported-version"
                or "wrong-workflow"
                or "wrong-target"
                or "wrong-provider"
                or "wrong-source"
                or "reused"
        )
        {
            await File.WriteAllTextAsync(HistoryPath, history.ToJsonString());
        }
        var result = await ExecuteAsync();
        result.DownstreamPublicationStatus.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
        result.Mutated.Should().BeFalse();
        string json = JsonSerializer.Serialize(result);
        json.Should().NotContain("secret-sentinel").And.NotContain(_root);
    }

    [TestCase(DocumentCacheDownstreamPublicationStatus.Possible)]
    [TestCase(DocumentCacheDownstreamPublicationStatus.Active)]
    [TestCase(DocumentCacheDownstreamPublicationStatus.Historical)]
    public async Task It_preserves_rejecting_exposure_states(DocumentCacheDownstreamPublicationStatus status)
    {
        await using (var session = await AcquireAsync())
        {
            await session.RecordSourceExposureAsync(
                Target,
                _created.WorkflowId,
                Fingerprint.Value,
                CancellationToken.None
            );
        }
        // Materialize later valid source-lifetime transitions. Their persistence/connector ordering is
        // covered by CdcSourcePublicationHistory tests; this table exercises the production bridge mapping.
        JsonObject history = JsonNode.Parse(await File.ReadAllTextAsync(HistoryPath))!.AsObject();
        if (
            status
            is DocumentCacheDownstreamPublicationStatus.Active
                or DocumentCacheDownstreamPublicationStatus.Historical
        )
        {
            history["transitions"]!
                .AsArray()
                .Add(new JsonObject { ["status"] = "Active", ["recordedAt"] = DateTimeOffset.UtcNow });
        }
        if (status == DocumentCacheDownstreamPublicationStatus.Historical)
        {
            history["transitions"]!
                .AsArray()
                .Add(new JsonObject { ["status"] = "Historical", ["recordedAt"] = DateTimeOffset.UtcNow });
        }
        await File.WriteAllTextAsync(HistoryPath, history.ToJsonString());
        var result = await ExecuteAsync();
        result.DownstreamPublicationStatus.Should().Be(status);
        result
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
    }

    [TestCase(DocumentCacheAdministrativeCommand.OfflineActivation)]
    [TestCase(DocumentCacheAdministrativeCommand.OfflineDeactivation)]
    [TestCase(DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery)]
    public async Task It_keeps_reservation_out_from_before_E18_entry_until_command_completion(
        DocumentCacheAdministrativeCommand command
    )
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = A.Fake<IDocumentCacheAdministrativeCommandRunner>();
        A.CallTo(() =>
                inner.ExecuteAsync(
                    A<DocumentCacheAdministrativeCommandRunnerRequest>._,
                    A<IDocumentCacheAdministrativeCommandWorkflow>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async () =>
            {
                // The provider mutex and proof/mutation execute inside this runner call.
                entered.SetResult();
                var result = await EvaluateAsync();
                await finish.Task;
                return result;
            });
        var runner = new CdcHistoryGatedAdministrativeCommandRunner(inner, _history);
        var commandTask = runner.ExecuteAsync(
            Request(command),
            A.Fake<IDocumentCacheAdministrativeCommandWorkflow>()
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<LocalCdcWorkflowJournalStore.Session> reservation = AcquireAsync();
        try
        {
            reservation.IsCompleted.Should().BeFalse();
        }
        finally
        {
            finish.SetResult();
        }
        (await commandTask)
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.Succeeded);
        await using (var session = await reservation)
        {
            await session.RecordIntentAsync(
                Target,
                _created.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.ReserveBinding,
                [],
                CancellationToken.None
            );
        }
        (await ExecuteAsync())
            .DownstreamPublicationStatus.Should()
            .Be(DocumentCacheDownstreamPublicationStatus.Possible);
    }

    [Test]
    public async Task It_reads_exposure_committed_by_a_reservation_that_wins_the_lock_first()
    {
        var session = await AcquireAsync();
        Task<DocumentCacheAdministrativeCommandResult> command = ExecuteAsync();
        try
        {
            command.IsCompleted.Should().BeFalse();
            await session.RecordIntentAsync(
                Target,
                _created.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.ReserveBinding,
                [],
                CancellationToken.None
            );
        }
        finally
        {
            await session.DisposeAsync();
        }
        (await command)
            .Classification.Should()
            .Be(DocumentCacheAdministrativeCommandClassification.DownstreamHistoryPresentOrUnknown);
    }

    [Test]
    public async Task It_bounds_lock_contention_without_entering_E18()
    {
        await using var session = await AcquireAsync();
        var history = new CdcDownstreamPublicationHistoryProvider(
            _store,
            "local",
            provider,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(20)
        );
        var result = await history.ExecuteAsync(
            Request(),
            () => throw new AssertionException("Must not enter E18."),
            CancellationToken.None
        );
        result.DownstreamPublicationStatus.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
        result.Mutated.Should().BeFalse();
    }

    [Test]
    public async Task It_preserves_cancellation_while_waiting_for_the_controller_lock()
    {
        await using var session = await AcquireAsync();
        using CancellationTokenSource cancellation = new();
        Task<DocumentCacheAdministrativeCommandResult> pending = _history.ExecuteAsync(
            Request(),
            EvaluateAsync,
            cancellation.Token
        );
        await cancellation.CancelAsync();
        Func<Task> act = () => pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task It_releases_the_lock_and_invalidates_inherited_scope_after_command_failure()
    {
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<DocumentCacheDownstreamPublicationHistoryObservation> child = null!;
        Func<Task> act = () =>
            _history.ExecuteAsync(
                Request(),
                () =>
                {
                    child = Task.Run(async () =>
                    {
                        await resume.Task;
                        return await _history.ObserveAsync(Key, Fingerprint);
                    });
                    throw new InvalidOperationException("command-failure");
                },
                CancellationToken.None
            );
        await act.Should().ThrowAsync<InvalidOperationException>();
        resume.SetResult();
        (await child).Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
        await using var session = await AcquireAsync();
    }

    [Test]
    public async Task It_keeps_initial_guarded_activation_distinct_from_internal_only_history()
    {
        await using var session = await AcquireAsync();
        var inner = A.Fake<IDocumentCacheAdministrativeCommandRunner>();
        var expected = new DocumentCacheAdministrativeCommandResult(
            DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
            Request().TargetKey,
            DocumentCacheAdministrativeCommandClassification.Succeeded
        );
        A.CallTo(() =>
                inner.ExecuteAsync(
                    A<DocumentCacheAdministrativeCommandRunnerRequest>._,
                    A<IDocumentCacheAdministrativeCommandWorkflow>._,
                    A<CancellationToken>._
                )
            )
            .Returns(expected);
        var runner = new CdcHistoryGatedAdministrativeCommandRunner(inner, _history);
        (
            await runner.ExecuteAsync(
                Request(DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation),
                A.Fake<IDocumentCacheAdministrativeCommandWorkflow>()
            )
        )
            .Should()
            .BeSameAs(expected);
    }
}
