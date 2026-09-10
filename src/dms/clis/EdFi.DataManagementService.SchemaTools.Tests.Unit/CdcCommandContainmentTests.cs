// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using CoreProvider = EdFi.DataManagementService.Core.DocumentCache.Cdc.CdcProvider;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

public partial class Given_Cdc_command_configuration
{
    private const string PreparationSentinel = "private-cms-schema-connection-sentinel";

    private static IEnumerable<TestCaseData> PreparationFailures()
    {
        foreach (
            var operation in new[]
            {
                CdcCommandOperation.Status,
                CdcCommandOperation.Watch,
                CdcCommandOperation.Stop,
                CdcCommandOperation.Start,
                CdcCommandOperation.Restart,
                CdcCommandOperation.Resume,
                CdcCommandOperation.IncreaseRecordSize,
            }
        )
        {
            foreach (
                string boundary in new[]
                {
                    "schema-load",
                    "schema-result",
                    "ddl-inventory",
                    "cms",
                    "target",
                    "membership",
                    "runtime-schema",
                }
            )
            {
                foreach (bool terminal in new[] { false, true })
                {
                    yield return new TestCaseData(operation, boundary, terminal, false);
                    if (operation == CdcCommandOperation.IncreaseRecordSize)
                    {
                        yield return new TestCaseData(operation, boundary, terminal, true);
                    }
                }
            }
        }
    }

