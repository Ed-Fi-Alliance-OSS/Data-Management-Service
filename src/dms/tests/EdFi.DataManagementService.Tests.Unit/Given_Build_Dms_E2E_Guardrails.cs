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
        // E2ETests invokes Initialize-E2EDatabase and forwards the deferred-start decision so the
        // SQL Server path starts DMS after provisioning while PostgreSQL restarts it (matched by
        // token rather than exact line so the multi-line call is not whitespace-brittle).
        e2eTestFunctionContents
            .Should()
            .Contain("Initialize-E2EDatabase")
            .And.Contain("-E2ETestSettings $e2eTestSettings")
            .And.Contain("-UsePublishedImage:$UsePublishedImage")
            .And.Contain("-StartDmsAfterProvisioning:$deferDmsStart");
        // The PostgreSQL (non-deferred) path restarts DMS after reprovisioning to discard cached
        // datastore connection pools; the SQL Server path instead starts DMS with -DmsOnly.
        initializeFunctionContents
            .Should()
            .Contain("Restart-DmsContainer")
            .And.Contain(
                "-Reason \"discard cached datastore connection pools after E2E database reprovisioning\""
            )
            .And.Contain("-DmsOnly");
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

    [Test]
    public void It_always_sets_the_e2e_datastore_database_name_for_the_test_process()
    {
        var processContextDefinition = ExtractFunctionBody("Invoke-WithE2ETestProcessContext");
        var beforeAction = processContextDefinition.Split("& $Action", StringSplitOptions.None)[0];
        var removedRelationalBackendSetting = "AppSettings__Use" + "Relational" + "Backend";

        beforeAction
            .Should()
            .Contain("AppSettings__DataStoreDatabaseName must be set")
            .And.Contain("$env:AppSettings__DataStoreDatabaseName = $E2ETestSettings.DataStoreDatabaseName");
        beforeAction.Should().NotContain("Remove-Item Env:AppSettings__DataStoreDatabaseName");
        processContextDefinition.Should().NotContain(removedRelationalBackendSetting);
    }

    [Test]
    public void It_uses_the_e2e_datastore_database_name_when_creating_cms_data_stores()
    {
        var startEnvironmentDefinition = ExtractFunctionBody("Start-DockerEnvironment");
        var e2eTestDefinition = ExtractFunctionBody("E2ETests");

        startEnvironmentDefinition
            .Should()
            .Contain("[string]")
            .And.Contain("$DataStoreDatabaseName = \"\"")
            .And.Contain("configure-local-data-store.ps1")
            .And.Contain("-DataStoreDatabaseName $DataStoreDatabaseName")
            .And.Contain("start-published-dms.ps1")
            .And.Contain("-DataStoreDatabaseName $DataStoreDatabaseName");

        e2eTestDefinition.Should().Contain("-DataStoreDatabaseName $e2eTestSettings.DataStoreDatabaseName");
    }

    [Test]
    public void It_propagates_the_selected_database_engine_through_the_standard_e2e_chain()
    {
        // The top-level dispatch and each E2E orchestration function must forward -DatabaseEngine so
        // the engine selection reaches the engine-aware start/configure/provision leaf scripts.
        _buildScriptContents
            .Should()
            .Contain(
                "E2ETest { Invoke-TestExecution E2ETests"
                    + " -UsePublishedImage:$UsePublishedImage -SkipDockerBuild:$SkipDockerBuild"
                    + " -LoadSeedData:$LoadSeedData -IdentityProvider $IdentityProvider"
                    + " -TestFilter $TestFilter -DatabaseEngine $DatabaseEngine }"
            );

        ExtractFunctionBody("Invoke-TestExecution")
            .Should()
            .Contain("E2ETests -UsePublishedImage:$UsePublishedImage")
            .And.Contain("-DatabaseEngine $DatabaseEngine");

        ExtractFunctionBody("E2ETests")
            .Should()
            .Contain(
                "Get-E2ETestEnvironmentContext -EnvironmentFile $EnvironmentFile -TestFilter $TestFilter -DatabaseEngine $DatabaseEngine"
            )
            .And.Contain("-DatabaseEngine $e2eTestSettings.DatabaseEngine");

        ExtractFunctionBody("Start-DockerEnvironment")
            .Should()
            .Contain("-DatabaseEngine $resolvedDatabaseEngine");

        ExtractFunctionBody("Invoke-E2EDatabaseProvisioning")
            .Should()
            .Contain("-DatabaseEngine $E2ETestSettings.DatabaseEngine");
    }

    [Test]
    public void It_resolves_the_engine_overlay_after_the_data_standard_overlay_before_reading_the_database_name()
    {
        var environmentContext = ExtractFunctionBody("Get-E2ETestEnvironmentContext");

        int dataStandardIndex = environmentContext.IndexOf(
            "Resolve-DataStandardEnvironmentFile",
            StringComparison.Ordinal
        );
        int engineIndex = environmentContext.IndexOf(
            "Resolve-DatabaseEngineEnvironmentFile",
            StringComparison.Ordinal
        );
        int databaseNameIndex = environmentContext.IndexOf(
            "$environmentValues[\"E2E_DATABASE_NAME\"]",
            StringComparison.Ordinal
        );

        dataStandardIndex.Should().BeGreaterThan(-1);
        engineIndex.Should().BeGreaterThan(dataStandardIndex);
        databaseNameIndex.Should().BeGreaterThan(engineIndex);

        environmentContext.Should().Contain("New-E2EDataStoreConnectionStrings");
    }

    [Test]
    public void It_saves_sets_and_restores_the_engine_and_connection_strings_for_the_test_process()
    {
        var processContext = ExtractFunctionBody("Invoke-WithE2ETestProcessContext");
        var beforeAction = processContext.Split("& $Action", StringSplitOptions.None)[0];
        var afterAction = processContext.Split("& $Action", StringSplitOptions.None)[1];

        foreach (
            var variableName in new[]
            {
                "AppSettings__DatabaseEngine",
                "AppSettings__DataStoreAdminConnectionString",
                "AppSettings__DataStoreConnectionString",
            }
        )
        {
            // Saved before the action (env read into a $previous... variable), set before the action,
            // and restored-or-removed after it.
            beforeAction
                .Should()
                .Contain($"= $env:{variableName}")
                .And.Contain($"$env:{variableName} = $E2ETestSettings.");
            afterAction
                .Should()
                .Contain($"Remove-Item Env:{variableName} -ErrorAction SilentlyContinue")
                .And.Contain($"$env:{variableName} = $previous");
        }
    }

    [Test]
    public void It_does_not_emit_connection_strings_or_secrets_to_host_output()
    {
        // The two opaque connection strings and passwords must never be written to any output stream.
        foreach (
            var functionName in new[] { "Get-E2ETestEnvironmentContext", "Invoke-WithE2ETestProcessContext" }
        )
        {
            ExtractFunctionBody(functionName)
                .Should()
                .NotMatchRegex(
                    @"Write-(Host|Output|Information|Verbose|Debug|Warning)[^\r\n]*(ConnectionString|[Pp]assword)"
                );
        }
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
