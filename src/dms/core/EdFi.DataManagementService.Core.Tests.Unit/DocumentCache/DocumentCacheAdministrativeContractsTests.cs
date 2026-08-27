// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DocumentCacheAdministrativeContracts")]
public class DocumentCacheAdministrativeContractsTests
{
    private const string DefaultNoMutationMessage =
        "Command result performed no lifecycle cache work latch or provider-setting mutation.";

    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string MixedCaseFingerprint =
        "sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DocumentCacheAdministrativeTargetKey _defaultTargetKey = new(
        tenantKey: "",
        dataStoreId: 1
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(Fingerprint);

    private static readonly DocumentCacheOfflineWriterAdmission _offlineActivationAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
    );

    private static readonly DocumentCacheOfflineWriterAdmission _offlineDeactivationAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
    );

    private static readonly DocumentCacheOfflineWriterAdmission _cacheAheadRecoveryAdmission = new(
        confirmed: true,
        DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
    );

    [TestFixture]
    [Parallelizable]
    public class Given_Administrative_Command_Requests : DocumentCacheAdministrativeContractsTests
    {
        [TestCaseSource(nameof(RequestContracts))]
        public void It_should_serialize_nested_target_key_and_expected_fingerprint(
            object request,
            Type requestType
        )
        {
            string json = JsonSerializer.Serialize(request, requestType);
            JsonObject root = JsonNode.Parse(json)!.AsObject();

            bool hasOfflineWriterAdmission = root.ContainsKey("offlineWriterAdmission");
            string[] expectedProperties = hasOfflineWriterAdmission
                ? ["targetKey", "confirmation", "expectedPhysicalSourceFingerprint", "offlineWriterAdmission"]
                : ["targetKey", "confirmation", "expectedPhysicalSourceFingerprint"];
            root.Select(property => property.Key).Should().Equal(expectedProperties);
            root["confirmation"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            root["expectedPhysicalSourceFingerprint"]!.GetValue<string>().Should().Be(Fingerprint);

            JsonObject targetKey = root["targetKey"]!.AsObject();
            targetKey.Select(property => property.Key).Should().Equal("tenantKey", "dataStoreId");
            targetKey["tenantKey"]!.GetValue<string>().Should().Be("");
            targetKey["dataStoreId"]!.GetValue<long>().Should().Be(1);

            if (hasOfflineWriterAdmission)
            {
                root["offlineWriterAdmission"]!
                    .GetValue<string>()
                    .Should()
                    .Be(DocumentCacheOfflineWriterAdmission.ClosedAndDrainedJsonValue);
            }
        }

        [TestCaseSource(nameof(CommandContracts))]
        public void It_should_expose_shared_command_confirmation_and_admission_requirements(
            DocumentCacheAdministrativeCommand command,
            DocumentCacheAdministrativeCommandConfirmation expectedConfirmation,
            DocumentCacheOfflineWriterAdmissionConfirmation? expectedOfflineWriterAdmissionConfirmation
        )
        {
            DocumentCacheAdministrativeCommandContract contract =
                DocumentCacheAdministrativeCommandContracts.Get(command);

            contract.Command.Should().Be(command);
            contract.ExpectedConfirmation.Should().Be(expectedConfirmation);
            contract
                .ExpectedOfflineWriterAdmissionConfirmation.Should()
                .Be(expectedOfflineWriterAdmissionConfirmation);
            contract
                .RequiresOfflineWriterAdmission.Should()
                .Be(expectedOfflineWriterAdmissionConfirmation is not null);
            DocumentCachePreflightClassifier
                .ExpectedCommandConfirmation(command)
                .Should()
                .Be(expectedConfirmation);
        }

        [Test]
        public void It_should_deserialize_the_default_tenant_key_shape()
        {
            const string json = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "offlineDeactivation",
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": "closedAndDrained"
                }
                """;

            DocumentCacheOfflineDeactivationRequest request =
                JsonSerializer.Deserialize<DocumentCacheOfflineDeactivationRequest>(json)!;

            request.TargetKey.TargetKey.Should().Be(DocumentCacheTargetKey.Create("", 1));
            request
                .Confirmation.Should()
                .Be(DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation);
            request.ExpectedPhysicalSourceFingerprint!.Value.Should().Be(Fingerprint);
            request.OfflineWriterAdmission.Should().NotBeNull();
            request
                .OfflineWriterAdmission!.Confirmation.Should()
                .Be(
                    DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
                );
        }

        [TestCase(" ")]
        [TestCase("SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
        [TestCase(MixedCaseFingerprint)]
        [TestCase("sha256:0123456789abcdef")]
        [TestCase("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
        [TestCase(Fingerprint + "\r\n")]
        public void It_should_reject_noncanonical_expected_fingerprints(string fingerprint)
        {
            string serializedFingerprint = JsonSerializer.Serialize(fingerprint);
            string json = $$"""
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "newEmptyActivation",
                  "expectedPhysicalSourceFingerprint": {{serializedFingerprint}}
                }
                """;

            Action act = () =>
                JsonSerializer.Deserialize<DocumentCacheGuardedNewEmptyActivationRequest>(json);

            act.Should().Throw<JsonException>();
        }

        [Test]
        public void It_should_reject_nonpositive_data_store_ids()
        {
            const string json = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 0 },
                  "confirmation": "newEmptyActivation",
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                }
                """;

            Action act = () =>
                JsonSerializer.Deserialize<DocumentCacheGuardedNewEmptyActivationRequest>(json);

            act.Should().Throw<ArgumentException>();
        }

        private static IEnumerable<TestCaseData> RequestContracts()
        {
            yield return new TestCaseData(
                new DocumentCacheGuardedNewEmptyActivationRequest(
                    _defaultTargetKey,
                    _fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation
                ),
                typeof(DocumentCacheGuardedNewEmptyActivationRequest)
            ).SetName("Guarded new-empty activation");
            yield return new TestCaseData(
                new DocumentCacheOfflineActivationRequest(
                    _defaultTargetKey,
                    _offlineActivationAdmission,
                    _fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OfflineActivation
                ),
                typeof(DocumentCacheOfflineActivationRequest)
            ).SetName("Offline activation");
            yield return new TestCaseData(
                new DocumentCacheOfflineDeactivationRequest(
                    _defaultTargetKey,
                    _offlineDeactivationAdmission,
                    _fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation
                ),
                typeof(DocumentCacheOfflineDeactivationRequest)
            ).SetName("Offline deactivation");
            yield return new TestCaseData(
                new DocumentCacheOnlineCacheRebuildRequest(
                    _defaultTargetKey,
                    _fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild
                ),
                typeof(DocumentCacheOnlineCacheRebuildRequest)
            ).SetName("Online cache rebuild");
            yield return new TestCaseData(
                new DocumentCacheExplicitIntegrityScrubRequest(
                    _defaultTargetKey,
                    _fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub
                ),
                typeof(DocumentCacheExplicitIntegrityScrubRequest)
            ).SetName("Explicit integrity scrub");
            yield return new TestCaseData(
                new DocumentCacheInternalOnlyCacheAheadRecoveryRequest(
                    _defaultTargetKey,
                    _cacheAheadRecoveryAdmission,
                    _fingerprint,
                    DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery
                ),
                typeof(DocumentCacheInternalOnlyCacheAheadRecoveryRequest)
            ).SetName("Internal-only cache-ahead recovery");
        }

        private static IEnumerable<TestCaseData> CommandContracts()
        {
            yield return new TestCaseData(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                DocumentCacheAdministrativeCommandConfirmation.NewEmptyActivation,
                null
            ).SetName("Guarded new-empty activation");
            yield return new TestCaseData(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                DocumentCacheAdministrativeCommandConfirmation.OfflineActivation,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            ).SetName("Offline activation");
            yield return new TestCaseData(
                DocumentCacheAdministrativeCommand.OfflineDeactivation,
                DocumentCacheAdministrativeCommandConfirmation.OfflineDeactivation,
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            ).SetName("Offline deactivation");
            yield return new TestCaseData(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild,
                null
            ).SetName("Online cache rebuild");
            yield return new TestCaseData(
                DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub,
                DocumentCacheAdministrativeCommandConfirmation.IntegrityScrub,
                null
            ).SetName("Explicit integrity scrub");
            yield return new TestCaseData(
                DocumentCacheAdministrativeCommand.InternalOnlyCacheAheadRecovery,
                DocumentCacheAdministrativeCommandConfirmation.InternalCacheAheadRecovery,
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
            ).SetName("Internal-only cache-ahead recovery");
        }

        [TestCaseSource(nameof(WriterFencedRequestContracts))]
        public void It_should_deserialize_closed_and_drained_as_the_command_specific_admission(
            string json,
            Type requestType,
            DocumentCacheOfflineWriterAdmissionConfirmation expectedConfirmation
        )
        {
            object request = JsonSerializer.Deserialize(json, requestType)!;

            DocumentCacheOfflineWriterAdmission? admission = request switch
            {
                DocumentCacheOfflineActivationRequest offlineActivationRequest =>
                    offlineActivationRequest.OfflineWriterAdmission,
                DocumentCacheOfflineDeactivationRequest offlineDeactivationRequest =>
                    offlineDeactivationRequest.OfflineWriterAdmission,
                DocumentCacheInternalOnlyCacheAheadRecoveryRequest cacheAheadRecoveryRequest =>
                    cacheAheadRecoveryRequest.OfflineWriterAdmission,
                _ => throw new ArgumentException("Unsupported request type.", nameof(requestType)),
            };

            admission.Should().NotBeNull();
            admission!.Confirmed.Should().BeTrue();
            admission.Confirmation.Should().Be(expectedConfirmation);
            admission.HasUnrecognizedConfirmation.Should().BeFalse();
        }

        [Test]
        public void It_should_represent_missing_offline_admission_for_preflight_classification()
        {
            const string json = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                }
                """;

            DocumentCacheOfflineActivationRequest request =
                JsonSerializer.Deserialize<DocumentCacheOfflineActivationRequest>(json)!;

            request.OfflineWriterAdmission.Should().BeNull();
        }

        [TestCase(
            """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": false
                }
                """
        )]
        [TestCase(
            """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": "ClosedAndDrained"
                }
                """
        )]
        [TestCase(
            """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": {
                    "confirmed": false,
                    "confirmation": "offlineActivationWritersClosedAndDrained"
                  }
                }
                """
        )]
        public void It_should_reject_non_token_offline_admission_json_contracts(string json)
        {
            Action act = () => JsonSerializer.Deserialize<DocumentCacheOfflineActivationRequest>(json);

            act.Should().Throw<JsonException>();
        }

        private static IEnumerable<TestCaseData> WriterFencedRequestContracts()
        {
            const string offlineActivationJson = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "offlineActivation",
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": "closedAndDrained"
                }
                """;

            const string offlineDeactivationJson = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "offlineDeactivation",
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": "closedAndDrained"
                }
                """;

            const string cacheAheadRecoveryJson = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "internalCacheAheadRecovery",
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "offlineWriterAdmission": "closedAndDrained"
                }
                """;

            yield return new TestCaseData(
                offlineActivationJson,
                typeof(DocumentCacheOfflineActivationRequest),
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained
            ).SetName("Offline activation");
            yield return new TestCaseData(
                offlineDeactivationJson,
                typeof(DocumentCacheOfflineDeactivationRequest),
                DocumentCacheOfflineWriterAdmissionConfirmation.OfflineDeactivationWritersClosedAndDrained
            ).SetName("Offline deactivation");
            yield return new TestCaseData(
                cacheAheadRecoveryJson,
                typeof(DocumentCacheInternalOnlyCacheAheadRecoveryRequest),
                DocumentCacheOfflineWriterAdmissionConfirmation.InternalOnlyCacheAheadRecoveryWritersClosedAndDrained
            ).SetName("Cache-ahead recovery");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Administrative_Command_Results : DocumentCacheAdministrativeContractsTests
    {
        private static readonly DocumentCacheAdministrativeDiagnosticCategory[] _producedDiagnosticCategories =
            Enum.GetValues<DocumentCacheAdministrativeDiagnosticCategory>();

        [Test]
        public void It_should_serialize_lower_camel_enums_and_observed_state()
        {
            DocumentCacheAdministrativeCommandResult result = new(
                DocumentCacheAdministrativeCommand.OfflineActivation,
                _defaultTargetKey,
                DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                DocumentCacheAdministrativeCommandClassification.WorkflowTimeout,
                mutated: true,
                targetGeneration: 42,
                physicalSourceFingerprint: _fingerprint,
                lifecycle: DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                phaseDiagnostics:
                [
                    new DocumentCacheAdministrativePhaseDiagnostic(
                        DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                        DocumentCacheAdministrativeCommandPhase.CaptureBoundary,
                        retryable: true,
                        DocumentCacheAdministrativeDiagnosticCategory.WorkflowTimeout,
                        affectedDocumentIds: [101, 102],
                        "Workflow timeout expired during baseline seeding."
                    ),
                ],
                offlineWriterAdmission: DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained,
                elapsedCommandTime: TimeSpan.FromMinutes(3)
            );

            string json = JsonSerializer.Serialize(result);
            JsonObject root = JsonNode.Parse(json)!.AsObject();

            root.Select(property => property.Key)
                .Should()
                .Equal(
                    "command",
                    "targetKey",
                    "status",
                    "classification",
                    "mutated",
                    "targetGeneration",
                    "physicalSourceFingerprint",
                    "lifecycle",
                    "cacheAheadRecoveryRequired",
                    "phaseDiagnostics",
                    "offlineWriterAdmission",
                    "elapsedCommandTimeSeconds"
                );
            root["command"]!.GetValue<string>().Should().Be("offlineActivation");
            root["status"]!.GetValue<string>().Should().Be("incompleteRetryable");
            root["classification"]!.GetValue<string>().Should().Be("workflowTimeout");
            root["mutated"]!.GetValue<bool>().Should().BeTrue();
            root["targetGeneration"]!.GetValue<long>().Should().Be(42);
            root["physicalSourceFingerprint"]!.GetValue<string>().Should().Be(Fingerprint);
            root["lifecycle"]!.GetValue<string>().Should().Be("tracking");
            root["phaseDiagnostics"]![0]!["currentPhase"]!.GetValue<string>().Should().Be("seedBaseline");
            root["phaseDiagnostics"]![0]!["lastCompletedPhase"]!
                .GetValue<string>()
                .Should()
                .Be("captureBoundary");
            root["phaseDiagnostics"]![0]!["retryable"]!.GetValue<bool>().Should().BeTrue();
            root["phaseDiagnostics"]![0]!["diagnosticCategory"]!
                .GetValue<string>()
                .Should()
                .Be("workflowTimeout");
            root["phaseDiagnostics"]![0]!["affectedDocumentIds"]!
                .AsArray()
                .Select(node => node!.GetValue<long>())
                .Should()
                .Equal(101, 102);
            root["offlineWriterAdmission"]!
                .GetValue<string>()
                .Should()
                .Be("offlineActivationWritersClosedAndDrained");
            root.Should().NotContainKey("elapsedCommandTime");
            root["elapsedCommandTimeSeconds"]!.GetValue<double>().Should().Be(180);

            DocumentCacheAdministrativeCommandResult deserialized =
                JsonSerializer.Deserialize<DocumentCacheAdministrativeCommandResult>(json)!;
            deserialized.ElapsedCommandTime.Should().Be(TimeSpan.FromMinutes(3));

            JsonObject targetKey = root["targetKey"]!.AsObject();
            targetKey["tenantKey"]!.GetValue<string>().Should().Be("");
            targetKey["dataStoreId"]!.GetValue<long>().Should().Be(1);
        }

        [TestCaseSource(nameof(PreflightClassifications))]
        public void It_should_round_trip_preflight_classifications_as_lower_camel(
            DocumentCacheAdministrativeCommandClassification classification
        )
        {
            DocumentCacheAdministrativeCommandResult result = new(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                _defaultTargetKey,
                classification
            );

            string json = JsonSerializer.Serialize(result);
            DocumentCacheAdministrativeCommandResult deserialized =
                JsonSerializer.Deserialize<DocumentCacheAdministrativeCommandResult>(json)!;

            deserialized.Classification.Should().Be(classification);
            JsonNode.Parse(json)!["classification"]!
                .GetValue<string>()
                .Should()
                .Be(ToLowerCamelCase(classification.ToString()));
        }

        [Test]
        public void It_should_deserialize_rejection_classifications()
        {
            const string json = """
                {
                  "command": "offlineDeactivation",
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "status": "rejectedNoMutation",
                  "classification": "unsupportedPrerequisiteIncident",
                  "mutated": false,
                  "targetGeneration": 9,
                  "physicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "lifecycle": "tracking",
                  "cacheAheadRecoveryRequired": true,
                  "phaseDiagnostics": [
                    {
                      "currentPhase": "preflight",
                      "lastCompletedPhase": "resolveTarget",
                      "retryable": false,
                      "diagnosticCategory": "providerPrerequisiteFailed",
                      "affectedDocumentIds": [],
                      "message": "Provider prerequisite failed."
                    }
                  ]
                }
                """;

            DocumentCacheAdministrativeCommandResult result =
                JsonSerializer.Deserialize<DocumentCacheAdministrativeCommandResult>(json)!;

            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineDeactivation);
            result.Status.Should().Be(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation);
            result
                .Classification.Should()
                .Be(DocumentCacheAdministrativeCommandClassification.UnsupportedPrerequisiteIncident);
            result.Mutated.Should().BeFalse();
            result.Lifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
            result.CacheAheadRecoveryRequired.Should().BeTrue();
            result.PhysicalSourceFingerprint!.Value.Should().Be(Fingerprint);
            result.TargetGeneration.Should().Be(9);
            result
                .PhaseDiagnostics.Should()
                .ContainSingle()
                .Which.DiagnosticCategory.Should()
                .Be(DocumentCacheAdministrativeDiagnosticCategory.ProviderPrerequisiteFailed);
            result.NoMutationGuarantee!.Guaranteed.Should().BeTrue();
        }

        [TestCase(DocumentCacheAdministrativeCommandStatus.RejectedNoMutation)]
        [TestCase(DocumentCacheAdministrativeCommandStatus.FailedNoMutation)]
        public void It_should_compute_default_no_mutation_guarantee_when_none_is_supplied(
            DocumentCacheAdministrativeCommandStatus status
        )
        {
            DocumentCacheAdministrativeCommandResult result = new(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                _defaultTargetKey,
                status,
                DocumentCacheAdministrativeCommandClassification.LifecycleMismatch,
                mutated: false
            );

            result.NoMutationGuarantee.Should().NotBeNull();
            result.NoMutationGuarantee!.Guaranteed.Should().BeTrue();
            result.NoMutationGuarantee.Message.Should().Be(DefaultNoMutationMessage);
        }

        [Test]
        public void It_should_not_return_no_mutation_guarantee_for_mutated_results()
        {
            DocumentCacheAdministrativeCommandResult result = new(
                DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                _defaultTargetKey,
                DocumentCacheAdministrativeCommandStatus.IncompleteRetryable,
                DocumentCacheAdministrativeCommandClassification.CancellationAfterMutation,
                mutated: true
            );

            result.NoMutationGuarantee.Should().BeNull();
        }

        [TestCaseSource(nameof(ProducedDiagnosticCategories))]
        public void It_should_round_trip_produced_diagnostic_categories(
            DocumentCacheAdministrativeDiagnosticCategory category
        )
        {
            DocumentCacheAdministrativeDiagnostic diagnostic = new(category, "Diagnostic message.");

            string json = JsonSerializer.Serialize(diagnostic);
            DocumentCacheAdministrativeDiagnostic deserialized =
                JsonSerializer.Deserialize<DocumentCacheAdministrativeDiagnostic>(json)!;

            deserialized.Category.Should().Be(category);
            JsonNode.Parse(json)!["category"]!
                .GetValue<string>()
                .Should()
                .Be(ToLowerCamelCase(category.ToString()));
        }

        [Test]
        public void It_should_cover_every_produced_diagnostic_category()
        {
            _producedDiagnosticCategories
                .Should()
                .BeEquivalentTo(Enum.GetValues<DocumentCacheAdministrativeDiagnosticCategory>());
        }

        [Test]
        public void It_should_reject_numeric_enum_values()
        {
            const string json = """
                {
                  "command": "offlineDeactivation",
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "status": "rejectedNoMutation",
                  "classification": 5,
                  "mutated": false,
                  "targetGeneration": null,
                  "physicalSourceFingerprint": null,
                  "lifecycle": null,
                  "cacheAheadRecoveryRequired": null,
                  "phaseDiagnostics": []
                }
                """;

            Action act = () => JsonSerializer.Deserialize<DocumentCacheAdministrativeCommandResult>(json);

            act.Should().Throw<JsonException>();
        }

        [Test]
        public void It_should_sanitize_bounded_diagnostic_and_no_mutation_messages()
        {
            string unsafeMessage = new string('x', 600) + "\r\n";

            DocumentCacheAdministrativeCommandResult result = new(
                DocumentCacheAdministrativeCommand.GuardedNewEmptyActivation,
                _defaultTargetKey,
                DocumentCacheAdministrativeCommandClassification.UnexpectedProviderFailure,
                diagnostics:
                [
                    new DocumentCacheAdministrativeDiagnostic(
                        DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
                        unsafeMessage
                    ),
                ],
                noMutationGuarantee: new DocumentCacheAdministrativeNoMutationGuarantee(
                    guaranteed: true,
                    DocumentCacheAdministrativeNoMutationScope.LifecycleCacheWorkLatchAndProviderSettings,
                    unsafeMessage
                )
            );

            result.Diagnostics.Single().Message.Should().HaveLength(512).And.NotContain("\r\n");
            result.NoMutationGuarantee!.Message.Should().HaveLength(512).And.NotContain("\r\n");
            result.NoMutationGuarantee.Message.Should().NotBe(DefaultNoMutationMessage);
        }

        [Test]
        public void It_should_reject_nonpositive_affected_document_ids()
        {
            Action act = () =>
                _ = new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.DrainWork,
                    DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                    retryable: true,
                    DocumentCacheAdministrativeDiagnosticCategory.PersistentPoison,
                    affectedDocumentIds: [0],
                    "Invalid document id."
                );

            act.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void It_should_pin_the_design_phase_names()
        {
            Enum.GetValues<DocumentCacheAdministrativeCommandPhase>()
                .Select(phase => JsonSerializer.Serialize(phase).Trim('"'))
                .Should()
                .Equal(
                    "resolveTarget",
                    "acquireMutex",
                    "preflight",
                    "enterResetting",
                    "clearCache",
                    "clearWork",
                    "enterRebuilding",
                    "captureBoundary",
                    "seedBaseline",
                    "drainWork",
                    "enterTracking",
                    "enterDisabled",
                    "scrubScan",
                    "setCacheAheadLatch",
                    "complete"
                );
        }

        private static IEnumerable<TestCaseData> ProducedDiagnosticCategories()
        {
            return _producedDiagnosticCategories.Select(category =>
                new TestCaseData(category).SetName($"Diagnostic category {category}")
            );
        }

        private static IEnumerable<TestCaseData> PreflightClassifications()
        {
            return Enum.GetValues<DocumentCacheAdministrativeCommandClassification>()
                .Select(classification =>
                    new TestCaseData(classification).SetName($"Preflight classification {classification}")
                );
        }

        private static string ToLowerCamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    }
}