    [TestCaseSource(nameof(PreparationFailures))]
    public async Task It_dispatches_containment_and_stop_before_fallible_preparation(
        CdcCommandOperation operation,
        string boundary,
        bool terminal,
        bool pendingIncrease
    )
    {
        var request = await RequestAsync();
        var services = new ServiceCollection();
        services.AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = _root);
        await using var scope = services.BuildServiceProvider();
        var bindings = scope.GetRequiredService<ICdcBindingLifecycleService>();
        await RetainBindingAsync(bindings, request, terminal);
        var original = await bindings.ExactMatchBindingAsync(request.Binding);
        var acknowledgement = new CdcCommandAcknowledgement(
            Guid.NewGuid(),
            request.Binding.ToCompleteBindingIdentity(),
            request.ConnectorPolicy.MaxRecordBytes,
            20000000,
            33554432,
            "operator",
            true,
            []
        );
        string acknowledgementPath = Path.Combine(_root, "acknowledgement-input.txt");
        await File.WriteAllTextAsync(
            acknowledgementPath,
            JsonSerializer.Serialize(acknowledgement, CdcCommandHost.JsonOptions)
        );
        if (pendingIncrease)
        {
            await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            );
            var journal = await session.ReadAsync(request.TargetIdentity, default);
            var increaseScope = new CdcRecordSizeIncreaseScope(
                acknowledgement.OperationId,
                acknowledgement.BindingIdentity,
                acknowledgement.PreviousMaxRecordBytes,
                acknowledgement.RequestedMaxRecordBytes
            );
            var confirmation = session.BeginRecordSizeAcknowledgement(journal.WorkflowId, increaseScope);
            await confirmation.ConfirmAndRunAsync(
                new(
                    increaseScope,
                    new(confirmation.InvocationId, "operator", DateTimeOffset.UtcNow, true, []),
                    true
                ),
                _ => Task.FromResult(true),
                default
            );
        }
        List<string> trace = [];
        using var http = new ContainmentHandler(request.Binding.ConnectorName, trace)
        {
            Configuration = pendingIncrease
                ? new Dictionary<string, string>
                {
                    ["producer.override.max.request.size"] = "10000000",
                    ["producer.override.buffer.memory"] = "33554432",
                }
                : null!,
        };
        var worker = A.Fake<ICdcWorkerInspectionTransport>();
        A.CallTo(() => worker.InspectAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                trace.Add("worker");
                return new CdcTransportResult<CdcWorkerInspection>.Observed(
                    new(
                        "worker-process",
                        request.WorkerMetricsEndpoint,
                        new Dictionary<string, string>
                        {
                            ["bootstrap.servers"] = request.ConnectorPolicy.KafkaBootstrapServers,
                            ["group.id"] = request.WorkerPolicy.WorkerKey.Value,
                            ["offset.storage.topic"] = request.WorkerPolicy.OffsetStorageTopic.Value,
                            ["connector.client.config.override.policy"] = "All",
                        },
                        request.WorkerPolicy.QualifiedImageDigest,
                        request.WorkerPolicy.HeapBytes,
                        "worker:8083"
                    )
                );
            });
        var realLoader = new ApiSchemaFileLoader(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        );
        var loader = A.Fake<IApiSchemaFileLoader>();
        A.CallTo(() => loader.Load(A<string>._, A<IReadOnlyList<string>>._))
            .ReturnsLazily(
                (string core, IReadOnlyList<string> extensions) =>
                {
                    trace.Add("schema-load");
                    if (boundary == "schema-load")
                    {
                        throw new IOException(PreparationSentinel);
                    }
                    if (boundary == "schema-result")
                    {
                        return new ApiSchemaFileLoadResult.FileReadErrorResult(
                            PreparationSentinel,
                            PreparationSentinel
                        );
                    }
                    var loaded = realLoader.Load(core, extensions);
                    return loaded;
                }
            );
        if (boundary == "ddl-inventory")
        {
            // Corrupt only a temporary synthetic copy. Loading/hashing still succeeds; relational
            // model derivation rejects the unsupported scalar type while constructing emitted inventory.
            var schema = JsonNode.Parse(await File.ReadAllTextAsync(_settings["Cdc:Schemas:0"]!))!;
            schema["projectSchema"]!["resourceSchemas"]!["widgets"]!["jsonSchemaForInsert"]!["properties"]![
                "widgetId"
            ]!["type"] = PreparationSentinel;
            string path = Path.Combine(_root, "invalid-schema.txt");
            await File.WriteAllTextAsync(path, schema.ToJsonString());
            _settings["Cdc:Schemas:0"] = path;
            var normalized = realLoader
                .Load(path, [])
                .Should()
                .BeOfType<ApiSchemaFileLoadResult.SuccessResult>()
                .Subject;
            var effective = SchemaBuilder().Build(normalized.NormalizedNodes);
            Action emit = () =>
                DdlPipelineHelpers.BuildDdlEmissionForDialect(
                    effective,
                    provider == CoreProvider.Postgresql ? SqlDialect.Pgsql : SqlDialect.Mssql
                );
            emit.Should().Throw<Exception>();
        }
        if (boundary == "membership")
        {
            _settings["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "99";
        }
        // Unreachable CMS and a failed schema path must not be consulted by explicit shutdown.
        _settings["ConfigurationServiceSettings:BaseUrl"] = "http://127.0.0.1:1";
        _settings["Cdc:Timing:PollMilliseconds"] = "1";
        var runner = new CdcCommandRunner(loader, SchemaBuilder())
        {
            CreateConnectClient = () => new HttpClient(http, disposeHandler: false),
            CreateWorker = _ => worker,
            CreateProjectionRuntime = (_, _, _, _) =>
            {
                trace.Add("runtime-" + boundary);
                if (boundary == "cms")
                {
                    throw new HttpRequestException(PreparationSentinel);
                }
                if (boundary == "runtime-schema")
                {
                    throw new InvalidOperationException(PreparationSentinel);
                }
                return Task.FromResult<CdcTransportResult<ICdcProjectionRuntime>>(
                    new CdcTransportResult<ICdcProjectionRuntime>.Unavailable(
                        new(CdcDeploymentComponent.Projection, CdcDeploymentFailure.Unavailable)
                    )
                );
            },
        };
        string settingsPath = Path.Combine(_root, "settings.txt");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(_settings.AsEnumerable().ToDictionary(p => p.Key, p => p.Value))
        );
        using var output = new StringWriter();
        var result = await runner.RunAsync(
            new(operation, settingsPath, _root, 2, 0, false, acknowledgementPath, true),
            output,
            default
        );
        var after = await bindings.ExactMatchBindingAsync(request.Binding);
        after
            .State.Should()
            .BeEquivalentTo(original.State!, options => options.Excluding(value => value.ObservedAt));
        JsonSerializer
            .Serialize(result, CdcCommandHost.JsonOptions)
            .Should()
            .NotContain(PreparationSentinel)
            .And.NotContain("secret-database")
            .And.NotContain("secret-sentinel");
        output.ToString().Should().NotContain(PreparationSentinel);
        if (operation == CdcCommandOperation.Stop)
        {
            result.Succeeded.Should().BeTrue();
            result
                .Data.Should()
                .BeOfType<CdcManagedLifecycleResult>()
                .Which.TargetShutdownVerified.Should()
                .BeTrue();
            trace
                .Should()
                .NotContain(t =>
                    t.StartsWith("schema", StringComparison.Ordinal)
                    || t.StartsWith("runtime", StringComparison.Ordinal)
                );
            trace.Should().Contain("worker").And.Contain("stop").And.Contain("status");
            await using (
                var shutdownSession = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(1),
                    default
                )
            )
            {
                await CdcWorkerStartup.RequireManagedShutdownAsync(shutdownSession, request, default);
            }
        }
        else
        {
            result.Succeeded.Should().BeFalse();
            var target = operation switch
            {
                CdcCommandOperation.Start or CdcCommandOperation.Restart or CdcCommandOperation.Resume =>
                    result.Data.Should().BeOfType<CdcManagedLifecycleResult>().Subject.Observation,
                CdcCommandOperation.IncreaseRecordSize => result
                    .Data.Should()
                    .BeOfType<CdcRecordSizeIncreaseResult>()
                    .Subject.Observation,
                _ => result
                    .Data.Should()
                    .BeOfType<CdcControllerStatusResult>()
                    .Subject.Targets.Should()
                    .ContainSingle()
                    .Subject,
            };
            target.Should().NotBeNull();
            target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Projection);
            target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            target.Status.SourceHistory.IncidentLatched.Should().Be(terminal);
            if (terminal)
            {
                target.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
                target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
                int stopIndex = pendingIncrease ? 1 : 0;
                trace[stopIndex].Should().Be("stop");
                trace[stopIndex + 1].Should().Be("status");
                trace.Count(t => t == "stop").Should().Be(operation == CdcCommandOperation.Watch ? 2 : 1);
            }
            else
            {
                target.Containment.Should().Be(CdcConnectorContainmentState.NotRequired);
                target.Status.SourceHistory.Continuity.Should().NotBe(CdcSourceHistoryContinuity.Lost);
                trace.Should().NotContain("stop");
            }
            if (boundary is "schema-load" or "schema-result" or "ddl-inventory" or "membership")
            {
                trace.Should().NotContain(t => t.StartsWith("runtime", StringComparison.Ordinal));
            }
            else
            {
                trace.Should().Contain("runtime-" + boundary);
            }
            if (operation == CdcCommandOperation.Watch)
            {
                output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
            }
        }
        trace.Should().NotContain("unexpected-http");
        await using var finalSession = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
        var finalJournal = await finalSession.ReadAsync(request.TargetIdentity, default);
        finalJournal.Operations.Should().NotContain(o => o.Effect == CdcWorkflowEffect.ResumeConnector);
        var increases = finalJournal
            .Operations.Where(o => o.Effect == CdcWorkflowEffect.IncreaseRecordSize)
            .ToArray();
        increases.Should().HaveCount(pendingIncrease ? 1 : 0);
        if (pendingIncrease)
        {
            increases[0].Completions.Should().BeEmpty();
            increases[0].RecordSizeIncrease.Single().Acknowledgements.Should().HaveCount(2);
            trace[0].Should().Be("configuration");
        }
    }

    private static async Task RetainBindingAsync(
        ICdcBindingLifecycleService bindings,
        CdcDeploymentRequest request,
        bool terminal
    )
    {
        (await bindings.CreateBindingIfAbsentAsync(request.Binding))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.Succeeded);
        if (terminal)
        {
            (
                await bindings.LatchSourceHistoryLossAsync(
                    new(
                        CdcJsonContract.CurrentContractVersion,
                        CdcIncidentType.SourceHistoryContinuityLost,
                        DateTimeOffset.UtcNow,
                        request.Binding.ToCompleteBindingIdentity(),
                        CdcIncidentFailureCategory.ConnectOffsetMissing,
                        new(
                            request.Binding.ConnectorName,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            [CdcIncidentUnavailableFact.ConnectOffset]
                        )
                    )
                )
            )
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
        }
    }

    private static IEnumerable<TestCaseData> JournalFailures()
    {
        foreach (var operation in new[] { CdcCommandOperation.Status, CdcCommandOperation.Watch })
        {
            foreach (
                string failure in new[]
                {
                    "missing",
                    "unreadable",
                    "corrupt",
                    "source-missing",
                    "source-incomplete",
                }
            )
            {
                foreach (
                    string binding in new[]
                    {
                        "terminal",
                        "no-incident",
                        "missing",
                        "topic-mismatch",
                        "partition-mismatch",
                        "target-mismatch",
                        "provider-mismatch",
                    }
                )
                {
                    yield return new TestCaseData(operation, failure, binding);
                }
            }
        }
        foreach (
            var operation in new[]
            {
                CdcCommandOperation.Enable,
                CdcCommandOperation.Validate,
                CdcCommandOperation.Start,
                CdcCommandOperation.Restart,
                CdcCommandOperation.Resume,
                CdcCommandOperation.StartWorker,
            }
        )
        {
            yield return new TestCaseData(operation, "missing", "terminal");
            yield return new TestCaseData(operation, "corrupt", "terminal");
        }
    }

    [TestCaseSource(nameof(JournalFailures))]
    public async Task It_contains_retained_incidents_through_the_request_builder_despite_journal_failure(
        CdcCommandOperation operation,
        string failure,
        string binding
    )
    {
        var request = await RequestAsync();
        var services = new ServiceCollection();
        services.AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = _root);
        await using var scope = services.BuildServiceProvider();
        var bindings = scope.GetRequiredService<ICdcBindingLifecycleService>();
        if (binding != "missing")
        {
            await RetainBindingAsync(bindings, request, binding != "no-incident");
        }
        var original = await bindings.ReadBindingAsync(request.Binding.ToBindingIdentity());
        string journalPath = Directory
            .GetFiles(Path.Combine(_root, "workflows"), "*.json", SearchOption.AllDirectories)
            .Single();
        string journal = await File.ReadAllTextAsync(journalPath);
        switch (failure)
        {
            case "missing":
                File.Delete(journalPath);
                break;
            case "unreadable":
                // A directory in place of the journal deterministically denies file reads on all hosts.
                File.Delete(journalPath);
                Directory.CreateDirectory(journalPath);
                break;
            case "corrupt":
                await File.WriteAllTextAsync(journalPath, "{\"private\":\"" + PreparationSentinel);
                break;
            default:
                var document = JsonNode.Parse(journal)!;
                var operations = document["operations"]!.AsArray();
                var source = operations.Single(o => o!["effect"]!.GetValue<string>() == "AssociateSource")!;
                if (failure == "source-missing")
                {
                    operations.Remove(source);
                }
                else
                {
                    source["completions"] = new JsonArray();
                }
                await File.WriteAllTextAsync(journalPath, document.ToJsonString());
                break;
        }
        switch (binding)
        {
            case "topic-mismatch":
                _settings["Cdc:TopicPrefix"] = "other";
                break;
            case "partition-mismatch":
                _settings["Cdc:PartitionCount"] = "2";
                break;
            case "target-mismatch":
                _settings["Cdc:DataStoreId"] = "99";
                break;
            case "provider-mismatch":
                _settings["Cdc:Provider"] = provider == CoreProvider.Postgresql ? "mssql" : "postgresql";
                _settings["AppSettings:Datastore"] = _settings["Cdc:Provider"];
                _settings["Cdc:SetupConnectionString"] =
                    provider == CoreProvider.Postgresql
                        ? "Server=localhost;Database=secret-database;User ID=setup;Password=secret-sentinel"
                        : "Host=localhost;Database=secret-database;Username=setup;Password=secret-sentinel";
                break;
        }
        var survivingFiles = Directory
            .GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("controller.lock", StringComparison.Ordinal))
            .ToDictionary(path => path, File.ReadAllText);
        List<string> trace = [];
        using var http = new ContainmentHandler(request.Binding.ConnectorName, trace);
        var runtime = A.Fake<ICdcProjectionRuntime>();
        var runner = new CdcCommandRunner(
            new ApiSchemaFileLoader(
                new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
                NullLogger<ApiSchemaFileLoader>.Instance
            ),
            SchemaBuilder()
        )
        {
            CreateConnectClient = () => new HttpClient(http, disposeHandler: false),
            CreateWorker = _ => A.Fake<ICdcWorkerInspectionTransport>(),
            CreateProjectionRuntime = (_, _, _, _) =>
            {
                trace.Add("runtime");
                return Task.FromResult<CdcTransportResult<ICdcProjectionRuntime>>(
                    new CdcTransportResult<ICdcProjectionRuntime>.Observed(runtime)
                );
            },
        };
        _settings["Cdc:Timing:PollMilliseconds"] = "1";
        string settingsPath = Path.Combine(_root, "settings.txt");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(_settings.AsEnumerable().ToDictionary(p => p.Key, p => p.Value))
        );
        using var output = new StringWriter();
        var result = await runner.RunAsync(
            new(operation, settingsPath, _root, 2, 0, false, "", false),
            output,
            default
        );
        result.Succeeded.Should().BeFalse();
        bool observation = operation is CdcCommandOperation.Status or CdcCommandOperation.Watch;
        bool matching = binding is "terminal" or "no-incident";
        if (observation && matching)
        {
            var status = result.Data.Should().BeOfType<CdcControllerStatusResult>().Subject;
            var target = status.Targets.Should().ContainSingle().Subject;
            target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.WorkflowState);
            target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            target.Status.SourceHistory.IncidentLatched.Should().Be(binding == "terminal");
            if (binding == "terminal")
            {
                target.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
                target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
                trace.Take(2).Should().Equal("stop", "status");
                trace.Count(t => t == "stop").Should().Be(operation == CdcCommandOperation.Watch ? 2 : 1);
                trace.Count(t => t == "status").Should().Be(2 * trace.Count(t => t == "stop"));
            }
            else
            {
                target.Containment.Should().Be(CdcConnectorContainmentState.NotRequired);
                target.Status.SourceHistory.Continuity.Should().NotBe(CdcSourceHistoryContinuity.Lost);
                trace.Should().NotContain("stop");
            }
            trace.Should().Contain("runtime");
            if (operation == CdcCommandOperation.Watch)
            {
                var passes = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                passes.Should().HaveCount(2);
                foreach (string pass in passes)
                {
                    var passStatus = JsonSerializer.Deserialize<CdcControllerStatusResult>(
                        pass,
                        CdcCommandHost.JsonOptions
                    )!;
                    passStatus.Targets.Single().Status.Readiness.Should().Be(CdcReadiness.NotReady);
                    passStatus.Targets.Single().Containment.Should().Be(target.Containment);
                }
            }
        }
        else
        {
            trace.Should().BeEmpty();
            result
                .Diagnostics.Should()
                .Contain(d =>
                    d.Component
                    == (matching ? CdcDeploymentComponent.WorkflowState : CdcDeploymentComponent.Request)
                );
        }
        trace.Should().NotContain("unexpected-http");
        JsonSerializer
            .Serialize(result, CdcCommandHost.JsonOptions)
            .Should()
            .NotContain(PreparationSentinel)
            .And.NotContain("secret-database")
            .And.NotContain("secret-sentinel");
        output.ToString().Should().NotContain(PreparationSentinel).And.NotContain("secret-sentinel");
        var after = await bindings.ReadBindingAsync(request.Binding.ToBindingIdentity());
        after
            .State.Should()
            .BeEquivalentTo(original.State!, options => options.Excluding(value => value.ObservedAt));
        foreach (var file in survivingFiles)
        {
            (await File.ReadAllTextAsync(file.Key)).Should().Be(file.Value);
        }
        Directory
            .GetFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path =>
                path != settingsPath && !path.EndsWith("controller.lock", StringComparison.Ordinal)
            )
            .Should()
            .BeEquivalentTo(survivingFiles.Keys);
        File.Exists(journalPath).Should().Be(failure is not ("missing" or "unreadable"));
        Directory.Exists(journalPath).Should().Be(failure == "unreadable");
    }

    [TestCase(CdcCommandOperation.Status, false)]
    [TestCase(CdcCommandOperation.Watch, false)]
    [TestCase(CdcCommandOperation.Status, true)]
    [TestCase(CdcCommandOperation.Watch, true)]
    public async Task It_returns_terminal_containment_after_the_command_deadline_but_honors_caller_cancellation(
        CdcCommandOperation operation,
        bool cancelCaller
    )
    {
        _settings["Cdc:Timing:CallMilliseconds"] = "600";
        _settings["Cdc:Timing:WaitMilliseconds"] = "1000";
        _settings["Cdc:Timing:PollMilliseconds"] = "1";
        var request = await RequestAsync();
        var services = new ServiceCollection();
        services.AddDmsCdcControlPlane();
        services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = _root);
        await using var scope = services.BuildServiceProvider();
        var bindings = scope.GetRequiredService<ICdcBindingLifecycleService>();
        await RetainBindingAsync(bindings, request, true);
        List<string> trace = [];
        using var caller = new CancellationTokenSource();
        using var http = new ContainmentHandler(request.Binding.ConnectorName, trace)
        {
            AfterStatus = ct => Task.Delay(100, ct),
            AfterStop = async ct =>
            {
                if (cancelCaller)
                {
                    await caller.CancelAsync();
                }
                // The command has less than one call budget left. Lose the HTTP response after
                // accepting stop; the production adapter and controller must independently read back.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
        };
        var runner = new CdcCommandRunner(A.Fake<IApiSchemaFileLoader>(), SchemaBuilder())
        {
            CreateRequest = async (_, _, _, _, _, ct, _, _) =>
            {
                await Task.Delay(700, ct);
                return request;
            },
            CreateConnectClient = () => new HttpClient(http, disposeHandler: false),
            CreateProjectionRuntime = (_, _, _, ct) =>
            {
                trace.Add("runtime");
                ct.ThrowIfCancellationRequested();
                throw new IOException(PreparationSentinel);
            },
        };
        string settingsPath = Path.Combine(_root, "deadline-settings.txt");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(_settings.AsEnumerable().ToDictionary(p => p.Key, p => p.Value))
        );
        using var output = new StringWriter();
        async Task Run()
        {
            var result = await runner.RunAsync(
                new(operation, settingsPath, _root, 3, 0, false, "", false),
                output,
                caller.Token
            );
            result.Succeeded.Should().BeFalse();
            var status = result.Data.Should().BeOfType<CdcControllerStatusResult>().Subject.Targets.Single();
            status.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
            status.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            status.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
            status.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
            trace.Count(t => t == "stop").Should().Be(1);
            trace.Should().NotContain("runtime");
            trace.Count(t => t == "status").Should().BeGreaterThan(0);
            result
                .Diagnostics.Should()
                .Contain(d =>
                    d.Component == CdcDeploymentComponent.Connect && d.Failure == CdcDeploymentFailure.Timeout
                );
            JsonSerializer
                .Serialize(result, CdcCommandHost.JsonOptions)
                .Should()
                .NotContain(PreparationSentinel);
            if (operation == CdcCommandOperation.Watch)
            {
                output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
            }
        }
        if (cancelCaller)
        {
            await FluentActions.Awaiting(Run).Should().ThrowAsync<OperationCanceledException>();
            trace.Should().NotContain("status");
        }
        else
        {
            await Run();
        }
        // The completed pass releases its controller lock, including when it outlives the command.
        await using var session = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }

    private static EffectiveSchemaSetBuilder SchemaBuilder() =>
        new(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

    private sealed class ContainmentHandler(string connector, List<string> trace) : HttpMessageHandler
    {
        private bool _stopped;
        internal IReadOnlyDictionary<string, string> Configuration { get; init; } = null!;
        internal Func<CancellationToken, Task> AfterStop { get; init; } = _ => Task.CompletedTask;
        internal Func<CancellationToken, Task> AfterStatus { get; init; } = _ => Task.CompletedTask;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path == $"/connectors/{connector}/stop")
            {
                _stopped = true;
                trace.Add("stop");
                await AfterStop(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get && path == $"/connectors/{connector}/status")
            {
                trace.Add("status");
                await AfterStatus(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            new
                            {
                                name = connector,
                                connector = new
                                {
                                    state = _stopped ? "STOPPED" : "RUNNING",
                                    worker_id = "worker:8083",
                                },
                                tasks = Array.Empty<object>(),
                            }
                        )
                    ),
                };
            }
            if (
                request.Method == HttpMethod.Get
                && path == $"/connectors/{connector}/config"
                && Configuration is not null
            )
            {
                trace.Add("configuration");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(Configuration)),
                };
            }
            trace.Add("unexpected-http");
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}

