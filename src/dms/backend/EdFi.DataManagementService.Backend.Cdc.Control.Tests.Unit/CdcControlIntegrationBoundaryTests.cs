// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The reverse of the connector-template project's dependency guardrail. The template library is
/// fenced off from Connect and Kafka lifecycle work; this library is fenced off from the ASP.NET
/// runtime host and from container test harnesses, so it cannot become a second home for either
/// template rules or runtime DMS behavior.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcControlIntegrationBoundaries")]
public class Given_CdcControlIntegrationBoundaryTests
{
    private static readonly string[] ExpectedPackageReferences =
    [
        "Confluent.Kafka",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Options",
    ];

    private static readonly string[] ExpectedProjectReferences =
    [
        "EdFi.DataManagementService.Backend",
        "EdFi.DataManagementService.Backend.Cdc",
        "EdFi.DataManagementService.Backend.Ddl",
        "EdFi.DataManagementService.Backend.Mssql",
        "EdFi.DataManagementService.Backend.Postgresql",
        "EdFi.DataManagementService.Core",
    ];

    private static readonly string[] ForbiddenDependencyTokens =
    [
        "AspNetCore",
        "Hosting",
        "Frontend",
        "Docker",
        "Testcontainers",
    ];

    [Test]
    public void It_limits_project_dependencies_to_the_control_plane_allow_list()
    {
        XDocument project = XDocument.Load(CdcControlProjectFilePath());
        string[] packageReferences = ReferenceNames(project, "PackageReference");
        string[] projectReferences = ReferenceNames(project, "ProjectReference");
        string dependencyText = string.Join('\n', packageReferences.Concat(projectReferences));

        using var _ = new AssertionScope();
        packageReferences.Should().Equal(ExpectedPackageReferences);
        projectReferences.Should().Equal(ExpectedProjectReferences);
        foreach (string forbiddenDependencyToken in ForbiddenDependencyTokens)
        {
            dependencyText
                .Contains(forbiddenDependencyToken, StringComparison.OrdinalIgnoreCase)
                .Should()
                .BeFalse("the CDC control plane must not depend on {0}", forbiddenDependencyToken);
        }
    }

    [Test]
    public void It_keeps_connector_template_rules_out_of_the_control_plane()
    {
        typeof(CdcControlOptions)
            .Assembly.GetTypes()
            .Select(type => type.Name)
            .Should()
            .NotContain(
                typeName =>
                    typeName.Contains("TemplateRenderer", StringComparison.Ordinal)
                    || typeName.Contains("TemplateValidator", StringComparison.Ordinal),
                "connector-template rendering and property comparison stay in the template library"
            );
    }

    private static string[] ReferenceNames(XDocument project, string elementName) =>
        project
            .Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Select(referencePath => referencePath.Replace('\\', Path.DirectorySeparatorChar))
            .Select(referencePath =>
                referencePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(referencePath)
                    : referencePath
            )
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string CdcControlProjectFilePath() =>
        Path.Combine(
            FindRepositoryRoot(),
            "src",
            "dms",
            "backend",
            "EdFi.DataManagementService.Backend.Cdc.Control",
            "EdFi.DataManagementService.Backend.Cdc.Control.csproj"
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

        throw new InvalidOperationException(
            "Unable to locate the repository root from the test output directory."
        );
    }
}
