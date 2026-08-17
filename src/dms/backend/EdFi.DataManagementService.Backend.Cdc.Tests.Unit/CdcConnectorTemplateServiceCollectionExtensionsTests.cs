// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateServiceRegistration")]
public class Given_CdcConnectorTemplateServiceRegistration
{
    [Test]
    public void It_registers_the_connector_template_service()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddCdcConnectorTemplates();

        ServiceDescriptor descriptor = services
            .Should()
            .ContainSingle(service => service.ServiceType == typeof(ICdcConnectorTemplateService))
            .Subject;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Test]
    public void It_consumes_the_shared_provider_setup_result_contract()
    {
        CdcProviderSetupReadiness readiness = Readiness(
            BuildProviderSetupResult(CdcProvider.Postgresql, CdcProviderSetupOutcome.CreatedOrMatched)
        );

        using var _ = new AssertionScope();
        readiness.Provider.Should().Be(CdcProvider.Postgresql);
        readiness.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        readiness.CanRenderTemplate.Should().BeTrue();
        readiness.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_failed_provider_setup_results_for_template_rendering()
    {
        CdcProviderSetupReadiness readiness = Readiness(
            BuildProviderSetupResult(CdcProvider.Postgresql, CdcProviderSetupOutcome.Failed)
        );

        readiness.CanRenderTemplate.Should().BeFalse();
        readiness
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ProviderSetupResultNotReady
                && diagnostic.PropertyName == "providerSetup.outcome"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RequestValidation
            );
    }

    [Test]
    public void It_requires_complete_postgresql_provider_setup_evidence_for_template_readiness()
    {
        CdcProviderSetupResult incompleteProviderSetup = BuildProviderSetupResult(
            CdcProvider.Postgresql,
            artifactInventory: [],
            sourceTableInventory: [],
            expectedMessageKeyColumns: [],
            omitHeartbeatActionQuery: true
        ) with
        {
            ObservedSourceFingerprint = null,
        };

        CdcProviderSetupReadiness readiness = Readiness(incompleteProviderSetup);

        using var _ = new AssertionScope();
        readiness.CanRenderTemplate.Should().BeFalse();
        readiness
            .Diagnostics.Select(diagnostic => diagnostic.Code)
            .Should()
            .BeEquivalentTo(
                CdcConnectorTemplateDiagnosticCodes.SourceFingerprintEvidenceRequired,
                CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired,
                CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired
            );
        readiness
            .Diagnostics.Should()
            .AllSatisfy(diagnostic =>
            {
                diagnostic.SafeArtifactOrObjectName.Should().BeNull();
                diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.RequestValidation);
                diagnostic.Severity.Should().Be(CdcConnectorTemplateDiagnosticSeverity.Error);
            });
    }

    [Test]
    public void It_requires_fixed_source_table_names_for_template_readiness()
    {
        CdcProviderSetupResult providerSetupResult = BuildProviderSetupResult(
            CdcProvider.Postgresql,
            sourceTableInventory: BuildSourceInventoryReplacing(
                CdcProvider.Postgresql,
                BuildSourceTable(
                    CdcProvider.Postgresql,
                    CdcSourceTableKind.Document,
                    "DocumentProjectionWork;DROP TABLE",
                    [BuildColumn(CdcProvider.Postgresql, "DocumentUuid")]
                )
            )
        );

        CdcProviderSetupReadiness readiness = Readiness(providerSetupResult);

        CdcConnectorTemplateDiagnostic diagnostic = readiness
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
            )
            .Subject;

        using var _ = new AssertionScope();
        readiness.CanRenderTemplate.Should().BeFalse();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.IncludeList);
        diagnostic.PropertyName.Should().Be("table.include.list");
        diagnostic.ExpectedValue.Should().Be("dms.Document");
        diagnostic.ObservedValue.Should().Be("dms.DocumentProjectionWork_DROP_TABLE");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
    }

    [Test]
    public void It_requires_sqlserver_capture_instance_evidence_for_template_readiness()
    {
        CdcProviderSetupReadiness readiness = Readiness(
            BuildProviderSetupResult(CdcProvider.SqlServer, artifactInventory: [])
        );

        using var _ = new AssertionScope();
        readiness.CanRenderTemplate.Should().BeFalse();
        readiness
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code
                == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(diagnostic =>
                diagnostic.PropertyName == "providerSetup.artifactInventory.sqlServerCaptureInstance"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RequestValidation
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    private static CdcProviderSetupReadiness Readiness(CdcProviderSetupResult providerSetupResult)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddCdcConnectorTemplates();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        return service.GetProviderSetupReadiness(providerSetupResult);
    }
}