[TestFixture(Ddl.CdcProvider.Postgresql)]
[TestFixture(Ddl.CdcProvider.SqlServer)]
[Platform(Exclude = "Win", Reason = "Local CDC state requires Unix owner-only permissions.")]
internal class Given_Cdc_command_managed_start(Ddl.CdcProvider provider) : CdcReadinessTestBase(provider)
{
    private string _settingsPath = null!;
    private bool _stopped;
    private ICdcWorkerStartupTransport _infrastructure = null!;
    private ICdcBindingLifecycleService _bindings = null!;
    private ICdcKafkaArtifactCleanupAdapter _cleanupKafka = null!;
    private ICdcProviderArtifactCleanupAdapter _cleanupProvider = null!;
    private bool _cleanupOnline;

    [SetUp]
    public async Task SetupCommand()
    {
        _cleanupKafka = null!;
        _cleanupProvider = null!;
        ShortTiming(1000);
        _infrastructure = A.Fake<ICdcWorkerStartupTransport>();
        A.CallTo(() => _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => Trace("broker-start"));
        A.CallTo(() => _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() => Trace("worker-start"));
        _bindings = _services.GetRequiredService<ICdcBindingLifecycleService>();
        _stopped = false;
        A.CallTo(() => _connect.StopAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("stop");
                _stopped = true;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ResumeAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("resume");
                _stopped = false;
                return Observed(new CdcTransportAcknowledgement());
            });
        A.CallTo(() => _connect.ReadStatusAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("status");
                var live = Status();
                return Observed(
                    _stopped
                        ? new CdcConnectStatus(
                            live.Runtime with
                            {
                                ConnectorState = CdcConnectorRuntimeState.Stopped,
                                SoleTaskState = CdcConnectorRuntimeState.Stopped,
                                TaskCount = 0,
                                RunningTaskCount = 0,
                            },
                            live.WorkerId,
                            []
                        )
                        : live
                );
            });
        _settingsPath = Path.Combine(_root, "command-settings.json");
        await File.WriteAllTextAsync(
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
        (await CommandAsync(CdcCommandOperation.Stop)).Succeeded.Should().BeTrue();
        await using (
            var shutdownSession = await new LocalCdcWorkflowJournalStore(_root).AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            )
        )
        {
            await CdcWorkerStartup.RequireManagedShutdownAsync(shutdownSession, _request, default);
        }
        _trace.Clear();
        _disposals = 0;
        Fake.ClearRecordedCalls(_runtime);
        Fake.ClearRecordedCalls(_connect);
    }

    private Task<CdcCommandResult> CommandAsync(
        CdcCommandOperation operation = CdcCommandOperation.Start,
        CancellationToken token = default,
        string settingsPath = ""
    )
    {
        var runner = new CdcCommandRunner(
            A.Fake<IApiSchemaFileLoader>(),
            new(A.Fake<IEffectiveSchemaHashProvider>(), A.Fake<IResourceKeySeedProvider>())
        )
        {
            CreateRequest = (_, _, _, _, _, _, _, _) => Task.FromResult(_request),
            CreateProjectionRuntime = (_, _, _, _) =>
            {
                Trace("initialize");
                return Task.FromResult(Observed(_runtime));
            },
            CreateStartupTransport = _ => _infrastructure,
            ConfigureKafkaProvisioning = _ =>
                new(_root, _kafka, _runtime, new CdcKafkaProducerInspection(_connect, _worker)),
            ConfigureValidation = _ =>
                new(_root, _provider, _templates, _kafka, _connect, _worker, _metrics, _positions),
            ConfigureRetirement = original =>
                _cleanupKafka is null
                    ? original
                    : new CdcBindingRetirement(
                        _store,
                        _bindings,
                        _connect,
                        _cleanupKafka,
                        _cleanupProvider,
                        TimeProvider.System
                    )
                    {
                        WorkerStartup = original.WorkerStartup,
                    },
            ConfigureManagedLifecycle = _ =>
                new(_root, _provider, _templates, _kafka, _connect, _worker, _metrics, [_positions]),
        };
        return runner.RunAsync(
            new(
                operation,
                string.IsNullOrEmpty(settingsPath) ? _settingsPath : settingsPath,
                _root,
                1,
                Target.Generation,
                operation == CdcCommandOperation.Retire,
                "",
                false
            ),
            TextWriter.Null,
            token
        );
    }

    private async Task PrepareEarlyCleanupAsync(bool reserved)
    {
        string settings = await File.ReadAllTextAsync(_settingsPath);
        Directory.Delete(_root, true);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(
                _root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        await File.WriteAllTextAsync(_settingsPath, settings);
        var provisioner = A.Fake<ICdcManagedDatabaseProvisioner>();
        A.CallTo(() => provisioner.CreateDatabase()).Returns(true);
        A.CallTo(() => provisioner.ReadSourceFingerprintAsync(A<CancellationToken>._))
            .Returns(_request.Binding.PhysicalSourceFingerprint);
        var receipt = await new CdcManagedDatabaseProvisioning(_store).ProvisionAsync(
            Target,
            provisioner,
            purpose: CdcWorkflowPurpose.InitialCdcProvisioning
        );
        if (reserved)
        {
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            );
            await session.RecordIntentAsync(
                Target,
                receipt.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.ReserveBinding,
                [],
                default
            );
            (await _bindings.CreateBindingIfAbsentAsync(_request.Binding))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
            await session.RecordIntentAsync(
                Target,
                receipt.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.CreateProvider,
                [],
                default
            );
        }
        _cleanupOnline = false;
        A.CallTo(() => _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Invokes(() =>
            {
                Trace("worker-start");
                _cleanupOnline = true;
            });
        A.CallTo(() => _connect.ReadConfigurationAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                Trace("cleanup-config");
                return _cleanupOnline
                    ? new CdcTransportResult<IReadOnlyDictionary<string, string>>.Absent()
                    : new CdcTransportResult<IReadOnlyDictionary<string, string>>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    );
            });
        A.CallTo(() => _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .Returns(new CdcTransportResult<CdcConnectOffsetEvidence>.Absent());
        _cleanupKafka = A.Fake<ICdcKafkaArtifactCleanupAdapter>(o => o.Strict());
        _cleanupProvider = A.Fake<ICdcProviderArtifactCleanupAdapter>(o => o.Strict());
        A.CallTo(() =>
                _cleanupKafka.InspectRetirementOffsetsAsync(
                    A<CdcArtifactCleanupScope>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                Trace("cleanup-offsets");
                _cleanupOnline.Should().BeTrue();
                return Observed(CdcRetirementOffsetState.Absent);
            });
        foreach (var adapter in new ICdcArtifactCleanupAdapter[] { _cleanupKafka, _cleanupProvider })
        {
            A.CallTo(() =>
                    adapter.DeleteAsync(
                        A<CdcArtifactCleanupScope>._,
                        A<CdcGovernedArtifactKind>._,
                        A<CancellationToken>._
                    )
                )
                .ReturnsLazily(
                    (CdcArtifactCleanupScope scope, CdcGovernedArtifactKind kind, CancellationToken _) =>
                    {
                        Trace("cleanup-" + kind);
                        _cleanupOnline.Should().BeTrue();
                        scope.RequireAbsence.Should().Be(!reserved);
                        return Observed(
                            new CdcGovernedArtifact(
                                kind,
                                scope.Inventory.Single(a => a.Kind == kind).Name,
                                CdcCleanupState.NotFound,
                                "Verified"
                            )
                        );
                    }
                );
        }
        A.CallTo(() =>
                _cleanupProvider.DeleteOwnedSqlServerJobsAsync(
                    A<CdcArtifactCleanupScope>._,
                    A<LocalCdcWorkflowJournalStore.Session>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(() =>
            {
                Trace("cleanup-jobs");
                _cleanupOnline.Should().BeTrue();
                return Observed(new CdcTransportAcknowledgement());
            });
        _trace.Clear();
    }

    [TestCase(false, "broker-start")]
    [TestCase(false, "cleanup-offsets")]
    [TestCase(true, "worker-start")]
    public async Task It_CdcBindingRetirement_retries_interrupted_cleanup_from_Retiring(
        bool reserved,
        string boundary
    )
    {
        await PrepareEarlyCleanupAsync(reserved);
        bool failed = false;
        _onCall = call =>
        {
            if (!failed && call == boundary)
            {
                failed = true;
                throw new IOException("interrupted");
            }
        };
        await RunWrapperBridgeAsync("local", false, "", cleanup: true, retryCleanup: true);
        failed.Should().BeTrue();
        ReadJournal().Operations.Last().Completions.Should().ContainSingle();
    }

    [TestCase(false, "receipt")]
    [TestCase(false, "history")]
    [TestCase(false, "corrupt-history")]
    [TestCase(false, "orphan-incident")]
    [TestCase(false, "exposure")]
    [TestCase(true, "binding")]
    [TestCase(true, "registration")]
    [TestCase(false, "unavailable")]
    public async Task It_CdcBindingRetirement_rejects_unsafe_early_cleanup(bool reserved, string failure)
    {
        await PrepareEarlyCleanupAsync(reserved);
        if (failure == "orphan-incident" && !OperatingSystem.IsWindows())
        {
            string directory = Path.Combine(
                _root,
                "incidents",
                _request.Binding.DeploymentKey,
                _request.Binding.InstanceKey
            );
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
            string path = Path.Combine(directory, _request.Binding.Generation + ".json");
            await File.WriteAllTextAsync(path, "{}");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        if (failure is "receipt" or "history" or "corrupt-history" or "binding")
        {
            string directory = failure switch
            {
                "receipt" => "workflows",
                "binding" => "bindings",
                _ => "source-history",
            };
            string path = Directory
                .GetFiles(Path.Combine(_root, directory), "*.json", SearchOption.AllDirectories)
                .Single();
            if (failure == "corrupt-history")
            {
                await File.WriteAllTextAsync(path, "{");
            }
            else
            {
                File.Delete(path);
            }
        }
        if (failure is "exposure" or "registration")
        {
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            );
            var journal = await session.ReadAsync(Target, default);
            if (failure == "exposure")
            {
                await session.RecordSourceExposureAsync(
                    Target,
                    journal.WorkflowId,
                    _request.Binding.PhysicalSourceFingerprint,
                    default
                );
            }
            else
            {
                await session.RecordIntentAsync(
                    Target,
                    journal.WorkflowId,
                    Guid.NewGuid(),
                    CdcWorkflowEffect.RegisterConnector,
                    [],
                    default
                );
            }
        }
        if (failure == "unavailable")
        {
            A.CallTo(() =>
                    _cleanupKafka.InspectRetirementOffsetsAsync(
                        A<CdcArtifactCleanupScope>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    new CdcTransportResult<CdcRetirementOffsetState>.Unavailable(
                        new(CdcDeploymentComponent.Kafka, CdcDeploymentFailure.Unavailable)
                    )
                );
        }
        (await CommandAsync(CdcCommandOperation.Retire)).Succeeded.Should().BeFalse();
        if (failure != "unavailable")
        {
            _trace.Should().NotContain("broker-start").And.NotContain("worker-start");
        }
        _trace.Should().NotContain("resume").And.NotContain("initialize");
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task It_CdcBindingRetirement_holds_original_authorization_through_cleanup_startup(
        bool cancel,
        bool worker
    )
    {
        await PrepareEarlyCleanupAsync(false);
        using var caller = new CancellationTokenSource();
        Func<CdcDeploymentRequest, CancellationToken, Task> effect = async (
            CdcDeploymentRequest _,
            CancellationToken ct
        ) =>
        {
            var contender = new LocalCdcWorkflowJournalStore(_root);
            await FluentActions
                .Awaiting(async () =>
                {
                    await using var other = await contender.AcquireAsync(
                        TimeSpan.FromMilliseconds(25),
                        TimeSpan.FromMilliseconds(1),
                        default
                    );
                })
                .Should()
                .ThrowAsync<CdcWorkflowStateException>()
                .Where(e => e.Failure == CdcWorkflowStateFailure.LockTimeout);
            _cleanupOnline = true;
            if (cancel)
            {
                await caller.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        };
        if (worker)
        {
            A.CallTo(() =>
                    _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily(effect);
        }
        else
        {
            A.CallTo(() =>
                    _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily(effect);
        }
        if (cancel)
        {
            await FluentActions
                .Awaiting(() => CommandAsync(CdcCommandOperation.Retire, caller.Token))
                .Should()
                .ThrowAsync<OperationCanceledException>();
        }
        else
        {
            (await CommandAsync(CdcCommandOperation.Retire)).Succeeded.Should().BeTrue();
        }
        await using var released = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }

    [TestCase("local", false)]
    [TestCase("published", false)]
    [TestCase("local", true)]
    [TestCase("published", true)]
    public async Task It_CdcBindingRetirement_cleans_early_failure_through_the_offline_wrapper(
        string flavor,
        bool reserved
    )
    {
        await PrepareEarlyCleanupAsync(reserved);
        await RunWrapperBridgeAsync(flavor, false, "", cleanup: true);
        var completed = ReadJournal().Operations.Last();
        (await CommandAsync(CdcCommandOperation.Retire)).Succeeded.Should().BeTrue();
        ReadJournal().Operations.Last().Should().BeEquivalentTo(completed);
        ReadJournal().Operations.Last().Effect.Should().Be(CdcWorkflowEffect.Retire);
        ReadJournal().Operations.Last().Completions.Should().ContainSingle();
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .Status.Should()
            .Be(CdcControlPlaneOperationStatus.BindingMissing);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
        var history = await session.ReadSourcePublicationHistoryAsync(
            Target,
            _request.Binding.PhysicalSourceFingerprint,
            default
        );
        history
            .Transitions.Last()
            .Status.Should()
            .Be(
                reserved
                    ? EdFi.DataManagementService
                        .Core
                        .DocumentCache
                        .DocumentCacheDownstreamPublicationStatus
                        .Historical
                    : EdFi.DataManagementService
                        .Core
                        .DocumentCache
                        .DocumentCacheDownstreamPublicationStatus
                        .InternalOnly
            );
        _trace.Should().NotContain("initialize").And.NotContain("resume").And.NotContain("start");
    }

    [TestCase("broker-start", false, false)]
    [TestCase("worker-start", false, false)]
    [TestCase("broker-start", true, false)]
    [TestCase("worker-start", true, false)]
    [TestCase("worker-start", false, true)]
    public async Task It_CdcWorkerStartup_keeps_the_command_session_until_launch_finishes(
        string boundary,
        bool cancel,
        bool fail
    )
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        async Task Pause(CancellationToken token)
        {
            Trace(boundary);
            entered.SetResult();
            await release.Task.WaitAsync(token);
            if (fail)
            {
                throw new IOException("controlled infrastructure failure");
            }
        }
        if (boundary == "broker-start")
        {
            A.CallTo(() =>
                    _infrastructure.StartBrokerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily((CdcDeploymentRequest _, CancellationToken token) => Pause(token));
        }
        else
        {
            A.CallTo(() =>
                    _infrastructure.StartWorkerAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .ReturnsLazily((CdcDeploymentRequest _, CancellationToken token) => Pause(token));
        }
        var command = CommandAsync(CdcCommandOperation.StartWorker, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var contender = _store.AcquireAsync(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1), default);
        try
        {
            contender
                .IsCompleted.Should()
                .BeFalse(
                    "startup must retain authorization through the first broker and final worker effect"
                );
            if (cancel)
            {
                await caller.CancelAsync();
                await FluentActions.Awaiting(() => command).Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                release.SetResult();
                (await command).Succeeded.Should().Be(!fail);
            }
        }
        finally
        {
            await caller.CancelAsync();
            release.TrySetResult();
            try
            {
                await command;
            }
            catch (OperationCanceledException)
            {
                // Join the cancelled command even when a preceding assertion failed.
            }
            await using var competing = await contender;
        }
        if (cancel && boundary == "broker-start")
        {
            _trace.Should().NotContain("worker-start");
        }
    }

    [TestCase(CdcWorkflowEffect.ResumeConnector)]
    [TestCase(CdcWorkflowEffect.Retire)]
    public async Task It_CdcWorkerStartup_rejects_a_shutdown_consumed_before_command_lock_acquisition(
        CdcWorkflowEffect effect
    )
    {
        Task<CdcCommandResult> command;
        await using (
            var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            )
        )
        {
            command = CommandAsync(CdcCommandOperation.StartWorker);
            command.IsCompleted.Should().BeFalse();
            var journal = await session.ReadAsync(Target, default);
            await session.RecordIntentAsync(Target, journal.WorkflowId, Guid.NewGuid(), effect, [], default);
        }
        (await command).Succeeded.Should().BeFalse();
        _trace.Should().NotContain("broker-start").And.NotContain("worker-start");
    }

    [TestCase("local", false, "")]
    [TestCase("published", false, "")]
    [TestCase("local", true, "")]
    [TestCase("published", true, "")]
    [TestCase("local", false, "backlog")]
    [TestCase("published", false, "lag")]
    [TestCase("local", false, "persistent")]
    public async Task It_CdcWorkerStartup_controls_the_retained_wrapper_first_launch(
        string flavor,
        bool reject,
        string catchUp
    )
    {
        await RunWrapperBridgeAsync(flavor, reject, catchUp);
    }

    private async Task RunWrapperBridgeAsync(
        string flavor,
        bool reject,
        string catchUp,
        bool cleanup = false,
        bool retryCleanup = false
    )
    {
        int postResumePasses = 0;
        bool persistent = catchUp == "persistent";
        if (catchUp.Length > 0)
        {
            _backlog = catchUp != "lag";
            _lag = catchUp == "lag" ? 1001 : 1;
            _onCall = call =>
            {
                if (call == "metrics" && _trace.Contains("resume"))
                {
                    postResumePasses++;
                    if (postResumePasses > 3 && !persistent)
                    {
                        _backlog = false;
                        _lag = 1;
                    }
                }
            };
        }
        if (reject)
        {
            await using var session = await _store.AcquireAsync(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1),
                default
            );
            var journal = await session.ReadAsync(Target, default);
            await session.RecordIntentAsync(
                Target,
                journal.WorkflowId,
                Guid.NewGuid(),
                CdcWorkflowEffect.ResumeConnector,
                [],
                default
            );
        }
        string repository = TestContext.CurrentContext.TestDirectory;
        while (!Directory.Exists(Path.Combine(repository, "eng", "docker-compose")))
        {
            repository = Directory.GetParent(repository)!.FullName;
        }
        string bridge = Path.Combine(_root, "wrapper-bridge.json");
        await File.WriteAllTextAsync(
            bridge,
            JsonSerializer.Serialize(
                new
                {
                    Cleanup = cleanup,
                    RetryCleanup = retryCleanup,
                    Flavor = flavor,
                    Reject = reject,
                    CatchUpTimeout = persistent,
                    SettingsPath = _settingsPath,
                    StateRoot = _root,
                    Binding = _request.Binding,
                    Provider = Provider == Ddl.CdcProvider.Postgresql ? "postgresql" : "mssql",
                    IdentityProvider = Provider == Ddl.CdcProvider.Postgresql ? "self-contained" : "keycloak",
                    WorkerKey = _request.WorkerPolicy.WorkerKey.Value,
                    OffsetTopic = _request.WorkerPolicy.OffsetStorageTopic.Value,
                }
            )
        );
        var info = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["DMS_T57_BRIDGE"] = bridge;
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(
            "$r = Invoke-Pester -Path eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1 -FullName '*production controller session bridge*' -Output Detailed -PassThru; if ($r.PassedCount -ne 1 -or $r.FailedCount -ne 0) { exit 1 }"
        );
        using var process = System.Diagnostics.Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        int commands = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            while (!process.HasExited)
            {
                if (!File.Exists(bridge + ".request"))
                {
                    await Task.Delay(10, timeout.Token);
                    continue;
                }
                var arguments = JsonSerializer.Deserialize<string[]>(
                    await File.ReadAllTextAsync(bridge + ".request", timeout.Token)
                )!;
                File.Delete(bridge + ".request");
                var operation = arguments[1] switch
                {
                    "retire" => CdcCommandOperation.Retire,
                    "start-worker" => CdcCommandOperation.StartWorker,
                    _ => CdcCommandOperation.Start,
                };
                arguments[4].Should().Be("--state-path");
                arguments[5].Should().Be(_root);
                var result = await CommandAsync(operation, timeout.Token, arguments[3]);
                if (operation == CdcCommandOperation.Start && catchUp.Length > 0)
                {
                    result.Succeeded.Should().Be(!persistent, JsonSerializer.Serialize(result));
                    result
                        .Data.Should()
                        .BeOfType<CdcManagedLifecycleResult>()
                        .Which.Ready.Should()
                        .Be(!persistent);
                    postResumePasses.Should().BeGreaterThan(3);
                    ReadJournal().Operations.Last().Completions.Should().ContainSingle();
                }
                await File.WriteAllTextAsync(
                    bridge + ".response.tmp",
                    JsonSerializer.Serialize(result, CdcCommandHost.JsonOptions),
                    timeout.Token
                );
                File.Move(bridge + ".response.tmp", bridge + ".response");
                commands++;
            }
            await process.WaitForExitAsync(timeout.Token);
            process.ExitCode.Should().Be(0, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
        commands.Should().Be(reject || cleanup && !retryCleanup ? 1 : 2);
        if (reject)
        {
            _trace
                .Should()
                .NotContain("broker-start")
                .And.NotContain("worker-start")
                .And.NotContain("resume");
        }
        else if (!retryCleanup)
        {
            _trace.Count(t => t == "broker-start").Should().Be(1);
            _trace.Count(t => t == "worker-start").Should().Be(1);
        }
    }

    private void LoseHistory(string evidence)
    {
        if (evidence == "offset")
        {
            _offsetState = CdcConnectOffsetState.Missing;
        }
        else
        {
            // Successful provider inspection observes a recreated same-named slot/capture identity.
            _identity = new('b', 64);
        }
    }

    [TestCase("offset")]
    [TestCase("provider")]
    public async Task It_latches_and_contains_new_history_loss_through_managed_start_dispatch(string evidence)
    {
        LoseHistory(evidence);
        var before = ReadJournal();
        var result = await CommandAsync();
        result.Succeeded.Should().BeFalse();
        var retained = await _bindings.ExactMatchBindingAsync(_request.Binding);
        retained.State!.State.Should().Be(CdcBindingState.IncidentLatched);
        var lifecycle = result.Data.Should().BeOfType<CdcManagedLifecycleResult>().Subject;
        lifecycle.Observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        lifecycle.Observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        lifecycle.Observation.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
        _trace.Should().Contain("stop");
        _trace.Skip(_trace.IndexOf("stop") + 1).Should().Contain("status");
        _trace.Should().NotContain("start").And.NotContain("resume");
        _disposals.Should().Be(1);
        ReadJournal().Should().BeEquivalentTo(before);
        _offsetState = CdcConnectOffsetState.Streaming;
        _identity = new('a', 64);
        _trace.Clear();
        (await CommandAsync()).Succeeded.Should().BeFalse();
        (await CommandAsync(CdcCommandOperation.Restart)).Succeeded.Should().BeFalse();
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.State.Should()
            .Be(CdcBindingState.IncidentLatched);
        _trace.Should().NotContain("start").And.NotContain("resume");
        A.CallTo(() => _connect.RestartAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        _disposals.Should().Be(3);
        JsonSerializer.Serialize(result, CdcCommandHost.JsonOptions).Should().NotContain("private-source");
    }

    [TestCase("offset")]
    [TestCase("provider")]
    public async Task It_keeps_explicit_validate_observational_when_history_is_lost(string evidence)
    {
        LoseHistory(evidence);
        var before = ReadJournal();
        var result = await CommandAsync(CdcCommandOperation.Validate);
        result.Succeeded.Should().BeFalse();
        result
            .Data.Should()
            .BeOfType<CdcEstablishedValidationObservation>()
            .Which.Continuity.Should()
            .Be(CdcSourceHistoryContinuity.Lost);
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.State.Should()
            .Be(CdcBindingState.BindingPresent);
        _trace.Should().NotContain("stop").And.NotContain("start").And.NotContain("resume");
        ReadJournal().Should().BeEquivalentTo(before);
        _disposals.Should().Be(1);
    }

    [Test]
    public async Task It_starts_projection_after_locked_preflight_and_drains_queued_work_before_handoff()
    {
        _backlog = true;
        A.CallTo(() => _runtime.StartProcessingAsync(A<CancellationToken>._))
            .ReturnsLazily(async () =>
            {
                Trace("start");
                _stopped.Should().BeTrue();
                _trace.Should().Contain(["provider", "offset", "config", "brokers", "status"]);
                ReadJournal().Operations.Last().Effect.Should().Be(CdcWorkflowEffect.StopConnector);
                // Processing starts under the same exclusive session that authorized startup.
                await FluentActions
                    .Awaiting(async () =>
                    {
                        await using var competing = await _store.AcquireAsync(
                            TimeSpan.FromMilliseconds(30),
                            TimeSpan.FromMilliseconds(1),
                            default
                        );
                    })
                    .Should()
                    .ThrowAsync<CdcWorkflowStateException>()
                    .Where(e => e.Failure == CdcWorkflowStateFailure.LockTimeout);
                _backlog = false;
            });
        var result = await CommandAsync();
        result.Succeeded.Should().BeTrue(JsonSerializer.Serialize(result));
        result.Data.Should().BeOfType<CdcManagedLifecycleResult>().Which.Ready.Should().BeTrue();
        _trace.Count(t => t == "start").Should().Be(1);
        _trace
            .Skip(_trace.IndexOf("start") + 1)
            .TakeWhile(t => t != "resume")
            .Should()
            .Contain(["provider", "offset", "status"]);
        _trace.Count(t => t == "resume").Should().Be(1);
        _disposals.Should().Be(1);
        _barriers.Should().Be(0);
    }

    [Test]
    public async Task It_rechecks_history_after_processing_starts_before_resuming()
    {
        _onCall = name =>
        {
            if (name == "start")
            {
                _offsetState = CdcConnectOffsetState.Missing;
            }
        };
        var result = await CommandAsync();
        result.Succeeded.Should().BeFalse();
        var lifecycle = result.Data.Should().BeOfType<CdcManagedLifecycleResult>().Subject;
        lifecycle.Observation.IncidentPersistence.Should().Be(CdcIncidentPersistenceState.Persisted);
        lifecycle.Observation.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
        _trace.Should().Contain("start").And.Contain("stop").And.NotContain("resume");
        _disposals.Should().Be(1);
    }

    [TestCase("running")]
    [TestCase("unavailable")]
    public async Task It_rejects_invalid_start_preflight_before_processing(string evidence)
    {
        if (evidence == "running")
        {
            _stopped = false;
        }
        else
        {
            A.CallTo(() =>
                    _connect.ReadOffsetEvidenceAsync(A<CdcDeploymentRequest>._, A<CancellationToken>._)
                )
                .Returns(
                    new CdcTransportResult<CdcConnectOffsetEvidence>.Unavailable(
                        new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
                    )
                );
        }
        var before = ReadJournal();
        (await CommandAsync()).Succeeded.Should().BeFalse();
        _trace.Should().NotContain("start").And.NotContain("resume").And.NotContain("stop");
        (await _bindings.ExactMatchBindingAsync(_request.Binding))
            .State!.State.Should()
            .Be(CdcBindingState.BindingPresent);
        ReadJournal().Should().BeEquivalentTo(before);
        _disposals.Should().Be(1);
    }

    [TestCase("offset")]
    [TestCase("start")]
    public async Task It_preserves_caller_cancellation_and_disposes_the_start_runtime(string boundary)
    {
        using var caller = new CancellationTokenSource();
        _onCall = name =>
        {
            if (name == boundary)
            {
                caller.Cancel();
                caller.Token.ThrowIfCancellationRequested();
            }
        };
        await FluentActions
            .Awaiting(() => CommandAsync(token: caller.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();
        _trace.Should().NotContain("resume");
        _disposals.Should().Be(1);
        await using var session = await _store.AcquireAsync(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            default
        );
    }
}
