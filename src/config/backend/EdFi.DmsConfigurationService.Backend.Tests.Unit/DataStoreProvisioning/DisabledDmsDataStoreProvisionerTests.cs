// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DisabledDmsDataStoreProvisionerTests
{
    private static DmsDataStoreProvisionerReadiness Recorded(DmsProvisionerReadiness verdict)
    {
        DmsDataStoreProvisionerReadiness readiness = new();
        readiness.Record(verdict);
        return readiness;
    }

    private static DmsDataStoreCreateRequest CreateRequest()
    {
        DmsTargetDatabaseName.TryCreate(
            "EdFi_Ods_Test",
            ProtectedDatabaseNames.Create("edfi_configurationservice", []),
            out DmsTargetDatabaseName name
        );
        return new DmsDataStoreCreateRequest(DmsDatabaseTemplate.Minimal, name, Guid.NewGuid(), Pin());
    }

    private static DmsTrustedArtifactPin Pin() =>
        new()
        {
            ContractVersion = 1,
            Provider = "postgresql",
            PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
            PackageVersion = "1.0.335",
            PackageSha256 = new string('a', 64),
            ArtifactSha256 = new string('b', 64),
            TemplateKind = "Minimal",
            DataStandardVersion = "5.2.0",
            EffectiveSchemaHash = new string('c', 64),
            InventorySha256 = new string('d', 64),
            ApiSchemaFormatVersion = "1.0.0",
            RelationalMappingVersion = "v3",
        };

    [TestFixture]
    public class Given_the_feature_is_disabled
    {
        private DmsArtifactResolution _resolution = null!;
        private DmsDataStoreCreateResult _create = null!;
        private DmsDataStoreDeleteResult _delete = null!;

        [SetUp]
        public async Task Setup()
        {
            DisabledDmsDataStoreProvisioner provisioner = new(
                Recorded(new DmsProvisionerReadiness.Disabled())
            );
            _resolution = await provisioner.ResolveArtifactAsync(
                DmsDatabaseTemplate.Sample,
                CancellationToken.None
            );
            _create = await provisioner.CreateAsync(CreateRequest(), CancellationToken.None);
            _delete = await provisioner.DeleteAsync(
                new DmsDataStoreDeleteRequest(CreateRequest().TargetName, Guid.NewGuid()),
                CancellationToken.None
            );
        }

        [Test]
        public void It_refuses_resolution_as_feature_disabled() =>
            _resolution
                .Should()
                .BeOfType<DmsArtifactResolution.Failure>()
                .Which.Detail.Should()
                .BeEquivalentTo(
                    DmsProvisioningFailure.Create(
                        DmsProvisioningFailureCode.FeatureDisabled,
                        DmsProvisioningDiagnostic.ForOperation(DisabledDmsDataStoreProvisioner.Operation)
                    )
                );

        [Test]
        public void It_refuses_create_with_a_clean_cleanup_report()
        {
            DmsDataStoreCreateResult.Failure failure = _create
                .Should()
                .BeOfType<DmsDataStoreCreateResult.Failure>()
                .Subject;
            failure.Detail.Code.Should().Be(DmsProvisioningFailureCode.FeatureDisabled);
            failure.Detail.Category.Should().Be(DmsProvisioningFailureCategory.NotReady);
            failure.Cleanup.Should().BeSameAs(DmsCleanupReport.Clean);
        }

        [Test]
        public void It_refuses_delete_with_a_clean_cleanup_report()
        {
            DmsDataStoreDeleteResult.Failure failure = _delete
                .Should()
                .BeOfType<DmsDataStoreDeleteResult.Failure>()
                .Subject;
            failure.Detail.Code.Should().Be(DmsProvisioningFailureCode.FeatureDisabled);
            failure.Cleanup.Should().BeSameAs(DmsCleanupReport.Clean);
        }
    }

    [TestFixture]
    public class Given_the_feature_is_enabled_but_not_ready
    {
        private DmsProvisioningFailure _adapterUnavailable = null!;
        private DmsProvisioningFailure _otherReason = null!;
        private DmsProvisioningFailure _pending = null!;

        [SetUp]
        public void Setup()
        {
            _adapterUnavailable = DisabledDmsDataStoreProvisioner.RefusalFor(
                new DmsProvisionerReadiness.NotReady([
                    new DmsReadinessReason(DmsProvisioningFailureCode.EngineIncompatible, "ServerFloors"),
                    new DmsReadinessReason(
                        DmsProvisioningFailureCode.AdapterUnavailable,
                        "AdapterResolution"
                    ),
                ])
            );
            _otherReason = DisabledDmsDataStoreProvisioner.RefusalFor(
                new DmsProvisionerReadiness.NotReady([
                    new DmsReadinessReason(DmsProvisioningFailureCode.StagingNotVisible, "StagingVisibility"),
                ])
            );
            _pending = DisabledDmsDataStoreProvisioner.RefusalFor(
                new DmsDataStoreProvisionerReadiness().Current
            );
        }

        [Test]
        public void It_reports_adapter_unavailable_when_any_reason_says_so() =>
            (_adapterUnavailable.Category, _adapterUnavailable.Code)
                .Should()
                .Be((DmsProvisioningFailureCategory.NotReady, DmsProvisioningFailureCode.AdapterUnavailable));

        [Test]
        public void It_reports_startup_validation_failed_otherwise() =>
            (_otherReason.Category, _otherReason.Code)
                .Should()
                .Be(
                    (
                        DmsProvisioningFailureCategory.NotReady,
                        DmsProvisioningFailureCode.StartupValidationFailed
                    )
                );

        [Test]
        public void It_refuses_before_startup_has_recorded_a_verdict() =>
            _pending.Code.Should().Be(DmsProvisioningFailureCode.StartupValidationFailed);
    }

    [TestFixture]
    public class Given_readiness_is_ready_with_no_orchestrator
    {
        private DisabledDmsDataStoreProvisioner _provisioner = null!;

        [SetUp]
        public void Setup() =>
            _provisioner = new DisabledDmsDataStoreProvisioner(
                Recorded(new DmsProvisionerReadiness.Ready("postgresql", DateTimeOffset.UtcNow))
            );

        [Test]
        public async Task It_refuses_to_pretend_it_can_provision()
        {
            Func<Task> create = () => _provisioner.CreateAsync(CreateRequest(), CancellationToken.None);
            await create.Should().ThrowAsync<InvalidOperationException>();
        }
    }
}
