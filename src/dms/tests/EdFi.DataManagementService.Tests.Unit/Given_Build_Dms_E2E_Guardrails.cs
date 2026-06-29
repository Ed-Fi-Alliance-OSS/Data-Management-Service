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
        var removedPositiveBackendLaneFilter = "Category=@relational-" + "backend";
        var removedNegativeBackendLaneFilter = "Category!=@relational-" + "backend";

        _buildScriptContents.Should().NotContain("Test-FilterIncludesRelationalCategory");
        _buildScriptContents.Should().NotContain("Test-FilterExcludesRelationalCategory");
        _buildScriptContents.Should().NotContain("Assert-E2ETestLaneMatchesFilter");
        _buildScriptContents.Should().NotContain(removedPositiveBackendLaneFilter);
        _buildScriptContents.Should().NotContain(removedNegativeBackendLaneFilter);
    }

    [Test]
    public void It_does_not_read_the_legacy_backend_lane_environment_variable()
    {
        var environmentContextFunctionContents = ExtractFunctionBody("Get-E2ETestEnvironmentContext");

        environmentContextFunctionContents.Should().NotContain("USE" + "_RELATIONAL_BACKEND");
        environmentContextFunctionContents.Should().NotContain("ConvertTo-Boolean");
        environmentContextFunctionContents.Should().Contain("E2E_DATABASE_NAME");
        environmentContextFunctionContents.Should().Contain("E2E_DATABASE_NAME must be set");
        environmentContextFunctionContents.Should().Contain("ShouldProvisionE2EDatabase = $true");
        environmentContextFunctionContents.Should().NotContain("\"edfi_datamanagementservice\"");
    }

    [Test]
    public void It_restarts_dms_after_e2e_database_reprovisioning()
    {
        _buildScriptContents.Should().NotContain("Initialize-RelationalE2EDatabase");
        _buildScriptContents.Should().NotContain("Invoke-RelationalE2EDatabaseProvisioning");

        var provisioningFunctionContents = ExtractFunctionBody("Invoke-E2EDatabaseProvisioning");
        var initializeFunctionContents = ExtractFunctionBody("Initialize-E2EDatabase");
        var e2eTestFunctionContents = ExtractFunctionBody("E2ETests");

        provisioningFunctionContents.Should().Contain("./provision-e2e-database.ps1");
        initializeFunctionContents
            .Should()
            .Contain("Invoke-E2EDatabaseProvisioning -E2ETestSettings $E2ETestSettings");
        e2eTestFunctionContents
            .Should()
            .Contain(
                "Invoke-Step { Initialize-E2EDatabase -E2ETestSettings $e2eTestSettings -UsePublishedImage:$UsePublishedImage }"
            );
        initializeFunctionContents
            .Should()
            .Contain("Restart-DmsContainer")
            .And.Contain("-Reason \"discard cached PostgreSQL pools after E2E database reprovisioning\"");
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
        suffixDefinition.Should().NotContain("relational-" + "ci-shard-");
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
