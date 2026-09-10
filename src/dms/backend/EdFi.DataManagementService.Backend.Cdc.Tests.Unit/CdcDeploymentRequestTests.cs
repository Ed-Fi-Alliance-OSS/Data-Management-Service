// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public class Given_CdcDeploymentRequest
{
    private CdcDeploymentRequest _request = null!;

    [SetUp]
    public void Setup() => _request = CdcDeploymentRequestTestData.Request();

    [Test]
    public void It_keeps_deferred_inventory_out_of_construction_and_serialization()
    {
        var calls = 0;
        var request = Deferred(() =>
        {
            calls++;
            return _request.ProviderSetup;
        });
        JsonSerializer.Serialize(request).Should().NotContain("ProviderSetup");
        calls.Should().Be(0);
        request.ProviderSetup.Should().BeSameAs(_request.ProviderSetup);
        request.ProviderSetup.Should().BeSameAs(_request.ProviderSetup);
        calls.Should().Be(1);
    }

    [Test]
    public void It_rejects_mismatched_deferred_provider_identity_before_consumption()
    {
        var request = Deferred(() =>
            CdcDeploymentRequestTestData.Request(CdcProvider.SqlServer).ProviderSetup
        );
        Action consume = () => _ = request.ProviderSetup;
        consume.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_mismatched_deferred_source_identity_before_consumption()
    {
        var setup = _request.ProviderSetup;
        var request = Deferred(() =>
            new(
                setup.Provider,
                setup.Mode,
                OtherPostgresqlSourceFingerprint,
                setup.SetupPrincipal,
                setup.ConnectorPrincipal,
                setup.ArtifactNames,
                setup.ArtifactOutput,
                setup.ExpectedSourceInventory,
                setup.DmsManagedTableInventory
            )
        );
        Action consume = () => _ = request.ProviderSetup;
        consume.Should().Throw<ArgumentException>();
    }

    private CdcDeploymentRequest Deferred(Func<CdcProviderSetupRequest> setup) =>
        CdcDeploymentRequest.CreateDeferred(
            _request.Binding,
            _request.DmsSettings,
            setup,
            _request.ConnectEndpoint,
            _request.WorkerMetricsEndpoint,
            _request.ConnectorPolicy,
            _request.WorkerPolicy,
            _request.ProviderConnectionProperties,
            _request.KafkaClientSecurityProperties,
            _request.Timing
        );

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_composes_the_existing_binding_and_template_contracts(CdcProvider provider)
    {
        CdcDeploymentRequest request = CdcDeploymentRequestTestData.Request(provider);
        CdcConnectorTemplateRequest template = request.CreateTemplateRequest(
            new(BindingGeneration, BuildProviderSetupResult(provider))
        );
        template.Binding.Should().BeSameAs(request.Binding);
        template.ProviderConnectionProperties.Should().BeSameAs(request.ProviderConnectionProperties);
        template.ArtifactInventory.Should().BeEquivalentTo(BuildCoreArtifactInventory(request.Binding));
    }

    [Test]
    public void It_defaults_to_ten_second_telemetry_freshness() =>
        _request.Timing.MaximumObservationAge.Should().Be(TimeSpan.FromSeconds(10));

    [Test]
    public void It_preserves_normalized_target_identity() =>
        _request.TargetIdentity.Should().Be(_request.Binding.ToTargetIdentity());

    [Test]
    public void It_excludes_runtime_configuration_and_credentials_from_workflow_json()
    {
        string json = JsonSerializer.Serialize(_request);
        json.Should()
            .NotContain("private-source-host")
            .And.NotContain("super-secret")
            .And.NotContain("DATABASE_PASSWORD")
            .And.NotContain("private-physical-table")
            .And.NotContain("8083")
            .And.NotContain("9404");
        using JsonDocument document = JsonDocument.Parse(json);
        document
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeEquivalentTo("Binding", "TargetIdentity", "Timing");
    }

    [Test]
    public void It_excludes_nested_configuration_from_diagnostic_text() =>
        _request.ToString().Should().Be(nameof(CdcDeploymentRequest));

    [TestCase("relative")]
    [TestCase("ftp://connect/")]
    [TestCase("http://user:super-secret@connect/")]
    [TestCase("http://connect/?token=super-secret")]
    [TestCase("http://connect/#super-secret")]
    public void It_rejects_unsafe_connect_endpoints_without_echoing_them(string endpoint)
    {
        Action act = () => CdcDeploymentRequestTestData.Request(endpoint: endpoint);
        act.Should().Throw<ArgumentException>().Which.ToString().Should().NotContain("super-secret");
    }

    [Test]
    public void It_validates_the_metrics_endpoint_too()
    {
        Action act = () => CdcDeploymentRequestTestData.Request(metricsEndpoint: "relative");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_literal_connector_secrets_using_template_rules()
    {
        Action act = () => CdcDeploymentRequestTestData.Request(password: "super-secret");
        act.Should().Throw<ArgumentException>().Which.ToString().Should().NotContain("super-secret");
    }

    [Test]
    public void It_rejects_a_different_physical_source()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Request(setupFingerprint: OtherPostgresqlSourceFingerprint);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_provider_mismatch()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Request(changeBinding: _ => BuildBinding(CdcProvider.SqlServer));
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_provider_artifacts_from_another_generation()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Request(
                setupArtifacts: BuildProviderArtifactNames(
                    CdcProvider.Postgresql,
                    BuildBinding(CdcProvider.Postgresql, bindingGeneration: 8)
                )
            );
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_noncanonical_binding_identity()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Request(changeBinding: binding =>
                binding with
                {
                    DataStoreId = "01",
                }
            );
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_insufficient_worker_heap()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Request(
                worker: CdcDeploymentRequestTestData.Worker(heapBytes: 67_108_864)
            );
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_keeps_shared_offset_storage_outside_the_binding_inventory()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Request(
                worker: CdcDeploymentRequestTestData.Worker(offsetTopic: _request.Binding.TopicName)
            );
        act.Should().Throw<ArgumentException>();
    }

    [TestCase("latest")]
    [TestCase("sha256:abc")]
    public void It_rejects_unpinned_worker_images(string digest)
    {
        Action act = () => CdcDeploymentRequestTestData.Worker(digest: digest);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_requires_authorization_for_production()
    {
        Action act = () =>
            CdcDeploymentRequestTestData.Worker(durability: CdcKafkaDurabilityProfile.Production);
        act.Should().Throw<ArgumentException>();
    }
}

[TestFixture]
public class Given_CdcDeploymentRequest_timing
{
    [TestCase(0, 300, 1, 10)]
    [TestCase(-1, 300, 1, 10)]
    [TestCase(301, 600, 1, 10)]
    [TestCase(1, 86401, 1, 10)]
    [TestCase(1, 0, 1, 10)]
    [TestCase(1, 300, 0, 10)]
    [TestCase(1, 300, 61, 10)]
    [TestCase(1, 300, 1, 0)]
    [TestCase(1, 300, 1, 61)]
    [TestCase(5, 1, 1, 10)]
    [TestCase(1, 1, 5, 10)]
    public void It_rejects_unbounded_or_inconsistent_intervals(int call, int wait, int poll, int age)
    {
        Action act = () =>
            new CdcDeploymentTiming(
                TimeSpan.FromSeconds(call),
                TimeSpan.FromSeconds(wait),
                TimeSpan.FromSeconds(poll),
                TimeSpan.FromSeconds(age)
            );
        act.Should().Throw<ArgumentException>();
    }
}

[TestFixture]
public class Given_CdcDeploymentRequest_results
{
    [Test]
    public void It_distinguishes_authoritative_absence_from_failed_lookup()
    {
        CdcTransportResult<string> absent = new CdcTransportResult<string>.Absent();
        CdcTransportResult<string> unavailable = new CdcTransportResult<string>.Unavailable(
            new(CdcDeploymentComponent.Connect, CdcDeploymentFailure.Unavailable)
        );
        absent.State.Should().NotBe(unavailable.State);
        JsonSerializer.Serialize(absent).Should().NotBe(JsonSerializer.Serialize(unavailable));
    }

    [Test]
    public void It_does_not_infer_absence_from_an_http_exception()
    {
        CdcDeploymentDiagnostic
            .FromException(
                CdcDeploymentComponent.Connect,
                new HttpRequestException("private-source-host", null, System.Net.HttpStatusCode.NotFound)
            )
            .Failure.Should()
            .Be(CdcDeploymentFailure.Unavailable);
    }

    [Test]
    public void It_serializes_the_failed_component_without_exception_details()
    {
        CdcDeploymentDiagnostic diagnostic = CdcDeploymentDiagnostic.FromException(
            CdcDeploymentComponent.Connect,
            new TimeoutException("Password=super-secret;Host=private-source-host")
        );
        string json = JsonSerializer.Serialize(diagnostic);
        json.Should().NotContain("super-secret").And.NotContain("private-source-host");
        using JsonDocument document = JsonDocument.Parse(json);
        document
            .RootElement.GetProperty("Component")
            .GetInt32()
            .Should()
            .Be((int)CdcDeploymentComponent.Connect);
    }

    [Test]
    public void It_keeps_raw_transport_payloads_out_of_json_and_text()
    {
        var observed = new CdcTransportResult<string>.Observed("super-secret");
        JsonSerializer.Serialize(observed).Should().NotContain("super-secret");
        observed.ToString().Should().NotContain("super-secret");
    }

    [Test]
    public void It_preserves_cancellation_and_its_token()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        OperationCanceledException exception = new("super-secret", source.Token);
        Action act = () => CdcDeploymentDiagnostic.FromException(CdcDeploymentComponent.Metrics, exception);
        act.Should().Throw<OperationCanceledException>().Which.Should().BeSameAs(exception);
    }

    [Test]
    public void It_classifies_authentication_failure_without_exposing_the_response()
    {
        CdcDeploymentDiagnostic
            .FromException(
                CdcDeploymentComponent.Kafka,
                new HttpRequestException("super-secret", null, System.Net.HttpStatusCode.Forbidden)
            )
            .Failure.Should()
            .Be(CdcDeploymentFailure.AuthenticationFailed);
    }
}
