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
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string MixedCaseFingerprint =
        "sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DocumentCacheAdministrativeTargetKey _defaultTargetKey = new(
        tenantKey: "",
        dataStoreId: 1
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(Fingerprint);

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

            root.Select(property => property.Key)
                .Should()
                .Equal("targetKey", "expectedPhysicalSourceFingerprint");
            root["expectedPhysicalSourceFingerprint"]!.GetValue<string>().Should().Be(Fingerprint);

            JsonObject targetKey = root["targetKey"]!.AsObject();
            targetKey.Select(property => property.Key).Should().Equal("tenantKey", "dataStoreId");
            targetKey["tenantKey"]!.GetValue<string>().Should().Be("");
            targetKey["dataStoreId"]!.GetValue<long>().Should().Be(1);
        }

        [Test]
        public void It_should_deserialize_the_default_tenant_key_shape()
        {
            const string json = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                }
                """;

            DocumentCacheOfflineDeactivationRequest request =
                JsonSerializer.Deserialize<DocumentCacheOfflineDeactivationRequest>(json)!;

            request.TargetKey.TargetKey.Should().Be(DocumentCacheTargetKey.Create("", 1));
            request.ExpectedPhysicalSourceFingerprint!.Value.Should().Be(Fingerprint);
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
                  "expectedPhysicalSourceFingerprint": {{serializedFingerprint}}
                }
                """;

            Action act = () => JsonSerializer.Deserialize<DocumentCacheOfflineDeactivationRequest>(json);

            act.Should().Throw<JsonException>();
        }

        [Test]
        public void It_should_reject_nonpositive_data_store_ids()
        {
            const string json = """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 0 },
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
                new DocumentCacheGuardedNewEmptyActivationRequest(_defaultTargetKey, _fingerprint),
                typeof(DocumentCacheGuardedNewEmptyActivationRequest)
            ).SetName("Guarded new-empty activation");
            yield return new TestCaseData(
                new DocumentCacheOfflineReadAccelerationActivationRequest(_defaultTargetKey, _fingerprint),
                typeof(DocumentCacheOfflineReadAccelerationActivationRequest)
            ).SetName("Offline read-acceleration activation");
            yield return new TestCaseData(
                new DocumentCacheOfflineDeactivationRequest(_defaultTargetKey, _fingerprint),
                typeof(DocumentCacheOfflineDeactivationRequest)
            ).SetName("Offline deactivation");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Administrative_Command_Results : DocumentCacheAdministrativeContractsTests
    {
        private static readonly DocumentCacheTargetDiagnosticCategory[] _producedDiagnosticCategories =
        [
            DocumentCacheTargetDiagnosticCategory.TargetNotConfigured,
            DocumentCacheTargetDiagnosticCategory.TargetUnresolved,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataMissing,
            DocumentCacheTargetDiagnosticCategory.ProviderMetadataUnknown,
            DocumentCacheTargetDiagnosticCategory.ProviderMismatch,
            DocumentCacheTargetDiagnosticCategory.ConnectionInputMissing,
            DocumentCacheTargetDiagnosticCategory.PhysicalSourceFingerprintFailure,
            DocumentCacheTargetDiagnosticCategory.InventoryFailure,
            DocumentCacheTargetDiagnosticCategory.EnqueueTriggerFailure,
            DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed,
            DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
            DocumentCacheTargetDiagnosticCategory.LifecycleObservationFailure,
            DocumentCacheTargetDiagnosticCategory.TransientCmsRefreshFailure,
            DocumentCacheTargetDiagnosticCategory.TargetReplaced,
            DocumentCacheTargetDiagnosticCategory.LifecycleMismatch,
            DocumentCacheTargetDiagnosticCategory.ResettingRequiresExplicitOperatorRecovery,
            DocumentCacheTargetDiagnosticCategory.CacheAheadLatchSet,
            DocumentCacheTargetDiagnosticCategory.NonemptyGuardedActivationState,
            DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown,
            DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch,
            DocumentCacheTargetDiagnosticCategory.UnexpectedProviderFailure,
        ];

        [Test]
        public void It_should_serialize_lower_camel_enums_and_observed_state()
        {
            DocumentCacheAdministrativeCommandResult result = new(
                DocumentCacheAdministrativeCommand.OfflineReadAccelerationActivation,
                _defaultTargetKey,
                DocumentCacheAdministrativePreflightClassification.ProviderPrerequisiteFailed,
                observedLifecycle: DocumentCacheLifecycleState.Tracking,
                cacheAheadRecoveryRequired: false,
                physicalSourceFingerprint: _fingerprint,
                targetContextGeneration: 42,
                downstreamPublicationStatus: DocumentCacheDownstreamPublicationStatus.InternalOnly,
                diagnostics:
                [
                    new DocumentCacheAdministrativeDiagnostic(
                        DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident,
                        "Unsupported prerequisite incident."
                    ),
                ],
                noMutationGuarantee: new DocumentCacheAdministrativeNoMutationGuarantee(
                    guaranteed: true,
                    DocumentCacheAdministrativeNoMutationScope.LifecycleCacheWorkLatchAndProviderSettings,
                    "Classifier performed no lifecycle, cache, work, latch, or provider-setting mutation."
                )
            );

            string json = JsonSerializer.Serialize(result);
            JsonObject root = JsonNode.Parse(json)!.AsObject();

            root.Select(property => property.Key)
                .Should()
                .Equal(
                    "command",
                    "targetKey",
                    "classification",
                    "observedLifecycle",
                    "cacheAheadRecoveryRequired",
                    "physicalSourceFingerprint",
                    "targetContextGeneration",
                    "downstreamPublicationStatus",
                    "diagnostics",
                    "noMutationGuarantee"
                );
            root["command"]!.GetValue<string>().Should().Be("offlineReadAccelerationActivation");
            root["classification"]!.GetValue<string>().Should().Be("providerPrerequisiteFailed");
            root["observedLifecycle"]!.GetValue<string>().Should().Be("tracking");
            root["physicalSourceFingerprint"]!.GetValue<string>().Should().Be(Fingerprint);
            root["targetContextGeneration"]!.GetValue<long>().Should().Be(42);
            root["downstreamPublicationStatus"]!.GetValue<string>().Should().Be("internalOnly");
            root["diagnostics"]![0]!["category"]!
                .GetValue<string>()
                .Should()
                .Be("unsupportedPrerequisiteIncident");
            root["noMutationGuarantee"]!["scope"]!
                .GetValue<string>()
                .Should()
                .Be("lifecycleCacheWorkLatchAndProviderSettings");

            JsonObject targetKey = root["targetKey"]!.AsObject();
            targetKey["tenantKey"]!.GetValue<string>().Should().Be("");
            targetKey["dataStoreId"]!.GetValue<long>().Should().Be(1);
        }

        [TestCaseSource(nameof(PreflightClassifications))]
        public void It_should_round_trip_preflight_classifications_as_lower_camel(
            DocumentCacheAdministrativePreflightClassification classification
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
                  "classification": "unsupportedPrerequisiteIncident",
                  "observedLifecycle": "tracking",
                  "cacheAheadRecoveryRequired": true,
                  "physicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "targetContextGeneration": 9,
                  "downstreamPublicationStatus": "unknown",
                  "diagnostics": [
                    {
                      "category": "providerPrerequisiteFailed",
                      "message": "Provider prerequisite failed."
                    }
                  ],
                  "noMutationGuarantee": {
                    "guaranteed": true,
                    "scope": "lifecycleCacheWorkLatchAndProviderSettings",
                    "message": "No mutation was performed."
                  }
                }
                """;

            DocumentCacheAdministrativeCommandResult result =
                JsonSerializer.Deserialize<DocumentCacheAdministrativeCommandResult>(json)!;

            result.Command.Should().Be(DocumentCacheAdministrativeCommand.OfflineDeactivation);
            result
                .Classification.Should()
                .Be(DocumentCacheAdministrativePreflightClassification.UnsupportedPrerequisiteIncident);
            result.ObservedLifecycle.Should().Be(DocumentCacheLifecycleState.Tracking);
            result.CacheAheadRecoveryRequired.Should().BeTrue();
            result.PhysicalSourceFingerprint!.Value.Should().Be(Fingerprint);
            result.TargetContextGeneration.Should().Be(9);
            result.DownstreamPublicationStatus.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
            result.NoMutationGuarantee!.Guaranteed.Should().BeTrue();
        }

        [TestCaseSource(nameof(ProducedDiagnosticCategories))]
        public void It_should_round_trip_produced_diagnostic_categories(
            DocumentCacheTargetDiagnosticCategory category
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
                .BeEquivalentTo(Enum.GetValues<DocumentCacheTargetDiagnosticCategory>());
        }

        [Test]
        public void It_should_reject_numeric_enum_values()
        {
            const string json = """
                {
                  "command": "offlineDeactivation",
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "classification": 5,
                  "observedLifecycle": null,
                  "cacheAheadRecoveryRequired": null,
                  "physicalSourceFingerprint": null,
                  "targetContextGeneration": null,
                  "downstreamPublicationStatus": null,
                  "diagnostics": [],
                  "noMutationGuarantee": null
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
                DocumentCacheAdministrativePreflightClassification.UnexpectedProviderFailure,
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
        }

        private static IEnumerable<TestCaseData> ProducedDiagnosticCategories()
        {
            return _producedDiagnosticCategories.Select(category =>
                new TestCaseData(category).SetName($"Diagnostic category {category}")
            );
        }

        private static IEnumerable<TestCaseData> PreflightClassifications()
        {
            return Enum.GetValues<DocumentCacheAdministrativePreflightClassification>()
                .Select(classification =>
                    new TestCaseData(classification).SetName($"Preflight classification {classification}")
                );
        }

        private static string ToLowerCamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];
    }
}
