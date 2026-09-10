// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
internal class Given_Cdc_command_enable_retry(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private string _settingsPath = null!;
    private ICdcWorkerStartupTransport _infrastructure = null!;
    private string JournalPath =>
        Directory.GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories).Single();

    [SetUp]
    public void SetupCommand()
    {
        // Reuse the qualified synthetic transport inventory, starting before provider intent.
        // Every interruption and subsequent retry below goes through the real command/coordinator.
        var journal = JsonNode.Parse(File.ReadAllText(JournalPath))!;
        var operations = journal["operations"]!.AsArray();
        while (operations.Count > 4)
        {
            operations.RemoveAt(operations.Count - 1);
        }
        File.WriteAllText(JournalPath, journal.ToJsonString());
        var historyPath = Directory
            .GetFiles(_root, "*.json", SearchOption.AllDirectories)
            .Single(p => JsonNode.Parse(File.ReadAllText(p))?["transitions"] is not null);
        var history = JsonNode.Parse(File.ReadAllText(historyPath))!;
        var transitions = history["transitions"]!.AsArray();
        while (transitions.Count > 2)
        {
            transitions.RemoveAt(transitions.Count - 1);
        }
        File.WriteAllText(historyPath, history.ToJsonString());
        _connectorExists = _exists = false;
        _posts = 0;
        _calls.Clear();
        _trace.Clear();
        ShortTiming(1000);
        _infrastructure = A.Fake<ICdcWorkerStartupTransport>();
        A.CallTo(() => _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => Trace("broker-start"));
        A.CallTo(() => _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => Trace("worker-start"));
        _settingsPath = Path.Combine(_root, "settings.json");
        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["AppSettings:Datastore"] =
                        Provider == Ddl.CdcProvider.Postgresql ? "postgresql" : "mssql",
                    ["Cdc:Provider"] = Provider == Ddl.CdcProvider.Postgresql ? "postgresql" : "sqlserver",
                    ["Cdc:DeploymentKey"] = Target.DeploymentKey,
                    ["Cdc:InstanceKey"] = Target.InstanceKey,
                    ["Cdc:DataStoreId"] = Target.DataStoreId,
                    ["Cdc:Generation"] = Target.Generation.ToString(),
                    ["Cdc:LagThresholdMilliseconds"] = "1000",
                    ["Cdc:Compose:Project"] = "test",
                    ["Cdc:Compose:File"] = "/unused-compose",
                    ["Cdc:Compose:EnvironmentFile"] = "/unused-env",
                    ["Cdc:Compose:BrokerSizeOverrideFile"] = "/unused-size",
                    ["Cdc:DurabilityProfile"] = "LocalSingleBroker",
                    ["Cdc:AuthorizationProfile"] = "AuthorizationDisabledLocal",
                    ["Cdc:KafkaAdminBootstrapServers"] = "127.0.0.1:1",
                    ["Cdc:SetupConnectionString"] =
                        Provider == Ddl.CdcProvider.Postgresql ? "Host=localhost" : "Server=localhost",
                }
            )
        );
        Fake.ClearRecordedCalls(_runtime);
    }

    private Task<CdcCommandResult> CommandAsync(CancellationToken token = default)
    {
        var runner = new CdcCommandRunner(
            A.Fake<IApiSchemaFileLoader>(),
            new(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>())
        )
        {
            CreateRequest = (_, _, _, _, _, _, _, _) => Task.FromResult(_request),
            CreateProjectionRuntime = (_, _, _, _) => Task.FromResult(Observed(_runtime)),
            ConfigureEnableWorkflow = _ =>
                new(
                    _root,
                    _provider,
                    _templates,
                    _kafka,
                    _connect,
                    _worker,
                    _infrastructure,
                    _metrics,
                    _positions
                ),
        };
        return runner.RunAsync(
            new(CdcCommandOperation.Enable, _settingsPath, _root, 1, Target.Generation, false, "", false),
            TextWriter.Null,
            token
        );
    }

    [TestCase("provider")]
    [TestCase("broker-start")]
    [TestCase("worker-start")]
    [TestCase("preflight")]
    [TestCase("post-after")]
    [TestCase("barrier")]
    [TestCase("metrics")]
    public async Task It_resumes_the_same_command_after_a_stage_interruption(string boundary)
    {
        var bindingPath = Directory
            .GetFiles(Path.Combine(_root, "bindings"), "*.json", SearchOption.AllDirectories)
            .Single();
        var binding = (await File.ReadAllBytesAsync(bindingPath));
        var workflow = ReadJournal().WorkflowId;
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == boundary)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            (await CommandAsync(cancellation.Token)).Succeeded.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            cancellation.IsCancellationRequested.Should().BeTrue();
        }
        _trace.Should().Contain(boundary);
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        var interrupted = ReadJournal();
        var consumptionPossible = interrupted.Operations.Any(o =>
            o.Effect == CdcWorkflowEffect.RegisterConnector
        );
        _onCall = _ => { };
        _calls.Clear();
        _trace.Clear();
        int barriers = _barriers;
        var result = await CommandAsync();
        result
            .Succeeded.Should()
            .BeTrue(JsonSerializer.Serialize(result.Diagnostics) + string.Join(",", _trace));
        ReadJournal().WorkflowId.Should().Be(workflow);
        ReadJournal().WriterPublicationAuthorized.Should().BeTrue();
        _barriers.Should().BeGreaterThan(barriers);
        _posts.Should().Be(1);
        (await File.ReadAllBytesAsync(bindingPath)).Should().Equal(binding);
        A.CallTo(() =>
                _runtime.ActivateAsync(
                    A<DocumentCacheGuardedNewEmptyActivationRequest>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        if (consumptionPossible)
        {
            _calls
                .Should()
                .OnlyContain(c =>
                    c.Mode == Ddl.CdcProviderSetupMode.ValidateOnly && !c.RequireUnconsumedInitialSlot
                );
            _trace.Should().NotContain("broker-start");
            _trace.Should().NotContain("worker-start");
        }
        var before = (await File.ReadAllBytesAsync(JournalPath));
        (await CommandAsync())
            .Succeeded.Should()
            .BeFalse("publication intent closes initial retry permanently");
        (await File.ReadAllBytesAsync(JournalPath)).Should().Equal(before);
    }

    [TestCase("provider-missing")]
    [TestCase("connector-missing")]
    [TestCase("offset-missing")]
    [TestCase("config-drift")]
    [TestCase("cache-ahead")]
    [TestCase("nonempty")]
    public async Task It_rejects_changed_live_evidence_after_interrupted_readiness(string defect)
    {
        using var cancellation = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == "metrics")
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            (await CommandAsync(cancellation.Token)).Succeeded.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            cancellation.IsCancellationRequested.Should().BeTrue();
        }
        _trace.Should().Contain("metrics");
        _onCall = _ => { };
        _trace.Clear();
        switch (defect)
        {
            case "provider-missing":
                _exists = false;
                break;
            case "connector-missing":
                _connectorExists = false;
                break;
            case "offset-missing":
                _offsetState = CdcConnectOffsetState.Missing;
                break;
            case "config-drift":
                _live["topic.prefix"] = "changed";
                break;
            case "cache-ahead":
                _latch = true;
                break;
            case "nonempty":
                _rows = true;
                break;
        }
        var retained = ReadJournal().Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider);
        (await CommandAsync()).Succeeded.Should().BeFalse();
        ReadJournal().WriterPublicationAuthorized.Should().BeFalse();
        JsonSerializer
            .Serialize(ReadJournal().Operations.Single(o => o.Effect == CdcWorkflowEffect.CreateProvider))
            .Should()
            .Be(JsonSerializer.Serialize(retained));
        _posts.Should().Be(1);
        _trace.Should().NotContain("broker-start");
    }

    [TestCase("missing")]
    [TestCase("corrupt")]
    [TestCase("source-history-only")]
    [TestCase("source-mismatch")]
    [TestCase("missing-history")]
    [TestCase("missing-binding")]
    [TestCase("cache-ahead")]
    [TestCase("nonempty")]
    [TestCase("managed-stop")]
    [TestCase("contradictory-provider")]
    public async Task It_rejects_ineligible_original_evidence_before_provider_or_Kafka_effects(string defect)
    {
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(JournalPath))!;
        switch (defect)
        {
            case "missing":
                File.Delete(JournalPath);
                break;
            case "corrupt":
                await File.WriteAllTextAsync(JournalPath, "{");
                break;
            case "source-history-only":
                journal["purpose"] = "SourceHistoryOnly";
                var operations = journal["operations"]!.AsArray();
                while (operations.Count > 2)
                {
                    operations.RemoveAt(operations.Count - 1);
                }
                await File.WriteAllTextAsync(JournalPath, journal.ToJsonString());
                break;
            case "source-mismatch":
                journal["operations"]![1]!["completions"]![0]!["evidence"]!["physicalSourceFingerprint"] =
                    "sha256:" + new string('f', 64);
                await File.WriteAllTextAsync(JournalPath, journal.ToJsonString());
                break;
            case "missing-history":
                Directory.Delete(Path.Combine(_root, "source-history"), true);
                break;
            case "missing-binding":
                Directory.Delete(Path.Combine(_root, "bindings"), true);
                break;
            case "cache-ahead":
                _latch = true;
                break;
            case "nonempty":
                _rows = true;
                break;
            case "managed-stop":
                await CompleteAsync(CdcWorkflowEffect.StopConnector);
                break;
            case "contradictory-provider":
                journal["operations"]![3]!["effect"] = "CreateProvider";
                await File.WriteAllTextAsync(JournalPath, journal.ToJsonString());
                break;
        }
        var result = await CommandAsync();
        result.Succeeded.Should().BeFalse();
        _trace.Should().NotContain("provider");
        _trace.Should().NotContain("broker-start");
        _posts.Should().Be(0);
        JsonSerializer.Serialize(result).Should().NotContain("private-source");
    }
}
