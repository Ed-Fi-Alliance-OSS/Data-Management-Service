// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Ddl = EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Projection correlation against a stub DMS. The evidence is read from the process that runs the
/// projector, identity disagreement is classified rather than flattened, and evidence that could not
/// be obtained keeps admission closed instead of passing.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcProjectionCorrelation")]
public class Given_CdcProjectionCorrelationCollector
{
    private const string OperationId = "operation-1";
    private const string BearerToken = "sentinel-projection-status-token";
    private const string DmsBaseUrl = "http://dms.internal:8080";

    /// <summary>
    /// Stands in for the binding's own fingerprint, which is computed rather than constant, so a test
    /// can ask for a target that reports no physical source at all by passing null.
    /// </summary>
    private const string BindingFingerprintDefault = "the-binding-fingerprint";

    private static readonly DateTimeOffset ProjectionObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ObservedAt = ProjectionObservedAt.AddSeconds(5);

    [Test]
    public async Task It_reports_matched_when_the_running_dms_projects_the_binding_target()
    {
        (CdcProjectionCorrelationObservation observation, StubHttpMessageHandler handler) = await Collect(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target()))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Matched);
        observation.Diagnostics.Should().BeEmpty();
        observation.ContractVersion.Should().Be(CdcJsonContract.CurrentContractVersion);
        observation.OperationId.Should().Be(OperationId);
        observation.ObservedAt.Should().Be(ObservedAt);
        observation.ProjectionObservedAt.Should().Be(ProjectionObservedAt);
        observation.TargetIdentity.Should().Be(TargetIdentity());
        observation.Provider.Should().Be(CdcProvider.Postgresql);
        observation.PhysicalSourceFingerprint.Should().Be(BindingFingerprint());
        observation.E18TargetKey.TenantKey.Should().BeEmpty();
        observation.E18TargetKey.DataStoreId.Should().Be(1);
        Validate(observation).Succeeded.Should().BeTrue();

        // The evidence came from the running DMS over HTTP, authorized as the status endpoint requires.
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Uri.AbsolutePath.Should().Be("/health/document-cache");
        handler.Requests[0].Authorization.Should().Be($"Bearer {BearerToken}");
    }

    /// <summary>
    /// The projection-status settings are no longer a precondition of resolving the CDC options, so a
    /// deployment that configures none of them can still retire a binding. The verbs that DO read the
    /// projector's report must therefore refuse here instead, naming the setting that is missing rather
    /// than throwing on a malformed request URI.
    /// </summary>
    [Test]
    public async Task It_refuses_without_attempting_a_request_when_no_dms_base_url_is_configured()
    {
        (ICdcProjectionCorrelationCollector collector, StubHttpMessageHandler handler) = Collector(
            _ => throw new InvalidOperationException("The collector must not issue a request."),
            configureOptions: options => options.DmsBaseUrl = string.Empty
        );

        CdcProjectionCorrelationObservation observation = await collector.CollectAsync(Context());

        using AssertionScope assertions = new();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_refuses_without_attempting_a_request_when_no_bearer_token_is_configured()
    {
        (ICdcProjectionCorrelationCollector collector, StubHttpMessageHandler handler) = Collector(
            _ => throw new InvalidOperationException("The collector must not issue a request."),
            configureOptions: options => options.DmsBearerToken = string.Empty
        );

        CdcProjectionCorrelationObservation observation = await collector.CollectAsync(Context());

        using AssertionScope assertions = new();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        handler.Requests.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_the_projection_evidence_the_dms_published()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(
                HttpStatusCode.OK,
                StatusJson(
                    Target(
                        operationalHealth: DocumentCacheOperationalHealthStatus.NonOperational,
                        operationalHealthReason: DocumentCacheStatusReason.InventoryInvalid,
                        caughtUp: DocumentCacheCaughtUpStatus.NotCaughtUp,
                        caughtUpReason: DocumentCacheStatusReason.QueueNotEmpty,
                        queuePresence: DocumentCacheStatusQueuePresence.NotEmpty,
                        enqueueFailures: EnqueueFailures(
                            DocumentCacheStatusEnqueueFailureCategory.ProviderTimeout,
                            DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed
                        )
                    )
                )
            )
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Matched);
        observation.OperationalHealthStatus.Should().Be(DocumentCacheOperationalHealthStatus.NonOperational);
        observation.OperationalHealthReason.Should().Be(DocumentCacheStatusReason.InventoryInvalid);
        observation.CaughtUpStatus.Should().Be(DocumentCacheCaughtUpStatus.NotCaughtUp);
        observation.CaughtUpReason.Should().Be(DocumentCacheStatusReason.QueueNotEmpty);
        observation.QueuePresence.Should().Be(DocumentCacheStatusQueuePresence.NotEmpty);
        observation
            .EnqueueFailureCategories.Should()
            .BeEquivalentTo(
                new[]
                {
                    DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
                    DocumentCacheStatusEnqueueFailureCategory.ProviderTimeout,
                }
            );
    }

    [Test]
    public async Task It_reports_a_target_mismatch_when_the_dms_projects_another_target()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target(dataStoreId: 2)))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.TargetMismatch);
        Diagnostic(observation).Code.Should().Be("projectionTargetMismatch");
        Diagnostic(observation).Category.Should().Be(CdcDiagnosticCategory.TargetMismatch);

        // The key names the target the binding expects, and nothing about the projection is claimed.
        observation.E18TargetKey.DataStoreId.Should().Be(1);
        observation.OperationalHealthStatus.Should().Be(DocumentCacheOperationalHealthStatus.Unknown);
        observation.OperationalHealthReason.Should().Be(DocumentCacheStatusReason.UnresolvedTarget);
        observation.CaughtUpStatus.Should().Be(DocumentCacheCaughtUpStatus.Unknown);
        observation.QueuePresence.Should().Be(DocumentCacheStatusQueuePresence.Unavailable);
        observation.EnqueueFailureCategories.Should().BeEmpty();
    }

    [Test]
    public async Task It_reports_a_target_mismatch_when_the_dms_projects_nothing()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson())
        );

        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.TargetMismatch);
    }

    [Test]
    public async Task It_reports_a_provider_mismatch_when_the_dms_resolved_another_provider()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target(provider: RelationalProviderToken.SqlServerValue)))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.ProviderMismatch);
        Diagnostic(observation).Category.Should().Be(CdcDiagnosticCategory.ProviderMismatch);
        Diagnostic(observation).Observed.Should().Be(RelationalProviderToken.SqlServerValue);
    }

    [Test]
    public async Task It_reports_a_source_mismatch_when_the_dms_projects_another_physical_source()
    {
        string otherFingerprint = CdcControlTemplateTestData
            .SourceFingerprint(Ddl.CdcProvider.SqlServer)
            .Value;

        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target(physicalSourceFingerprint: otherFingerprint)))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.SourceMismatch);
        Diagnostic(observation).Category.Should().Be(CdcDiagnosticCategory.SourceMismatch);
        Diagnostic(observation).Observed.Should().Be(otherFingerprint);

        // The envelope still describes the source the binding captures, so the mismatch is carried by
        // the correlation state rather than by an envelope that claims a source it never bound.
        observation.PhysicalSourceFingerprint.Should().Be(BindingFingerprint());
    }

    [TestCase(null)]
    [TestCase("oracle")]
    public async Task It_reports_an_invalid_payload_when_the_provider_is_unreadable(string? provider)
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target(provider: provider)))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.InvalidPayload);
        Diagnostic(observation).Code.Should().Be("projectionProviderUnreadable");
        Diagnostic(observation).Observed.Should().Be(provider is null ? "absent" : "unrecognized");
    }

    [Test]
    public async Task It_reports_an_invalid_payload_when_the_physical_source_is_not_reported()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target(physicalSourceFingerprint: null)))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.InvalidPayload);
        Diagnostic(observation).Code.Should().Be("projectionPhysicalSourceUnreported");
    }

    [Test]
    public async Task It_reports_an_invalid_payload_when_the_binding_target_is_reported_twice()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.OK, StatusJson(Target(), Target()))
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.InvalidPayload);
        Diagnostic(observation).Code.Should().Be("projectionDuplicateTarget");
    }

    [Test]
    public async Task It_correlates_before_the_binding_has_a_physical_source()
    {
        CdcProjectionCorrelationObservation observation = await Observe(
            _ =>
                Json(
                    HttpStatusCode.OK,
                    StatusJson(
                        Target(
                            physicalSourceFingerprint: CdcControlTemplateTestData
                                .SourceFingerprint(Ddl.CdcProvider.SqlServer)
                                .Value
                        )
                    )
                ),
            context: new(OperationId, TargetIdentity(), PhysicalSourceFingerprint: null)
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Matched);

        // A binding with no recorded source adopts nothing from the payload, so the preflight cannot
        // turn the DMS's own reported source into evidence about a binding that does not exist yet.
        observation.PhysicalSourceFingerprint.Should().BeNull();
    }

    [Test]
    public async Task It_reports_an_unmapped_status_endpoint_distinctly_and_names_the_setting()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.NotFound, """{"message":"not found"}""")
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        Diagnostic(observation).Code.Should().Be("projectionStatusEndpointNotMapped");
        Diagnostic(observation).Message.Should().Contain("DataManagement:DocumentCache:Status:RequiredRole");
        Diagnostic(observation).Retryable.Should().BeFalse();
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    public async Task It_reports_a_rejected_token_distinctly_from_an_unmapped_route(HttpStatusCode statusCode)
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(statusCode, """{"detail":"forbidden"}""")
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        Diagnostic(observation).Code.Should().Be("projectionStatusUnauthorized");
        Diagnostic(observation).Retryable.Should().BeFalse();
    }

    [Test]
    public async Task It_reports_a_failing_dms_as_unavailable()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.ServiceUnavailable, """{"message":"unavailable"}""")
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        Diagnostic(observation).Code.Should().Be("projectionStatusUnavailable");
        Diagnostic(observation).Retryable.Should().BeTrue();
    }

    [Test]
    public async Task It_reports_an_unreachable_dms_as_unavailable_without_quoting_the_failure()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            throw new HttpRequestException("connection refused")
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        Diagnostic(observation).Code.Should().Be("projectionStatusUnavailable");
        Diagnostic(observation).Message.Should().NotContain("connection refused");
    }

    [Test]
    public async Task It_reports_a_status_read_that_never_answers_as_unavailable()
    {
        CdcProjectionCorrelationObservation observation = await Observe(
            _ => Json(HttpStatusCode.OK, StatusJson(Target())),
            neverAnswers: true,
            statusEndpointTimeout: TimeSpan.FromMilliseconds(20)
        );

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        Diagnostic(observation).Code.Should().Be("projectionStatusUnavailable");
    }

    [TestCase("{")]
    [TestCase("""{"contractVersion":1,"observedAt":"2026-08-28T09:00:00Z","targets":"none"}""")]
    [TestCase("")]
    public async Task It_reports_a_malformed_status_body_as_unavailable(string body)
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ => Json(HttpStatusCode.OK, body));

        using var _ = new AssertionScope();
        observation.CorrelationState.Should().Be(CdcProjectionCorrelationState.Unavailable);
        Diagnostic(observation).Code.Should().Be("projectionStatusMalformedResponse");
    }

    [Test]
    public async Task It_never_carries_the_status_credential_into_the_observation()
    {
        CdcProjectionCorrelationObservation observation = await Observe(_ =>
            Json(HttpStatusCode.Unauthorized, $$"""{"detail":"token {{BearerToken}} rejected"}""")
        );

        JsonSerializer.Serialize(observation).Should().NotContain(BearerToken);
    }

    /// <summary>
    /// The correlation must be read from the process that runs the projector. An in-process status
    /// service would report only that its runtime was not observed, and the enablement sequence would
    /// dead-end at an unknown caught-up state, so the collector takes no dependency that could reach
    /// one — neither the service itself nor a container it could resolve one from.
    /// </summary>
    [Test]
    public void It_never_resolves_the_in_process_document_cache_status_service()
    {
        Type[] dependencies =
        [
            .. typeof(CdcProjectionCorrelationCollector)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType),
            .. typeof(CdcProjectionCorrelationCollector)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(field => field.FieldType),
        ];

        using var _ = new AssertionScope();
        dependencies.Should().NotContain(typeof(IDocumentCacheStatusService));
        dependencies.Should().NotContain(typeof(IServiceProvider));
    }

    [Test]
    public async Task It_propagates_caller_cancellation_rather_than_reporting_a_timeout()
    {
        ICdcProjectionCorrelationCollector collector = Collector(
            _ => Json(HttpStatusCode.OK, StatusJson(Target())),
            neverAnswers: true
        ).Collector;
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        Func<Task> collect = () => collector.CollectAsync(Context(), cancellation.Token);

        await collect.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<CdcProjectionCorrelationObservation> Observe(
        Func<RecordedRequest, HttpResponseMessage> respond,
        CdcObservationContext? context = null,
        bool neverAnswers = false,
        TimeSpan? statusEndpointTimeout = null
    ) => (await Collect(respond, context, neverAnswers, statusEndpointTimeout)).Observation;

    private static async Task<(
        CdcProjectionCorrelationObservation Observation,
        StubHttpMessageHandler Handler
    )> Collect(
        Func<RecordedRequest, HttpResponseMessage> respond,
        CdcObservationContext? context = null,
        bool neverAnswers = false,
        TimeSpan? statusEndpointTimeout = null
    )
    {
        (ICdcProjectionCorrelationCollector collector, StubHttpMessageHandler handler) = Collector(
            respond,
            neverAnswers,
            statusEndpointTimeout
        );

        CdcProjectionCorrelationObservation observation = await collector.CollectAsync(
            context ?? Context(),
            CancellationToken.None
        );

        return (observation, handler);
    }

    private static (ICdcProjectionCorrelationCollector Collector, StubHttpMessageHandler Handler) Collector(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool neverAnswers = false,
        TimeSpan? statusEndpointTimeout = null,
        Action<CdcControlOptions>? configureOptions = null
    )
    {
        StubHttpMessageHandler handler = new(respond, neverAnswers);
        IHttpClientFactory httpClientFactory = A.Fake<IHttpClientFactory>();
        A.CallTo(() => httpClientFactory.CreateClient(A<string>._))
            .ReturnsLazily(() => new HttpClient(handler, disposeHandler: false));

        CdcControlOptions controlOptions = ControlOptions(statusEndpointTimeout);
        configureOptions?.Invoke(controlOptions);

        CdcProjectionCorrelationCollector collector = new(
            httpClientFactory,
            Options.Create(controlOptions),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcProjectionCorrelationCollector>.Instance
        );

        return (collector, handler);
    }

    private static string StatusJson(params DocumentCacheStatusTarget[] targets) =>
        JsonSerializer.Serialize(new DocumentCacheStatusResponse(ProjectionObservedAt, targets));

    private static DocumentCacheStatusTarget Target(
        string tenantKey = "",
        long dataStoreId = 1,
        string? provider = RelationalProviderToken.PostgresqlValue,
        string? physicalSourceFingerprint = BindingFingerprintDefault,
        DocumentCacheOperationalHealthStatus operationalHealth =
            DocumentCacheOperationalHealthStatus.Operational,
        DocumentCacheStatusReason operationalHealthReason = DocumentCacheStatusReason.None,
        DocumentCacheCaughtUpStatus caughtUp = DocumentCacheCaughtUpStatus.CaughtUp,
        DocumentCacheStatusReason caughtUpReason = DocumentCacheStatusReason.None,
        DocumentCacheStatusQueuePresence queuePresence = DocumentCacheStatusQueuePresence.Empty,
        DocumentCacheStatusEnqueueFailures? enqueueFailures = null
    ) =>
        new(
            DocumentCacheStatusTargetKey.FromTargetKey(DocumentCacheTargetKey.Create(tenantKey, dataStoreId)),
            targetGeneration: 1,
            processObservedAt: ProjectionObservedAt,
            durableObservedAt: ProjectionObservedAt,
            provider,
            physicalSourceFingerprint == BindingFingerprintDefault
                ? BindingFingerprint()
                : physicalSourceFingerprint,
            new DocumentCacheStatusResolutionComponent(
                DocumentCacheStatusResolutionStatus.Resolved,
                DocumentCacheStatusResolutionReason.None,
                ProjectionObservedAt,
                message: null
            ),
            new DocumentCacheStatusEligibilityComponent(
                DocumentCacheStatusEligibilityStatus.Eligible,
                DocumentCacheStatusReason.None,
                message: null
            ),
            new DocumentCacheStatusInventoryComponentGroup(
                ProjectionObservedAt,
                ValidInventory(),
                ValidInventory(),
                ValidInventory(),
                ValidInventory(),
                new DocumentCacheStatusEnqueueTriggerComponent(
                    DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                    DocumentCacheStatusInventoryReason.None,
                    message: null
                )
            ),
            new DocumentCacheStatusProviderPrerequisitesComponent(
                DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                DocumentCacheStatusProviderPrerequisiteReason.None,
                observedAt: null,
                NotApplicablePrerequisite(),
                NotApplicablePrerequisite()
            ),
            new DocumentCacheStatusLifecycleComponent(
                DocumentCacheStatusLifecycleState.Tracking,
                DocumentCacheStatusAvailability.Available,
                message: null
            ),
            new DocumentCacheStatusCacheAheadComponent(
                DocumentCacheStatusCacheAheadState.Clear,
                recoveryRequired: false,
                message: null
            ),
            new DocumentCacheOperationalHealthComponent(
                operationalHealth,
                operationalHealthReason,
                message: null
            ),
            new DocumentCacheCaughtUpComponent(caughtUp, caughtUpReason, message: null),
            new DocumentCacheStatusQueueSummary(
                queuePresence,
                oldestWorkFirstEnqueuedAt: null,
                oldestWorkAgeSeconds: null,
                DocumentCacheStatusBacklogEstimate.Unavailable
            ),
            new DocumentCacheStatusExecutionStateComponent(
                DocumentCacheStatusExecutionState.Idle,
                ProjectionObservedAt,
                activeWorkers: 0,
                concurrencySlotsUsed: 0,
                targetBackoffUntil: null,
                lastSuccessfulWorkAt: null,
                lastFailureAt: null,
                message: null
            ),
            activeCommand: null,
            lastEndedDiagnostic: null,
            new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(),
            new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
            new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
            DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(
                DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
            ),
            enqueueFailures ?? new DocumentCacheStatusEnqueueFailures()
        );

    private static DocumentCacheStatusEnqueueFailures EnqueueFailures(
        params DocumentCacheStatusEnqueueFailureCategory[] categories
    ) =>
        new(
            byCategory:
            [
                .. categories.Select(category => new DocumentCacheStatusEnqueueFailureCategoryCount(
                    category,
                    1
                )),
            ]
        );

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, message: null);

    private static DocumentCacheStatusProviderPrerequisiteComponent NotApplicablePrerequisite() =>
        new(
            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            message: null
        );

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json) };

    private static CdcControlOptions ControlOptions(TimeSpan? statusEndpointTimeout) =>
        new()
        {
            DeploymentKey = CdcControlTemplateTestData.DeploymentKey,
            InstanceKey = CdcControlTemplateTestData.InstanceKey,
            TopicPrefix = CdcControlTemplateTestData.TopicPrefix,
            Generation = CdcControlTemplateTestData.BindingGeneration,
            PartitionCount = 1,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            DmsBaseUrl = DmsBaseUrl,
            DmsBearerToken = BearerToken,
            Timeouts = new() { StatusEndpoint = statusEndpointTimeout ?? TimeSpan.FromSeconds(30) },
        };

    private static CdcObservationContext Context() =>
        new(OperationId, TargetIdentity(), BindingFingerprint());

    private static CdcTargetIdentity TargetIdentity() =>
        CdcControlTemplateTestData.BuildTargetIdentity(Ddl.CdcProvider.Postgresql);

    private static string BindingFingerprint() =>
        CdcControlTemplateTestData.SourceFingerprint(Ddl.CdcProvider.Postgresql).Value;

    private static CdcDiagnostic Diagnostic(CdcProjectionCorrelationObservation observation) =>
        observation.Diagnostics.Should().ContainSingle().Subject;

    private static CdcContractValidationResult Validate(CdcProjectionCorrelationObservation observation) =>
        CdcProjectionCorrelationObservationValidator.Validate(
            observation,
            new(OperationId, TargetIdentity(), BindingFingerprint(), ObservedAt.AddMinutes(1))
        );

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization);

    private sealed class StubHttpMessageHandler(
        Func<RecordedRequest, HttpResponseMessage> respond,
        bool neverAnswers
    ) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RecordedRequest recorded = new(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString()
            );
            Requests.Add(recorded);

            if (neverAnswers)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return respond(recorded);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
