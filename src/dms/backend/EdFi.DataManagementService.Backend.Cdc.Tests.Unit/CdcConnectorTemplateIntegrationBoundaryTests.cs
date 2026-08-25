// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml.Linq;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateIntegrationBoundaries")]
public class Given_CdcConnectorTemplateIntegrationBoundaryTests
{
    private static readonly string[] ExpectedPackageReferences =
    [
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    ];

    private static readonly string[] ExpectedProjectReferences =
    [
        "EdFi.DataManagementService.Backend.Ddl",
        "EdFi.DataManagementService.Core",
    ];

    [Test]
    public void It_returns_render_registration_and_artifact_evidence_without_connect_lifecycle_inputs()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                includeRedactedArtifactPayload: true
            )
        );

        CdcConnectorTemplateResult result = service.Render(request);

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.RegistrationPayload.Should().NotBeNull();
        result.RegistrationPayload!.Name.Should().Be(request.ConnectorName.Value);
        result.RegistrationPayload.Config.Should().Equal(result.Config);
        result.RedactedArtifactPayload.Should().NotBeNull();
        result
            .RedactedArtifactPayload!.FileName.Should()
            .Be(new CdcSafeName("cdc-connector-template.sqlserver.dms_binding_connector.manifest.json"));
        result.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        result.Config.Should().Contain("name", request.ConnectorName.Value);
        result
            .Config.Should()
            .Contain("schema.history.internal.kafka.topic", "edfi.documents.schema-history");
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.Contains("offset.storage", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("errors.deadletterqueue.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.Contains("acl", StringComparison.OrdinalIgnoreCase));
        result
            .Config.Values.Should()
            .NotContain(value => value.Contains("connectors/", StringComparison.Ordinal));
        result
            .Config.Values.Should()
            .NotContain(value => value.Contains("CREATE PUBLICATION", StringComparison.OrdinalIgnoreCase));
        result
            .Config.Values.Should()
            .NotContain(value => value.Contains("ALTER PUBLICATION", StringComparison.OrdinalIgnoreCase));
        result
            .Config.Values.Should()
            .NotContain(value => value.Contains("sp_cdc_enable_table", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void It_allows_preflight_and_live_validation_from_supplied_read_back_evidence()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult preflight = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: request.BindingIdentity.BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                )
            )
        );
        CdcConnectorTemplateResult liveReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: request.BindingIdentity.BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string> { ["server"] = request.ConnectorName.Value }
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Config.Should().Equal(rendered.Config);
        preflight.Diagnostics.Should().BeEmpty();
        liveReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBack.Config.Should().Equal(rendered.Config);
        liveReadBack.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_limits_project_dependencies_to_template_contracts()
    {
        XDocument project = XDocument.Load(CdcProjectFilePath());
        string[] packageReferences = project
            .Descendants("PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] projectReferences = project
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(referencePath => referencePath.Replace('\\', Path.DirectorySeparatorChar))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string dependencyText = string.Join('\n', packageReferences.Concat(projectReferences));

        using var _ = new AssertionScope();
        packageReferences.Should().Equal(ExpectedPackageReferences);
        projectReferences.Should().Equal(ExpectedProjectReferences);
        foreach (
            string forbiddenDependencyToken in new[]
            {
                "Http",
                "Kafka",
                "Docker",
                "Testcontainers",
                "Confluent",
                "Connect",
                "Topic",
                "Acl",
                "Offset",
                "Admin",
            }
        )
        {
            dependencyText
                .Contains(forbiddenDependencyToken, StringComparison.OrdinalIgnoreCase)
                .Should()
                .BeFalse();
        }
    }

    [Test]
    public void It_documents_that_the_pinned_runtime_must_supply_the_murmur2_partitioner_class()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcConnectorTemplateResult result = service.Render(BuildRequest(CdcProvider.Postgresql));

        using var _ = new AssertionScope();
        result
            .Config.Should()
            .Contain(
                "producer.override.partitioner.class",
                "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner"
            );
        typeof(ICdcConnectorTemplateService)
            .Assembly.GetTypes()
            .Select(type => type.Name)
            .Should()
            .NotContain(
                "KafkaMurmur2V1Partitioner",
                "this repository emits the pinned-image partitioner class name while the qualified connector runtime owns packaging the implementation"
            );
    }

    private static ServiceProvider BuildServiceProvider() =>
        new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

    private static string CdcProjectFilePath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "src",
            "dms",
            "backend",
            "EdFi.DataManagementService.Backend.Cdc",
            "EdFi.DataManagementService.Backend.Cdc.csproj"
        );

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "dms", "EdFi.DataManagementService.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from the test directory.");
    }
}
