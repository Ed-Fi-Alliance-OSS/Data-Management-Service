// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache;

[TestFixture]
[Parallelizable]
[Category("DownstreamPublicationHistory")]
public class DocumentCacheDownstreamPublicationHistoryTests
{
    private static readonly DateTimeOffset _observedAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly DocumentCacheTargetKey _targetKey = DocumentCacheTargetKey.Create("TenantA", 7);

    private static readonly DocumentCachePhysicalSourceFingerprint _fingerprint = new(
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    );

    private static readonly DocumentCachePhysicalSourceFingerprint _otherFingerprint = new(
        "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
    );

    [TestFixture]
    [Parallelizable]
    public class Given_Downstream_Publication_History_Observations
        : DocumentCacheDownstreamPublicationHistoryTests
    {
        [Test]
        public void It_should_bind_observations_to_the_target_fingerprint_status_evidence_and_time()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                targetKey: DocumentCacheTargetKey.Create("tenanta", 7),
                fingerprint: _fingerprint,
                evidenceSource: "fake-binding-store",
                evidenceGenerationIdentifier: "binding-generation-3"
            );

            observation.TargetKey.Should().Be(_targetKey);
            observation.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
            observation.EvidenceSource.Should().Be("fake-binding-store");
            observation.EvidenceGenerationIdentifier.Should().Be("binding-generation-3");
            observation.ObservedAt.Should().Be(_observedAt);
            observation.DiagnosticText.Should().Be("Internal-only downstream observation.");
        }

        [Test]
        public void It_should_require_an_evidence_source_or_generation_identifier()
        {
            Action act = () =>
                _ = Observation(
                    DocumentCacheDownstreamPublicationStatus.Unknown,
                    evidenceSource: null,
                    evidenceGenerationIdentifier: null
                );

            act.Should().Throw<ArgumentException>();
        }

        [Test]
        public void It_should_sanitize_and_bound_evidence_and_diagnostic_text()
        {
            string unsafeText = new string('x', 600) + "\r\n";

            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.Possible,
                evidenceSource: "source\r\n",
                evidenceGenerationIdentifier: "generation\r\n",
                diagnosticText: unsafeText
            );

            observation.EvidenceSource.Should().NotContain("\r").And.NotContain("\n");
            observation.EvidenceGenerationIdentifier.Should().NotContain("\r").And.NotContain("\n");
            observation.DiagnosticText.Should().HaveLength(512).And.NotContain("\r\n");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Default_Production_Provider : DocumentCacheDownstreamPublicationHistoryTests
    {
        [Test]
        public async Task It_should_return_unknown_until_durable_binding_state_exists()
        {
            FakeTimeProvider timeProvider = new(_observedAt);
            DocumentCacheUnknownDownstreamPublicationHistoryProvider provider = new(timeProvider);

            DocumentCacheDownstreamPublicationHistoryObservation observation = await provider.ObserveAsync(
                _targetKey,
                _fingerprint
            );

            observation.TargetKey.Should().Be(_targetKey);
            observation.PhysicalSourceFingerprint.Should().Be(_fingerprint);
            observation.Status.Should().Be(DocumentCacheDownstreamPublicationStatus.Unknown);
            observation.EvidenceSource.Should().Be("document-cache-default-downstream-publication-history");
            observation.ObservedAt.Should().Be(_observedAt);
            observation.DiagnosticText.Should().Contain("durable CDC binding state");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Downstream_Publication_History_Proof_Evaluation
        : DocumentCacheDownstreamPublicationHistoryTests
    {
        [Test]
        public void It_should_accept_internal_only_observations_for_the_same_target_and_fingerprint()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                targetKey: DocumentCacheTargetKey.Create("tenanta", 7),
                fingerprint: _fingerprint
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    _fingerprint,
                    observation,
                    expectedPhysicalSourceFingerprint: _fingerprint
                );

            result.IsAccepted.Should().BeTrue();
            result.Classification.Should().Be(DocumentCacheAdministrativePreflightClassification.Eligible);
            result
                .DownstreamPublicationStatus.Should()
                .Be(DocumentCacheDownstreamPublicationStatus.InternalOnly);
            result.Diagnostics.Should().BeEmpty();
        }

        [TestCase(DocumentCacheDownstreamPublicationStatus.Active)]
        [TestCase(DocumentCacheDownstreamPublicationStatus.Historical)]
        [TestCase(DocumentCacheDownstreamPublicationStatus.Possible)]
        [TestCase(DocumentCacheDownstreamPublicationStatus.Unknown)]
        public void It_should_reject_non_internal_only_downstream_history(
            DocumentCacheDownstreamPublicationStatus status
        )
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                status,
                fingerprint: _fingerprint
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    _fingerprint,
                    observation
                );

            result.IsAccepted.Should().BeFalse();
            result
                .Classification.Should()
                .Be(DocumentCacheAdministrativePreflightClassification.DownstreamHistoryPresentOrUnknown);
            result.DownstreamPublicationStatus.Should().Be(status);
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.DownstreamPublicationHistoryPresentOrUnknown);
        }

        [Test]
        public void It_should_reject_when_the_current_target_fingerprint_is_missing()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                fingerprint: _fingerprint
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    currentPhysicalSourceFingerprint: null,
                    observation
                );

            AssertExpectedSourceMismatch(result);
        }

        [Test]
        public void It_should_reject_observations_bound_to_a_different_target()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                targetKey: DocumentCacheTargetKey.Create("TenantA", 8),
                fingerprint: _fingerprint
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    _fingerprint,
                    observation
                );

            AssertExpectedSourceMismatch(result);
        }

        [Test]
        public void It_should_reject_when_the_downstream_observation_fingerprint_is_missing()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                fingerprint: null
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    _fingerprint,
                    observation
                );

            AssertExpectedSourceMismatch(result);
        }

        [Test]
        public void It_should_reject_when_the_downstream_observation_fingerprint_differs_from_current()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                fingerprint: _otherFingerprint
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    _fingerprint,
                    observation
                );

            AssertExpectedSourceMismatch(result);
        }

        [Test]
        public void It_should_reject_when_the_request_expected_fingerprint_differs_from_current()
        {
            DocumentCacheDownstreamPublicationHistoryObservation observation = Observation(
                DocumentCacheDownstreamPublicationStatus.InternalOnly,
                fingerprint: _fingerprint
            );

            DocumentCacheDownstreamPublicationHistoryProofResult result =
                DocumentCacheDownstreamPublicationHistoryProofEvaluator.Evaluate(
                    _targetKey,
                    _fingerprint,
                    observation,
                    expectedPhysicalSourceFingerprint: _otherFingerprint
                );

            AssertExpectedSourceMismatch(result);
        }

        private static void AssertExpectedSourceMismatch(
            DocumentCacheDownstreamPublicationHistoryProofResult result
        )
        {
            result.IsAccepted.Should().BeFalse();
            result
                .Classification.Should()
                .Be(DocumentCacheAdministrativePreflightClassification.ExpectedSourceMismatch);
            result
                .Diagnostics.Should()
                .ContainSingle()
                .Which.Category.Should()
                .Be(DocumentCacheTargetDiagnosticCategory.ExpectedSourceMismatch);
        }
    }

    private static DocumentCacheDownstreamPublicationHistoryObservation Observation(
        DocumentCacheDownstreamPublicationStatus status,
        DocumentCacheTargetKey? targetKey = null,
        DocumentCachePhysicalSourceFingerprint? fingerprint = null,
        string? evidenceSource = "fake-binding-store",
        string? evidenceGenerationIdentifier = "binding-generation-1",
        string diagnosticText = "Internal-only downstream observation."
    ) =>
        new(
            targetKey ?? _targetKey,
            fingerprint,
            status,
            evidenceSource,
            evidenceGenerationIdentifier,
            _observedAt,
            diagnosticText
        );
}
