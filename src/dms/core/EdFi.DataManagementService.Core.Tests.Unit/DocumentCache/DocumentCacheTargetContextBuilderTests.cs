// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheTargetContext")]
public class DocumentCacheTargetContextBuilderTests
{
    private const string TargetInput = "Server=hidden;Database=hidden;Password=hidden;";

    private const string SensitiveProviderFailure =
        "Server=prod-db.example.com;Database=StudentRecords;Password=Secret123;Host=ProdHost;";

    private static readonly DocumentCacheTargetKey _targetKey = DocumentCacheTargetKey.Create("TenantA", 7);

    private static readonly DocumentCacheTargetContextGeneration _generation = new(3);

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCacheLifecycleObservation _trackingLifecycle = new(
        DocumentCacheLifecycleState.Tracking,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheLifecycleObservation _disabledLifecycle = new(
        DocumentCacheLifecycleState.Disabled,
        CacheAheadRecoveryRequired: false
    );

    private static readonly DocumentCacheProviderInventoryValidationResult _satisfiedInventory = new(
        new DocumentCacheInventoryValidationResult(
            DocumentCacheInventoryStatus.Satisfied,
            "Inventory satisfied."
        ),
        new DocumentCacheEnqueueTriggerValidationResult(
            DocumentCacheEnqueueTriggerStatus.Satisfied,
            "Enqueue trigger satisfied."
        )
    );

    private static readonly DocumentCacheProviderPrerequisiteValidationResult _satisfiedPrerequisites =
        DocumentCacheProviderPrerequisiteValidationResult.Initialization(
            DocumentCacheSqlServerPrerequisiteDetails.NotApplicable(),
            _trackingLifecycle
        );

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resolved_Eligible_Target : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public async Task It_builds_a_process_local_execution_context_with_effective_settings()
        {
            BuilderFixture fixture = new();

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result.HasExecutionContext.Should().BeTrue();
            result.Observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            result.Observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Eligible);
            result.Observation.Diagnostics.Should().BeEmpty();

            DocumentCacheTargetExecutionContext context = result.ExecutionContext!;
            context.TargetKey.Should().Be(_targetKey);
            context.Generation.Should().Be(_generation);
            context.DataStore.Id.Should().Be(7);
            context.DataStore.DataStoreType.Should().Be("Operational");
            context.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
            context.ConnectionInput.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
            context.ConnectionInput.Value.Should().Be(TargetInput);
            context.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            context.Lifecycle.Should().Be(_trackingLifecycle);
            context.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
            context.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Satisfied);
            context
                .SqlServerPrerequisites!.ReadCommittedSnapshot.Status.Should()
                .Be(DocumentCacheProviderPrerequisiteStatus.NotApplicable);
            context.EffectiveSettings.ReadAccelerationEnabled.Should().BeTrue();
            context.EffectiveSettings.ProjectorPageSize.Should().Be(25);

