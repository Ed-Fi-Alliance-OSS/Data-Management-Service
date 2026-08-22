// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
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
                diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
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
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.IncludeListViolation);
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [Test]
    public void It_reports_malformed_provider_setup_evidence_without_throwing_for_template_readiness()
    {
        CdcProviderSetupResult providerSetupResult = BuildProviderSetupResult(CdcProvider.SqlServer) with
        {
            ArtifactInventory =
            [
                BuildSqlServerGatingRoleArtifact(),
                BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.Document) with
                {
                    SafeObservedValues = null!,
                },
                BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
            ],
            SourceTableInventory = null!,
            ExpectedMessageKeyColumns = null!,
        };

        CdcProviderSetupReadiness readiness = Readiness(providerSetupResult);

        using var _ = new AssertionScope();
        readiness.CanRenderTemplate.Should().BeFalse();
        readiness
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ObservedValue == "missing"
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ObservedValue == "missing"
            )
            .And.Contain(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "missing"
            );
        string.Join(
                "|",
                readiness.Diagnostics.SelectMany(diagnostic =>
                    new[]
                    {
                        diagnostic.ExpectedValue ?? string.Empty,
                        diagnostic.ObservedValue ?? string.Empty,
                    }
                )
            )
            .Should()
            .NotContain("${env:CDC_DATABASE_PASSWORD}");
    }

    [Test]
    public void It_reports_null_nested_provider_setup_inventory_entries_without_throwing_for_template_readiness()
    {
        CdcProviderSetupResult providerSetupResult = BuildProviderSetupResult(CdcProvider.Postgresql) with
        {
            SourceTableInventory =
            [
                BuildSourceTable(
                    CdcProvider.Postgresql,
                    CdcSourceTableKind.DocumentCache,
                    "DocumentCache",
                    [BuildColumn(CdcProvider.Postgresql, "DocumentUuid")]
                ),
                null!,
                BuildSourceTable(
                    CdcProvider.Postgresql,
                    CdcSourceTableKind.CdcHeartbeat,
                    "CdcHeartbeat",
                    [
                        BuildColumn(CdcProvider.Postgresql, "HeartbeatId"),
                        BuildColumn(CdcProvider.Postgresql, "HeartbeatSequence", 2),
                        BuildColumn(CdcProvider.Postgresql, "HeartbeatAt", 3),
                    ]
                ),
            ],
            ExpectedMessageKeyColumns =
            [
                new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
                new(CdcSourceTableKind.Document, null!),
            ],
        };
        CdcProviderSetupReadiness? readiness = null;

        Action act = () => readiness = Readiness(providerSetupResult);

        using var _ = new AssertionScope();
        act.Should().NotThrow();
        readiness.Should().NotBeNull();
        readiness!.CanRenderTemplate.Should().BeFalse();
        readiness
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ObservedValue == "3"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ObservedValue == "2"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [Test]
    public void It_reports_shared_provider_prerequisite_diagnostics_for_request_validation_and_readiness()
    {
        CdcProviderSetupResult providerSetupResult = BuildProviderSetupResult(
            CdcProvider.Postgresql,
            artifactInventory: [],
            sourceTableInventory:
            [
                BuildSourceTable(
                    CdcProvider.Postgresql,
                    CdcSourceTableKind.DocumentCache,
                    "DocumentCache",
                    [BuildColumn(CdcProvider.Postgresql, "DocumentUuid;DROP_TABLE")]
                ),
                BuildSourceTable(
                    CdcProvider.Postgresql,
                    CdcSourceTableKind.Document,
                    "DocumentProjectionWork;DROP_TABLE",
                    [
                        BuildColumn(CdcProvider.Postgresql, "DocumentUuid"),
                        BuildColumn(CdcProvider.Postgresql, "DocumentUuid", 2),
                    ]
                ),
                BuildSourceTable(
                    CdcProvider.Postgresql,
                    CdcSourceTableKind.CdcHeartbeat,
                    "CdcHeartbeat",
                    [
                        BuildColumn(CdcProvider.Postgresql, "HeartbeatId"),
                        BuildColumn(CdcProvider.Postgresql, "HeartbeatSequence", 2),
                        BuildColumn(CdcProvider.Postgresql, "HeartbeatAt", 3),
                    ]
                ),
            ]
        );
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcProviderSetupReadiness readiness = service.GetProviderSetupReadiness(providerSetupResult);
        CdcConnectorTemplateValidationResult requestValidation = service.ValidateRequest(
            BuildRequest(
                providerSetupResult,
                providerConnectionProperties: new CdcProviderConnectionProperties(
                    CdcProvider.Postgresql,
                    BuildProviderConnectionProperties(CdcProvider.Postgresql)
                ),
                deploymentPolicy: BuildDeploymentPolicy(CdcProvider.Postgresql)
            )
        );

        using var _ = new AssertionScope();
        readiness.CanRenderTemplate.Should().BeFalse();
        requestValidation.IsValid.Should().BeFalse();
        NormalizeProviderPrerequisiteDiagnostics(
                requestValidation.Diagnostics.Where(IsNotBindingArtifactNameDiagnostic).ToArray()
            )
            .Should()
            .BeEquivalentTo(
                NormalizeProviderPrerequisiteDiagnostics(
                    readiness.Diagnostics.Where(IsNotBindingArtifactNameDiagnostic).ToArray()
                )
            );
        requestValidation
            .Diagnostics.Where(diagnostic => !IsNotBindingArtifactNameDiagnostic(diagnostic))
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.ExpectedValue
                    == "one matched provider setup artifact with the expected binding name"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        readiness
            .Diagnostics.Where(diagnostic => !IsNotBindingArtifactNameDiagnostic(diagnostic))
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.ExpectedValue == "one matched provider setup artifact"
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            );
        requestValidation
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.SafeArtifactOrObjectName == new CdcSafeName("dms_binding_connector")
            );
        readiness
            .Diagnostics.All(diagnostic => diagnostic.SafeArtifactOrObjectName is null)
            .Should()
            .BeTrue();
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

    private static object[] NormalizeProviderPrerequisiteDiagnostics(
        IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics
    ) =>
        diagnostics
            .Select(diagnostic => new
            {
                diagnostic.Code,
                diagnostic.Category,
                diagnostic.Severity,
                diagnostic.PropertyName,
                diagnostic.ExpectedValue,
                diagnostic.ObservedValue,
                diagnostic.Provider,
                diagnostic.SourcePhase,
                diagnostic.RedactionClassification,
            })
            .ToArray();

    private static bool IsNotBindingArtifactNameDiagnostic(CdcConnectorTemplateDiagnostic diagnostic) =>
        diagnostic.Code
            is not (
                CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired
                or CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired
                or CdcConnectorTemplateDiagnosticCodes.SqlServerGatingRoleMetadataRequired
                or CdcConnectorTemplateDiagnosticCodes.ProviderSetupArtifactInventoryMalformed
            );
}
