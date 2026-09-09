// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
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

    private static EffectiveSchemaSetBuilder SchemaBuilder() =>
        new(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

    private sealed class ContainmentHandler(string connector, List<string> trace) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path == $"/connectors/{connector}/stop")
            {
                trace.Add("stop");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.Method == HttpMethod.Get && path == $"/connectors/{connector}/status")
            {
                trace.Add("status");
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(
                                new
                                {
                                    name = connector,
                                    connector = new { state = "STOPPED", worker_id = "worker:8083" },
                                    tasks = Array.Empty<object>(),
                                }
                            )
                        ),
                    }
                );
            }
            trace.Add("unexpected-http");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
