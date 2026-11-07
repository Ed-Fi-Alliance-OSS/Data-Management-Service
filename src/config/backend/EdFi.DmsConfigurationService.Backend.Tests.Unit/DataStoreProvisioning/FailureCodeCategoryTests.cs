// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;
using FluentAssertions.Execution;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.DmsProvisioningFailureCategory;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.DmsProvisioningFailureCode;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class FailureCodeCategoryTests
{
    /// <summary>Spec section 8, pinned independently of the implementation table.</summary>
    private static readonly IReadOnlyDictionary<
        DmsProvisioningFailureCode,
        DmsProvisioningFailureCategory[]
    > _section8 = new Dictionary<DmsProvisioningFailureCode, DmsProvisioningFailureCategory[]>
    {
        [FeatureDisabled] = [NotReady],
        [StartupValidationFailed] = [NotReady],
        [AdapterUnavailable] = [NotReady],
        [TemplateNotAllowlisted] = [InvalidRequest],
        [InvalidTargetName] = [InvalidRequest],
        [EmptySourceIdentity] = [InvalidRequest],
        [ArtifactPinMissing] = [InvalidRequest],
        [ProtectedTarget] = [Permanent],
        [AdminPrivilegeMissing] = [Permanent],
        [RoleStateUnsafe] = [Permanent],
        [EngineIncompatible] = [Permanent],
        [StagingNotVisible] = [Permanent],
        [ServerRejected] = [Permanent],
        [SecretResolutionFailed] = [Transient],
        [ServerUnavailable] = [Transient],
        [LockTimeout] = [Transient],
        [LockUnavailable] = [Transient],
        [TimedOut] = [Transient],
        [StagingFailed] = [Transient],
        [StagingNameCollision] = [Transient],
        [Unknown] = [Transient],
        [ExclusiveAccessNotEstablished] = [Transient, OperatorInspectionRequired],
        [LockLost] = [Transient, OperatorInspectionRequired],
        [ArtifactMissing] = [OperatorInspectionRequired],
        [ArtifactUntrusted] = [OperatorInspectionRequired],
        [ArtifactTampered] = [OperatorInspectionRequired],
        [ManifestInvalid] = [OperatorInspectionRequired],
        [ManifestMismatch] = [OperatorInspectionRequired],
        [BackupInvalid] = [OperatorInspectionRequired],
        [PackageIdentityInvalid] = [OperatorInspectionRequired],
        [ArtifactChangedSinceCreate] = [OperatorInspectionRequired],
        [RestoreFailed] = [Permanent, Transient],
        [ContentProfileViolation] = [OperatorInspectionRequired],
        [TargetIncompatible] = [OperatorInspectionRequired],
        [SecurityStateViolation] = [OperatorInspectionRequired],
        [StagingIntegrityViolation] = [OperatorInspectionRequired],
        [IdentityAssignmentFailed] = [OperatorInspectionRequired],
        [TargetUnowned] = [OperatorInspectionRequired],
        [TargetUnverifiable] = [OperatorInspectionRequired],
        [TargetFenced] = [OperatorInspectionRequired],
        [InstanceChanged] = [OperatorInspectionRequired],
        [OutcomeUnknown] = [OperatorInspectionRequired],
        [CompensationFailed] = [OperatorInspectionRequired],
        [DropFailed] = [Transient, OperatorInspectionRequired],
    };

    private static readonly DmsProvisioningDiagnostic _diagnostic = DmsProvisioningDiagnostic.ForOperation(
        "Test"
    );

    [TestFixture]
    public class Given_every_failure_code
    {
        private DmsProvisioningFailureCode[] _codes = [];

        [SetUp]
        public void Setup() => _codes = Enum.GetValues<DmsProvisioningFailureCode>();

        [Test]
        public void It_defines_exactly_the_section_8_codes() =>
            _codes.Should().BeEquivalentTo(_section8.Keys);

        [Test]
        public void It_maps_each_code_to_its_section_8_categories()
        {
            using AssertionScope scope = new();
            foreach (DmsProvisioningFailureCode code in _codes)
            {
                DmsProvisioningFailure.CategoriesOf(code).Should().Equal(_section8[code], "code {0}", code);
            }
        }

        [Test]
        public void It_defaults_each_code_to_its_first_category()
        {
            using AssertionScope scope = new();
            foreach (DmsProvisioningFailureCode code in _codes)
            {
                DmsProvisioningFailure
                    .DefaultCategoryOf(code)
                    .Should()
                    .Be(_section8[code][0], "code {0}", code);
            }
        }

        [Test]
        public void It_creates_each_failure_in_the_default_category_with_the_fixed_summary()
        {
            using AssertionScope scope = new();
            foreach (DmsProvisioningFailureCode code in _codes)
            {
                DmsProvisioningFailure failure = DmsProvisioningFailure.Create(code, _diagnostic);
                failure.Category.Should().Be(_section8[code][0], "code {0}", code);
                failure.Summary.Should().Be(DmsProvisioningFailure.SummaryOf(code), "code {0}", code);
                failure.Diagnostic.Should().BeSameAs(_diagnostic);
            }
        }

        [Test]
        public void It_gives_every_code_a_distinct_non_blank_summary()
        {
            string[] summaries = [.. _codes.Select(DmsProvisioningFailure.SummaryOf)];
            summaries
                .Should()
                .OnlyContain(summary => !string.IsNullOrWhiteSpace(summary))
                .And.OnlyHaveUniqueItems();
        }

        [Test]
        public void It_gives_exactly_four_codes_a_context_dependent_category() =>
            _codes
                .Where(code => DmsProvisioningFailure.CategoriesOf(code).Count > 1)
                .Should()
                .BeEquivalentTo([ExclusiveAccessNotEstablished, LockLost, RestoreFailed, DropFailed]);
    }

    [TestFixture]
    public class Given_an_explicit_category
    {
        private DmsProvisioningFailure? _allowed;
        private Action _disallowed = () => { };
        private Action _undefinedCode = () => { };

        [SetUp]
        public void Setup()
        {
            _allowed = DmsProvisioningFailure.Create(LockLost, OperatorInspectionRequired, _diagnostic);
            _disallowed = () => DmsProvisioningFailure.Create(LockLost, Permanent, _diagnostic);
            _undefinedCode = () =>
                DmsProvisioningFailure.Create((DmsProvisioningFailureCode)int.MaxValue, _diagnostic);
        }

        [Test]
        public void It_accepts_a_category_the_code_allows() =>
            _allowed!.Category.Should().Be(OperatorInspectionRequired);

        [Test]
        public void It_keeps_the_code_and_fixed_summary() =>
            (_allowed!.Code, _allowed.Summary)
                .Should()
                .Be((LockLost, DmsProvisioningFailure.SummaryOf(LockLost)));

        [Test]
        public void It_rejects_a_category_the_code_does_not_allow() =>
            _disallowed.Should().Throw<ArgumentException>();

        [Test]
        public void It_rejects_an_undefined_code() =>
            _undefinedCode.Should().Throw<ArgumentOutOfRangeException>();
    }
}