            A.CallTo(() =>
                    fixture.FingerprintReader.ReadFingerprintAsync(TargetInput, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() => fixture.LifecycleReader.ReadLifecycleAsync(TargetInput, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    fixture.InventoryValidator.ValidateInventoryAsync(TargetInput, A<CancellationToken>._)
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    fixture.PrerequisiteValidator.ValidateInitializationAsync(
                        TargetInput,
                        _trackingLifecycle,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Provider_Metadata_Failures : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public async Task It_marks_missing_provider_metadata_as_resolved_but_ineligible()
        {
            BuilderFixture fixture = new(
                DataStore: CreateDataStore(
                    relationalProviderToken: null,
                    relationalProviderMetadataStatus: RelationalProviderMetadataStatus.Missing
                )
            );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result.HasExecutionContext.Should().BeFalse();
            result.Observation.ResolutionState.Should().Be(DocumentCacheTargetResolutionState.Resolved);
            result.Observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            result.Observation.Generation.Should().Be(_generation);
            result
                .Observation.Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing);
            result.Observation.Diagnostics[0].Message.Should().NotContain("hidden");
            AssertProviderAdaptersWereNotCalled(fixture);
        }

        [Test]
        public async Task It_marks_unknown_provider_metadata_as_resolved_but_ineligible()
        {
            BuilderFixture fixture = new(
                DataStore: CreateDataStore(
                    relationalProviderToken: null,
                    relationalProviderMetadataStatus: RelationalProviderMetadataStatus.Unknown
                )
            );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result
                .Observation.Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown);
            AssertProviderAdaptersWereNotCalled(fixture);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Provider_Mismatch : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public async Task It_marks_the_target_ineligible_without_using_target_connection_input()
        {
            BuilderFixture fixture = new(
                DataStore: CreateDataStore(
                    relationalProviderToken: RelationalProviderToken.SqlServer,
                    relationalProviderMetadataStatus: RelationalProviderMetadataStatus.Supported
                )
            );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result.Observation.ProviderToken.Should().Be(RelationalProviderToken.SqlServer);
            result
                .Observation.Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderMismatch);
            AssertProviderAdaptersWereNotCalled(fixture);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Blank_Target_Connection_Input : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public async Task It_marks_the_target_ineligible_with_sanitized_diagnostics()
        {
            BuilderFixture fixture = new(DataStore: CreateDataStore(connectionString: "   "));

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result
                .Observation.Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing);
            result.Observation.Diagnostics[0].Message.Should().NotContain("Server").And.NotContain("hidden");
            AssertProviderAdaptersWereNotCalled(fixture);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Target_Context_Provider_Observations : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public async Task It_combines_fingerprint_inventory_enqueue_and_prerequisite_failures()
        {
            DocumentCacheProviderPrerequisiteValidationResult prerequisiteFailure =
                DocumentCacheProviderPrerequisiteValidationResult.Initialization(
                    new DocumentCacheSqlServerPrerequisiteDetails(
                        new DocumentCacheProviderPrerequisiteResult(
                            DocumentCacheProviderPrerequisiteName.ReadCommittedSnapshot,
                            DocumentCacheProviderPrerequisiteStatus.Disabled,
                            "RCSI disabled."
                        ),
                        new DocumentCacheProviderPrerequisiteResult(
                            DocumentCacheProviderPrerequisiteName.NestedTriggers,
                            DocumentCacheProviderPrerequisiteStatus.Satisfied,
                            "Nested triggers satisfied."
                        )
                    ),
                    _disabledLifecycle
                );
            BuilderFixture fixture = new(
                FingerprintResult: DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                    DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed,
                    "Malformed fingerprint input."
                ),
                LifecycleResult: DocumentCacheLifecycleReadResult.Success(_disabledLifecycle),
                InventoryResult: new DocumentCacheProviderInventoryValidationResult(
                    new DocumentCacheInventoryValidationResult(
                        DocumentCacheInventoryStatus.Invalid,
                        "Inventory invalid."
                    ),
                    new DocumentCacheEnqueueTriggerValidationResult(
                        DocumentCacheEnqueueTriggerStatus.Disabled,
                        "Enqueue trigger disabled."
                    )
                ),
                PrerequisiteResult: prerequisiteFailure
            );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result.HasExecutionContext.Should().BeFalse();
            result.Observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            result.Observation.Lifecycle.Should().Be(_disabledLifecycle);
            result.Observation.Inventory!.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
            result.Observation.EnqueueTrigger!.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Disabled);
            result
                .Observation.Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .BeEquivalentTo([
                    DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                    DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
                    DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
                ]);
        }

        [TestCase(
            DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentityMissing,
            DocumentCacheInventoryStatus.Missing
        )]
        [TestCase(
            DocumentCachePhysicalSourceFingerprintReadStatus.DataStoreIdentitySingletonMissing,
            DocumentCacheInventoryStatus.Missing
        )]
        [TestCase(
            DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityMalformed,
            DocumentCacheInventoryStatus.Invalid
        )]
        [TestCase(
            DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityAllZero,
            DocumentCacheInventoryStatus.Invalid
        )]
        [TestCase(
            DocumentCachePhysicalSourceFingerprintReadStatus.SourceIdentityUnreadable,
            DocumentCacheInventoryStatus.Unreadable
        )]
        public async Task It_surfaces_source_identity_read_failures_through_inventory_observation(
            DocumentCachePhysicalSourceFingerprintReadStatus fingerprintStatus,
            DocumentCacheInventoryStatus expectedInventoryStatus
        )
        {
            BuilderFixture fixture = new(
                FingerprintResult: DocumentCachePhysicalSourceFingerprintReadResult.Failure(
                    fingerprintStatus,
                    "Source identity inventory failure."
                )
            );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result.HasExecutionContext.Should().BeFalse();
            result.Observation.EligibilityState.Should().Be(DocumentCacheTargetEligibilityState.Ineligible);
            result.Observation.PhysicalSourceFingerprint.Should().BeNull();
            result.Observation.Inventory!.Status.Should().Be(expectedInventoryStatus);
            result.Observation.Inventory.Message.Should().Be("Source identity inventory failure.");
            result
                .Observation.EnqueueTrigger!.Status.Should()
                .Be(DocumentCacheEnqueueTriggerStatus.Satisfied);

            result
                .Observation.Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .BeEquivalentTo([
                    DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure,
                ]);

            DocumentCacheTargetDiagnostic inventoryDiagnostic = result.Observation.Diagnostics.Single(
                diagnostic => diagnostic.Category == DocumentCacheTargetDiagnosticCategory.InventoryFailure
            );
            inventoryDiagnostic.Inventory!.Status.Should().Be(expectedInventoryStatus);
            inventoryDiagnostic.Message.Should().Be("Source identity inventory failure.");
        }

        [Test]
        public async Task It_marks_lifecycle_read_failures_ineligible_without_running_prerequisites()
        {
            BuilderFixture fixture = new(
                LifecycleResult: DocumentCacheLifecycleReadResult.Failure(
                    DocumentCacheLifecycleReadStatus.Invalid,
                    "Lifecycle invalid."
                )
            );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            result.HasExecutionContext.Should().BeFalse();
            result.Observation.Lifecycle.Should().BeNull();
            result
                .Observation.Diagnostics.Should()
                .Contain(diagnostic =>
                    diagnostic.Category == DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure
                );
            result.Observation.SqlServerPrerequisites.Should().BeNull();
            A.CallTo(() =>
                    fixture.PrerequisiteValidator.ValidateInitializationAsync(
                        A<string>._,
                        A<DocumentCacheLifecycleObservation>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Provider_Operation_Logging : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public async Task It_logs_sanitized_failure_categories_without_raw_exceptions_for_context_observation_failures()
        {
            RecordingLogger<DocumentCacheTargetContextBuilder> logger = new();
            BuilderFixture fixture = new(Logger: logger);
            InvalidOperationException sensitiveException = SensitiveException();
            A.CallTo(() =>
                    fixture.FingerprintReader.ReadFingerprintAsync(TargetInput, A<CancellationToken>._)
                )
                .Returns(
                    Task.FromException<DocumentCachePhysicalSourceFingerprintReadResult>(sensitiveException)
                );
            A.CallTo(() => fixture.LifecycleReader.ReadLifecycleAsync(TargetInput, A<CancellationToken>._))
                .Returns(Task.FromException<DocumentCacheLifecycleReadResult>(sensitiveException));
            A.CallTo(() =>
                    fixture.InventoryValidator.ValidateInventoryAsync(TargetInput, A<CancellationToken>._)
                )
                .Returns(
                    Task.FromException<DocumentCacheProviderInventoryValidationResult>(sensitiveException)
                );

            await fixture.Builder.BuildAsync(_targetKey, fixture.ResolvedDataStore, _generation);

            logger.Records.Should().HaveCount(3);
            logger
                .Records.Select(record => record.Properties["FailureCategory"])
                .Should()
                .Equal(
                    DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
                    DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
                    DocumentCacheTargetDiagnosticCategory.InventoryFailure
                );
            AssertProviderFailureLogsAreSanitized(logger.Records);
        }

        [Test]
        public async Task It_logs_sanitized_failure_categories_without_raw_exceptions_for_prerequisite_failures()
        {
            RecordingLogger<DocumentCacheTargetContextBuilder> logger = new();
            BuilderFixture fixture = new(
                LifecycleResult: DocumentCacheLifecycleReadResult.Success(_disabledLifecycle),
                Logger: logger
            );
            A.CallTo(() =>
                    fixture.PrerequisiteValidator.ValidateInitializationAsync(
                        TargetInput,
                        _disabledLifecycle,
                        A<CancellationToken>._
                    )
                )
                .Returns(
                    Task.FromException<DocumentCacheProviderPrerequisiteValidationResult>(
                        SensitiveException()
                    )
                );

            DocumentCacheTargetContextBuildResult result = await fixture.Builder.BuildAsync(
                _targetKey,
                fixture.ResolvedDataStore,
                _generation
            );

            logger.Records.Should().ContainSingle();
            logger
                .Records.Single()
                .Properties["FailureCategory"]
                .Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
            result
                .Observation.Diagnostics.Select(diagnostic => diagnostic.Category)
                .Should()
                .Contain(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
            AssertProviderFailureLogsAreSanitized(logger.Records);
        }

        private static InvalidOperationException SensitiveException() => new(SensitiveProviderFailure);

        private static void AssertProviderFailureLogsAreSanitized(IReadOnlyList<LogRecord> records)
        {
            foreach (LogRecord record in records)
            {
                record.Level.Should().Be(LogLevel.Debug);
                record.Exception.Should().BeNull();
                record.Properties["ExceptionType"].Should().Be(nameof(InvalidOperationException));
            }

            string renderedLogText = string.Join(
                "\n",
                records
                    .Select(record => record.Message)
                    .Concat(
                        records.SelectMany(record =>
                            record.Properties.Values.Select(value => value?.ToString() ?? string.Empty)
                        )
                    )
            );
            renderedLogText.Should().NotContain("prod-db.example.com");
            renderedLogText.Should().NotContain("StudentRecords");
            renderedLogText.Should().NotContain("Secret123");
            renderedLogText.Should().NotContain("ProdHost");
            renderedLogText.Should().NotContain("Password");
            renderedLogText.Should().NotContain("Server=");
            renderedLogText.Should().NotContain("Database=");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Target_Context_Builder_Contracts : DocumentCacheTargetContextBuilderTests
    {
        [Test]
        public void It_normalizes_mssql_process_datastore_to_the_sqlserver_provider_token()
        {
            bool created = DocumentCacheProcessProviderToken.TryCreate(
                "mssql",
                out DocumentCacheProcessProviderToken? providerToken
            );

            created.Should().BeTrue();
            providerToken!.ProviderToken.Should().Be(RelationalProviderToken.SqlServer);
        }

        [Test]
        public void It_does_not_depend_on_request_scoped_data_store_selection()
        {
            Type requestScopedSelectionContract = typeof(DataStoreSelection)
                .GetInterfaces()
                .Single(interfaceType => interfaceType.Name == "I" + nameof(DataStoreSelection));

            typeof(DocumentCacheTargetContextBuilder)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Should()
                .NotContain(requestScopedSelectionContract);
        }

        [Test]
        public void It_does_not_re_read_the_data_store_provider()
        {
            typeof(DocumentCacheTargetContextBuilder)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Should()
                .NotContain(typeof(IDataStoreProvider));
        }
    }

    private static void AssertProviderAdaptersWereNotCalled(BuilderFixture fixture)
    {
        A.CallTo(() => fixture.FingerprintReader.ReadFingerprintAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => fixture.LifecycleReader.ReadLifecycleAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => fixture.InventoryValidator.ValidateInventoryAsync(A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() =>
                fixture.PrerequisiteValidator.ValidateInitializationAsync(
                    A<string>._,
                    A<DocumentCacheLifecycleObservation>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private static DataStore CreateDataStore(
        string? connectionString = TargetInput,
        RelationalProviderToken? relationalProviderToken = null,
        RelationalProviderMetadataStatus relationalProviderMetadataStatus =
            RelationalProviderMetadataStatus.Supported
    )
    {
        RelationalProviderToken? effectiveProviderToken =
            relationalProviderMetadataStatus == RelationalProviderMetadataStatus.Supported
                ? relationalProviderToken ?? RelationalProviderToken.Postgresql
                : relationalProviderToken;

        return new(
            7,
            "Operational",
            "Display name must not leak",
            connectionString,
            [],
            effectiveProviderToken,
            relationalProviderMetadataStatus
        );
    }

    private sealed class BuilderFixture
    {
        public IDocumentCachePhysicalSourceFingerprintReader FingerprintReader { get; } =
            A.Fake<IDocumentCachePhysicalSourceFingerprintReader>();

        public IDocumentCacheLifecycleReader LifecycleReader { get; } =
            A.Fake<IDocumentCacheLifecycleReader>();

        public IDocumentCacheInventoryValidator InventoryValidator { get; } =
            A.Fake<IDocumentCacheInventoryValidator>();

        public IDocumentCacheProviderPrerequisiteValidator PrerequisiteValidator { get; } =
            A.Fake<IDocumentCacheProviderPrerequisiteValidator>();

        public DocumentCacheResolvedTargetDataStore ResolvedDataStore { get; }

        public DocumentCacheTargetContextBuilder Builder { get; }

        public BuilderFixture(
            DataStore? DataStore = null,
            RelationalProviderToken? ProcessProviderToken = null,
            RelationalProviderToken? AdapterProviderToken = null,
            DocumentCachePhysicalSourceFingerprintReadResult? FingerprintResult = null,
            DocumentCacheLifecycleReadResult? LifecycleResult = null,
            DocumentCacheProviderInventoryValidationResult? InventoryResult = null,
            DocumentCacheProviderPrerequisiteValidationResult? PrerequisiteResult = null,
            ILogger<DocumentCacheTargetContextBuilder>? Logger = null
        )
        {
            ResolvedDataStore = DocumentCacheResolvedTargetDataStore.From(DataStore ?? CreateDataStore());
            RelationalProviderToken adapterToken = AdapterProviderToken ?? RelationalProviderToken.Postgresql;

            A.CallTo(() => FingerprintReader.ProviderToken).Returns(adapterToken);
            A.CallTo(() => LifecycleReader.ProviderToken).Returns(adapterToken);
            A.CallTo(() => InventoryValidator.ProviderToken).Returns(adapterToken);
            A.CallTo(() => PrerequisiteValidator.ProviderToken).Returns(adapterToken);
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(A<string>._, A<CancellationToken>._))
                .Returns(
                    Task.FromResult(
                        FingerprintResult
                            ?? DocumentCachePhysicalSourceFingerprintReadResult.Success(_fingerprint)
                    )
                );
            A.CallTo(() => LifecycleReader.ReadLifecycleAsync(A<string>._, A<CancellationToken>._))
                .Returns(
                    Task.FromResult(
                        LifecycleResult ?? DocumentCacheLifecycleReadResult.Success(_trackingLifecycle)
                    )
                );
            A.CallTo(() => InventoryValidator.ValidateInventoryAsync(A<string>._, A<CancellationToken>._))
                .Returns(Task.FromResult(InventoryResult ?? _satisfiedInventory));
            A.CallTo(() =>
                    PrerequisiteValidator.ValidateInitializationAsync(
                        A<string>._,
                        A<DocumentCacheLifecycleObservation>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(Task.FromResult(PrerequisiteResult ?? _satisfiedPrerequisites));

            DocumentCacheOptions options = new()
            {
                ReadAcceleration = new DocumentCacheReadAccelerationOptions { Enabled = true },
                Projector = new DocumentCacheProjectorOptions { PageSize = 25 },
            };

            Builder = new DocumentCacheTargetContextBuilder(
                Options.Create(options),
                new DocumentCacheProcessProviderToken(
                    ProcessProviderToken ?? RelationalProviderToken.Postgresql
                ),
                FingerprintReader,
                LifecycleReader,
                InventoryValidator,
                PrerequisiteValidator,
                Logger ?? NullLogger<DocumentCacheTargetContextBuilder>.Instance
            );
        }
    }
}
