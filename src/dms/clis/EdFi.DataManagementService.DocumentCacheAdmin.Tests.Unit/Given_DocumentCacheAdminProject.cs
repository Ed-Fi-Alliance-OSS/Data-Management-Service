// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml.Linq;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
public sealed class Given_DocumentCacheAdminProject
{
    private XDocument _project = null!;

    [SetUp]
    public void Setup()
    {
        _project = XDocument.Load(ProjectFilePath());
    }

    [Test]
    public void It_is_configured_as_the_document_cache_admin_tool_package()
    {
        ProjectProperty("TargetFramework").Should().Be("net10.0");
        ProjectProperty("OutputType").Should().Be("Exe");
        ProjectProperty("Nullable").Should().Be("enable");
        ProjectProperty("TreatWarningsAsErrors").Should().Be("true");
        ProjectProperty("PackageId").Should().Be(DocumentCacheAdminCliConstants.PackageId);
        ProjectProperty("PackAsTool").Should().Be("true");
        ProjectProperty("ToolCommandName").Should().Be(DocumentCacheAdminCliConstants.ToolCommandName);
        ProjectProperty("PackageReadmeFile").Should().Be("README.md");
    }

    [Test]
    public void It_references_shared_document_cache_runtime_projects()
    {
        ProjectReferences()
            .Should()
            .Contain([
                @"..\..\core\EdFi.DataManagementService.Core\EdFi.DataManagementService.Core.csproj",
                @"..\..\backend\EdFi.DataManagementService.Backend\EdFi.DataManagementService.Backend.csproj",
                @"..\..\backend\EdFi.DataManagementService.Backend.Postgresql\EdFi.DataManagementService.Backend.Postgresql.csproj",
                @"..\..\backend\EdFi.DataManagementService.Backend.Mssql\EdFi.DataManagementService.Backend.Mssql.csproj",
            ]);
    }

    [Test]
    public void It_uses_system_command_line_for_the_cli_surface()
    {
        PackageReferences().Should().Contain("System.CommandLine");
    }

    private string ProjectProperty(string name) =>
        _project
            .Root?.Elements("PropertyGroup")
            .Elements(name)
            .Select(element => element.Value)
            .FirstOrDefault()
        ?? throw new InvalidOperationException($"Project property '{name}' was not found.");

    private IEnumerable<string> ProjectReferences() =>
        _project.Root?.Elements("ItemGroup").Elements("ProjectReference").Select(ProjectItemInclude)
        ?? throw new InvalidOperationException("Project root was not found.");

    private IEnumerable<string> PackageReferences() =>
        _project.Root?.Elements("ItemGroup").Elements("PackageReference").Select(ProjectItemInclude)
        ?? throw new InvalidOperationException("Project root was not found.");

    private static string ProjectItemInclude(XElement element) =>
        element.Attribute("Include")?.Value
        ?? throw new InvalidOperationException("Project item is missing Include.");

    private static string ProjectFilePath() =>
        Path.Combine(
            RepositoryRoot(),
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "EdFi.DataManagementService.DocumentCacheAdmin.csproj"
        );

    private static string RepositoryRoot()
    {
        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            string solutionPath = Path.Combine(
                currentDirectory.FullName,
                "src",
                "dms",
                "EdFi.DataManagementService.sln"
            );

            if (File.Exists(solutionPath))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }
}
