// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_Build_Dms_E2E_Guardrails
{
    private DirectoryInfo _repositoryRoot = null!;
    private string _buildScriptContents = null!;

    [SetUp]
    public void Setup()
    {
        _repositoryRoot = FindRepositoryRoot();
        _buildScriptContents = File.ReadAllText(Path.Combine(_repositoryRoot.FullName, "build-dms.ps1"));
    }

    [Test]
    public void It_does_not_define_backend_lane_filter_assertions()
    {
        _buildScriptContents.Should().NotContain("Test-FilterIncludesRelationalCategory");
        _buildScriptContents.Should().NotContain("Test-FilterExcludesRelationalCategory");
        _buildScriptContents.Should().NotContain("Assert-E2ETestLaneMatchesFilter");
        _buildScriptContents.Should().NotContain("Category=@relational-backend");
        _buildScriptContents.Should().NotContain("Category!=@relational-backend");
    }

    [Test]
    public void It_does_not_read_the_legacy_backend_lane_environment_variable()
    {
        var environmentContextFunctionContents = ExtractFunctionBody("Get-E2ETestEnvironmentContext");

        environmentContextFunctionContents.Should().NotContain("USE_RELATIONAL_BACKEND");
        environmentContextFunctionContents.Should().NotContain("ConvertTo-Boolean");
        environmentContextFunctionContents.Should().Contain("E2E_DATABASE_NAME");
    }

    [Test]
    public void It_restarts_dms_after_relational_database_reprovisioning()
    {
        var provisioningFunctionContents = ExtractFunctionBody("Invoke-RelationalE2EDatabaseProvisioning");
        var initializeFunctionContents = ExtractFunctionBody("Initialize-RelationalE2EDatabase");

        provisioningFunctionContents.Should().Contain("./provision-e2e-database.ps1");
        initializeFunctionContents
            .Should()
            .Contain("Invoke-RelationalE2EDatabaseProvisioning -E2ETestSettings $E2ETestSettings");
        initializeFunctionContents
            .Should()
            .Contain("Restart-DmsContainer")
            .And.Contain("-Reason \"discard cached PostgreSQL pools after relational reprovisioning\"");
    }

    [Test]
    public void It_derives_shard_suffix_from_neutral_e2e_ci_shard_filter()
    {
        var suffixDefinition = ExtractFunctionBody("Get-E2ETestResultSuffix");

        suffixDefinition
            .Should()
            .Contain("e2e-ci-shard-")
            .And.Contain("e2e-shard-")
            .And.Contain("ConvertTo-NormalizedTestFilter");
        suffixDefinition.Should().NotContain("relational-ci-shard-");
        suffixDefinition.Should().NotContain("relational-shard-");
    }

    private string ExtractFunctionBody(string functionName)
    {
        int startIndex = _buildScriptContents.IndexOf($"function {functionName}", StringComparison.Ordinal);
        startIndex.Should().BeGreaterThan(-1, $"function '{functionName}' must exist in build-dms.ps1");

        int nextFunctionIndex = _buildScriptContents.IndexOf(
            "\nfunction ",
            startIndex + 1,
            StringComparison.Ordinal
        );

        int endIndex = nextFunctionIndex == -1 ? _buildScriptContents.Length : nextFunctionIndex;
        return _buildScriptContents.Substring(startIndex, endIndex - startIndex);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (
            currentDirectory is not null && !File.Exists(Path.Combine(currentDirectory.FullName, "LICENSE"))
        )
        {
            currentDirectory = currentDirectory.Parent;
        }

        return currentDirectory
            ?? throw new InvalidOperationException(
                "Could not locate repository root from the test assembly output."
            );
    }
}
