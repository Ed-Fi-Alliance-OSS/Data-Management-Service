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
                    yield return new TestCaseData(operation, boundary, terminal);
                }
            }
        }
    }

    [TestCaseSource(nameof(PreparationFailures))]
    public async Task It_dispatches_containment_and_stop_before_fallible_preparation(
        CdcCommandOperation operation,
        string boundary,
        bool terminal
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
        List<string> trace = [];
        using var http = new ContainmentHandler(request.Binding.ConnectorName, trace);
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
            new(operation, settingsPath, _root, 2, 0, false, "", false),
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
            await CdcCommandRunner.RequireManagedShutdownAsync(_root, request, default);
        }
        else
        {
            result.Succeeded.Should().BeFalse();
            var status = result.Data.Should().BeOfType<CdcControllerStatusResult>().Subject;
            var target = status.Targets.Should().ContainSingle().Subject;
            target.Diagnostics.Should().Contain(d => d.Component == CdcDeploymentComponent.Projection);
            target.Status.Readiness.Should().Be(CdcReadiness.NotReady);
            target.Status.SourceHistory.IncidentLatched.Should().Be(terminal);
            if (terminal)
            {
                target.Containment.Should().Be(CdcConnectorContainmentState.Stopped);
                target.Status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Lost);
                trace[0].Should().Be("stop");
                trace[1].Should().Be("status");
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
    private ICdcBindingLifecycleService _bindings = null!;

    [SetUp]
    public async Task SetupCommand()
    {
        ShortTiming(1000);
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
        await CdcCommandRunner.RequireManagedShutdownAsync(_root, _request, default);
        _trace.Clear();
        _disposals = 0;
        Fake.ClearRecordedCalls(_runtime);
        Fake.ClearRecordedCalls(_connect);
    }

    private Task<CdcCommandResult> CommandAsync(
        CdcCommandOperation operation = CdcCommandOperation.Start,
        CancellationToken token = default
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
            ConfigureValidation = _ =>
                new(_root, _provider, _templates, _kafka, _connect, _worker, _metrics, _positions),
            ConfigureManagedLifecycle = _ =>
                new(_root, _provider, _templates, _kafka, _connect, _worker, _metrics, [_positions]),
        };
        return runner.RunAsync(
            new(operation, _settingsPath, _root, 1, Target.Generation, false, "", false),
            TextWriter.Null,
            token
        );
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
