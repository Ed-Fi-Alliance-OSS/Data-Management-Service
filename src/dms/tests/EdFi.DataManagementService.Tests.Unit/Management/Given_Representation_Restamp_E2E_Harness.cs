// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Unit.Management;

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness
{
    private string _temporaryDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "dms1318-restamp-harness-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_temporaryDirectory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_finds_the_repository_root_for_normal_and_linked_worktree_git_markers(bool useGitFile)
    {
        string repositoryRoot = Path.Combine(_temporaryDirectory, "repository");
        string nestedDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");
        string solutionDirectory = Path.Combine(repositoryRoot, "src", "dms");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateDirectory(solutionDirectory);
        File.WriteAllText(Path.Combine(solutionDirectory, "EdFi.DataManagementService.sln"), "");

        string gitMarker = Path.Combine(repositoryRoot, ".git");
        if (useGitFile)
        {
            File.WriteAllText(gitMarker, "gitdir: ../worktrees/repository");
        }
        else
        {
            Directory.CreateDirectory(gitMarker);
        }

        RepresentationRestampE2EHarness
            .RepositoryRoot(new DirectoryInfo(nestedDirectory))
            .Should()
            .Be(repositoryRoot);
    }

    [TestCase("Debug")]
    [TestCase("Release")]
    public void It_reads_the_build_configuration_from_the_test_assembly_output_directory(
        string buildConfiguration
    )
    {
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "EdFi.DataManagementService.Tests.E2E",
            "bin",
            buildConfiguration,
            "net10.0"
        );
        Directory.CreateDirectory(outputDirectory);

        RepresentationRestampE2EHarness
            .BuildConfiguration(new DirectoryInfo(outputDirectory))
            .Should()
            .Be(buildConfiguration);
    }

    [Test]
    public void It_reads_the_nearest_configuration_segment_when_an_ancestor_directory_also_matches()
    {
        // A repository checked out under a path segment named Release must not outrank the
        // configuration segment of the output directory itself.
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "Release",
            "EdFi.DataManagementService.Tests.E2E",
            "bin",
            "Debug",
            "net10.0"
        );
        Directory.CreateDirectory(outputDirectory);

        RepresentationRestampE2EHarness
            .BuildConfiguration(new DirectoryInfo(outputDirectory))
            .Should()
            .Be("Debug");
    }

    [Test]
    public void It_falls_back_to_debug_when_the_output_directory_has_no_configuration_segment()
    {
        string outputDirectory = Path.Combine(_temporaryDirectory, "artifacts", "net10.0");
        Directory.CreateDirectory(outputDirectory);

        RepresentationRestampE2EHarness
            .BuildConfiguration(new DirectoryInfo(outputDirectory))
            .Should()
            .Be("Debug");
    }

    [Test]
    public void It_launches_the_document_cache_admin_cli_in_the_current_build_configuration()
    {
        const string toolProjectPath =
            "/repository/src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/EdFi.DataManagementService.DocumentCacheAdmin.csproj";
        const string documentUuid = "9622f938-2c1a-4f99-9bc4-10970b1c2649";

        IReadOnlyList<string> arguments = RepresentationRestampE2EHarness.BuildCliProcessArguments(
            toolProjectPath,
            "Release",
            "restamp-preview",
            1,
            ["--mode", "tracking", "--document-uuid", documentUuid]
        );

        arguments
            .Should()
            .Equal(
                "run",
                "--project",
                toolProjectPath,
                "--configuration",
                "Release",
                "--no-build",
                "--",
                "restamp-preview",
                "--data-store-id",
                "1",
                "--mode",
                "tracking",
                "--document-uuid",
                documentUuid,
                "--json"
            );
    }

    [Test]
    public void It_selects_postgresql_for_the_postgresql_database_engine()
    {
        RepresentationRestampE2EHarness
            .ProviderFor("postgresql")
            .Should()
            .Be(RelationalProviderToken.Postgresql);
    }

    [Test]
    public void It_selects_sql_server_for_the_mssql_database_engine()
    {
        RepresentationRestampE2EHarness.ProviderFor("mssql").Should().Be(RelationalProviderToken.SqlServer);
    }

    [TestCase(DocumentCacheRepresentationRestampMode.Disabled, true)]
    [TestCase(DocumentCacheRepresentationRestampMode.Tracking, false)]
    public void It_requires_a_lifecycle_reset_only_for_the_disabled_mode(
        DocumentCacheRepresentationRestampMode mode,
        bool expected
    )
    {
        RepresentationRestampE2EHarness.RequiresDisabledLifecycleReset(mode).Should().Be(expected);
    }

    [Test]
    public void It_rejects_an_unsupported_database_engine()
    {
        const string databaseEngine = "unsupported-provider";

        Action act = () => RepresentationRestampE2EHarness.ProviderFor(databaseEngine);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{databaseEngine}*");
    }

    [TestCase(
        typeof(EdFi.DataManagementService.Tests.E2E.Features.Resources.ETagValidationsFeature),
        "A representation restamp invalidates the strong ETag without changing domain fields"
    )]
    [TestCase(
        typeof(EdFi.DataManagementService.Tests.E2E.Features.ChangeQueries.LiveResourceEndpointsFilterByChangeVersion_Feature),
        "A representation restamp admits a current resource into a later live Change Query window without delete or key-change history"
    )]
    [TestCase(
        typeof(EdFi.DataManagementService.Tests.E2E.Features.Resources.ETagValidationsFeature),
        "A disabled representation restamp invalidates the strong ETag without changing domain fields"
    )]
    [TestCase(
        typeof(EdFi.DataManagementService.Tests.E2E.Features.ChangeQueries.LiveResourceEndpointsFilterByChangeVersion_Feature),
        "A disabled representation restamp admits a current resource into a later live Change Query window without delete or key-change history"
    )]
    public void It_selects_restamp_scenarios_for_the_supported_provider_matrix(
        Type featureType,
        string scenarioDescription
    )
    {
        MethodInfo scenarioMethod = featureType
            .GetMethods()
            .Single(method =>
                method
                    .GetCustomAttributesData()
                    .SingleOrDefault(attribute => attribute.AttributeType == typeof(DescriptionAttribute))
                    ?.ConstructorArguments.Single()
                    .Value as string
                == scenarioDescription
            );
        string[] categories = scenarioMethod
            .GetCustomAttributes<CategoryAttribute>()
            .Select(attribute => attribute.Name)
            .ToArray();

        categories.Should().Contain("PostgresqlRepresentative");
        categories.Should().Contain("MssqlRepresentative");
    }

    [Test]
    public async Task It_selects_npgsql_and_double_quoted_sql_for_postgresql_operations()
    {
        IRepresentationRestampE2EProviderOperations operations =
            RepresentationRestampE2EHarness.ProviderOperationsFor(RelationalProviderToken.Postgresql);

        await using DbConnection connection = operations.OpenConnection(
            "Host=localhost;Database=restamp;Username=restamp;Password=restamp"
        );

        connection.Should().BeOfType<NpgsqlConnection>();
        operations.Sql.SetLifecycle.Should().Contain("dms.\"DocumentCacheState\"");
        operations.Sql.ReadCanonicalContentVersion.Should().Contain("dms.\"Document\"");
        operations.Sql.ReadRequiredWorkVersion.Should().Contain("dms.\"DocumentProjectionWork\"");
        operations.Sql.IsProjected.Should().Contain("dms.\"DocumentCache\"");
        operations.Sql.IsProjected.Should().Contain("dms.\"DocumentProjectionWork\"");
    }

    [Test]
    public async Task It_selects_sql_connection_and_bracketed_sql_for_sql_server_operations()
    {
        IRepresentationRestampE2EProviderOperations operations =
            RepresentationRestampE2EHarness.ProviderOperationsFor(RelationalProviderToken.SqlServer);

        await using DbConnection connection = operations.OpenConnection(
            "Server=localhost;Database=restamp;User Id=restamp;Password=restamp;TrustServerCertificate=true"
        );

        connection.Should().BeOfType<SqlConnection>();
        operations.Sql.SetLifecycle.Should().Contain("[dms].[DocumentCacheState]");
        operations.Sql.ReadCanonicalContentVersion.Should().Contain("[dms].[Document]");
        operations.Sql.ReadRequiredWorkVersion.Should().Contain("[dms].[DocumentProjectionWork]");
        operations.Sql.IsProjected.Should().Contain("[dms].[DocumentCache]");
        operations.Sql.IsProjected.Should().Contain("[dms].[DocumentProjectionWork]");
    }

    [Test]
    public async Task It_loads_the_real_manifest_workspace_for_the_ordinary_projector()
    {
        string apiSchemaDirectory = Path.Combine(AppContext.BaseDirectory, "ApiSchema");

        await using ServiceProvider serviceProvider =
            await RepresentationRestampE2EHarness.CreateProjectionServiceProviderAsync(
                RelationalProviderToken.Postgresql,
                "postgresql",
                apiSchemaDirectory,
                CancellationToken.None
            );

        IEffectiveSchemaSetProvider schemaSetProvider =
            serviceProvider.GetRequiredService<IEffectiveSchemaSetProvider>();
        schemaSetProvider.IsInitialized.Should().BeTrue();
        schemaSetProvider.EffectiveSchemaSet.EffectiveSchema.ResourceKeyCount.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task It_translates_poll_cancellation_into_a_contextual_timeout()
    {
        const string timeoutContext =
            "representation-restamp projection for document 9622f938-2c1a-4f99-9bc4-10970b1c2649";

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.WaitForConditionAsync(
                _ => Task.FromResult(false),
                TimeSpan.FromMilliseconds(25),
                TimeSpan.FromMinutes(1),
                timeoutContext
            );

        await act.Should().ThrowAsync<TimeoutException>().WithMessage($"*{timeoutContext}*");
    }

    [Test]
    public async Task It_removes_the_schema_copy_when_projection_waiting_fails()
    {
        string schemaCopyDirectory = Path.Combine(_temporaryDirectory, "schema-copy");
        Directory.CreateDirectory(schemaCopyDirectory);
        await File.WriteAllTextAsync(Path.Combine(schemaCopyDirectory, "schema.json"), "{}");
        var expectedException = new TimeoutException("projection state context");

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunWithDirectoryCleanupAsync(
                schemaCopyDirectory,
                () => Task.FromException(expectedException)
            );

        TimeoutException exception = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        exception.Should().BeSameAs(expectedException);
        Directory.Exists(schemaCopyDirectory).Should().BeFalse();
    }
}

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Disabled_Command
{
    private readonly Guid _documentUuid = Guid.Parse("9622f938-2c1a-4f99-9bc4-10970b1c2649");
    private readonly Guid _operationId = Guid.Parse("d899d246-9b72-433b-b62e-b69dd4e6c5bc");
    private readonly List<(string Command, string[] Arguments)> _commands = [];
    private readonly List<string> _events = [];

    [SetUp]
    public async Task Setup()
    {
        _commands.Clear();
        _events.Clear();
        await RepresentationRestampE2EHarness.ExecuteRestampCommandsAsync(
            _documentUuid,
            DocumentCacheRepresentationRestampMode.Disabled,
            (command, arguments) =>
            {
                _commands.Add((command, arguments.ToArray()));
                _events.Add(command);
                return Task.FromResult(
                    command == "restamp-preview"
                        ? new JsonObject { ["result"] = new JsonObject { ["operationId"] = _operationId } }
                        : new JsonObject
                        {
                            ["result"] = new JsonObject
                            {
                                ["state"] = "completed",
                                ["claimLevel"] = "canonicalOnlyComplete",
                            },
                        }
                );
            },
            () =>
            {
                _events.Add("verify canonical state");
                return Task.CompletedTask;
            },
            () =>
            {
                _events.Add("drain projector");
                return Task.CompletedTask;
            }
        );
    }

    [Test]
    public void It_previews_the_selected_document_in_explicit_disabled_mode()
    {
        _commands[0].Command.Should().Be("restamp-preview");
        _commands[0]
            .Arguments.Should()
            .Equal(
                "--mode",
                "disabled",
                "--reason",
                "E2E observable representation restamp verification",
                "--document-uuid",
                _documentUuid.ToString(),
                "--offline-writer-admission",
                "closedAndDrained"
            );
    }

    [Test]
    public void It_executes_the_preview_operation_with_the_exact_confirmation()
    {
        _commands[1].Command.Should().Be("restamp-execute");
        _commands[1]
            .Arguments.Should()
            .Equal(
                "--operation-id",
                _operationId.ToString(),
                "--confirm",
                "representationRestamp",
                "--offline-writer-admission",
                "closedAndDrained"
            );
    }

    [Test]
    public void It_verifies_canonical_state_without_draining_the_projector()
    {
        _events.Should().Equal("restamp-preview", "restamp-execute", "verify canonical state");
    }

    [TestCase("paused", "canonicalOnlyComplete", "state")]
    [TestCase("completed", "projectionWorkQueued", "claimLevel")]
    public async Task It_rejects_incomplete_or_publication_claiming_disabled_results(
        string state,
        string claimLevel,
        string rejectedField
    )
    {
        List<string> followUp = [];
        Func<Task> act = () =>
            RepresentationRestampE2EHarness.ExecuteRestampCommandsAsync(
                _documentUuid,
                DocumentCacheRepresentationRestampMode.Disabled,
                (command, _) =>
                    Task.FromResult(
                        command == "restamp-preview"
                            ? new JsonObject
                            {
                                ["result"] = new JsonObject { ["operationId"] = _operationId },
                            }
                            : new JsonObject
                            {
                                ["result"] = new JsonObject
                                {
                                    ["state"] = state,
                                    ["claimLevel"] = claimLevel,
                                },
                            }
                    ),
                () =>
                {
                    followUp.Add("verify");
                    return Task.CompletedTask;
                },
                () =>
                {
                    followUp.Add("drain");
                    return Task.CompletedTask;
                }
            );

        await act.Should().ThrowAsync<AssertionException>().WithMessage($"*{rejectedField}*");
        followUp.Should().BeEmpty();
    }

    [Test]
    public async Task It_preserves_tracking_preview_and_drain_after_canonical_verification()
    {
        List<string> events = [];
        await RepresentationRestampE2EHarness.ExecuteRestampCommandsAsync(
            _documentUuid,
            DocumentCacheRepresentationRestampMode.Tracking,
            (command, arguments) =>
            {
                events.Add(command);
                if (command == "restamp-preview")
                {
                    arguments.Take(2).Should().Equal("--mode", "tracking");
                    return Task.FromResult(
                        new JsonObject { ["result"] = new JsonObject { ["operationId"] = _operationId } }
                    );
                }

                return Task.FromResult(
                    new JsonObject
                    {
                        ["result"] = new JsonObject
                        {
                            ["state"] = "completed",
                            ["claimLevel"] = "projectionWorkQueued",
                        },
                    }
                );
            },
            () =>
            {
                events.Add("verify");
                return Task.CompletedTask;
            },
            () =>
            {
                events.Add("drain");
                return Task.CompletedTask;
            }
        );

        events.Should().Equal("restamp-preview", "restamp-execute", "verify", "drain");
    }
}

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Docker_Failure_Messages
{
    [Test]
    public void It_reports_the_operation_container_exit_code_and_both_streams()
    {
        string message = RepresentationRestampE2EHarness.DockerFailureMessage(
            "cp",
            "dms-published-dms-1",
            1,
            "  copied nothing  ",
            "  Error response from daemon: No such container: dms-published-dms-1  "
        );

        message.Should().Contain("docker cp failed");
        message.Should().Contain("dms-published-dms-1");
        message.Should().Contain("exit code 1");
        message.Should().Contain("stdout: copied nothing");
        message.Should().Contain("stderr: Error response from daemon: No such container");
    }

    [Test]
    public void It_names_the_setting_that_selects_the_container_for_each_image_mode()
    {
        string message = RepresentationRestampE2EHarness.DockerFailureMessage(
            "stop",
            "ed-fi-api",
            125,
            "",
            "No such container"
        );

        message.Should().Contain("AppSettings__DmsContainerName");
        message.Should().Contain("local image: ed-fi-api");
        message.Should().Contain("published image: dms-published-dms-1");
    }

    [Test]
    public void It_reports_a_launch_failure_without_inventing_an_exit_code()
    {
        string message = RepresentationRestampE2EHarness.DockerStartFailureMessage(
            "cp",
            "dms-published-dms-1"
        );

        message.Should().Contain("failed to start docker for operation 'cp'");
        message.Should().Contain("dms-published-dms-1");
        message.Should().NotContain("exit code");
    }
}

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Cleanup
{
    private const string Context = "representation restamp (Tracking) for document 9622f938";

    [Test]
    public async Task It_runs_cleanup_after_a_successful_action()
    {
        List<string> events = [];

        await RepresentationRestampE2EHarness.RunWithCleanupAsync(
            Context,
            () =>
            {
                events.Add("action");
                return Task.CompletedTask;
            },
            () =>
            {
                events.Add("cleanup");
                return Task.CompletedTask;
            }
        );

        events.Should().Equal("action", "cleanup");
    }

    [Test]
    public async Task It_still_runs_cleanup_when_the_action_fails()
    {
        var primaryFailure = new InvalidOperationException("restamp failed");
        var cleanupRan = false;

        Func<Task> act = () =>
            RepresentationRestampE2EHarness.RunWithCleanupAsync(
                Context,
                () => Task.FromException(primaryFailure),
                () =>
                {
                    cleanupRan = true;
                    return Task.CompletedTask;
                }
            );

        InvalidOperationException thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Should().BeSameAs(primaryFailure);
        cleanupRan.Should().BeTrue();
    }

    [Test]
    public async Task It_surfaces_a_cleanup_failure_when_the_action_succeeded()
    {
        var cleanupFailure = new InvalidOperationException("docker start failed");

        Func<Task> act = () =>
            RepresentationRestampE2EHarness.RunWithCleanupAsync(
                Context,
                () => Task.CompletedTask,
                () => Task.FromException(cleanupFailure)
            );

        InvalidOperationException thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Should().BeSameAs(cleanupFailure);
    }

    [Test]
    public async Task It_preserves_the_primary_failure_first_when_cleanup_also_fails()
    {
        var primaryFailure = new InvalidOperationException("restamp failed");
        var cleanupFailure = new InvalidOperationException("docker start failed");

        Func<Task> act = () =>
            RepresentationRestampE2EHarness.RunWithCleanupAsync(
                Context,
                () => Task.FromException(primaryFailure),
                () => Task.FromException(cleanupFailure)
            );

        AggregateException thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.Message.Should().Contain(Context);
        thrown.InnerExceptions.Should().Equal(primaryFailure, cleanupFailure);
    }

    [Test]
    public async Task It_flattens_a_nested_cleanup_so_the_primary_failure_stays_first()
    {
        // The restamp cleanup is itself a RunWithCleanupAsync: reset the lifecycle, then restart the
        // container whether or not the reset succeeded. All three failures have to survive, in order.
        var primaryFailure = new InvalidOperationException("restamp failed");
        var lifecycleFailure = new InvalidOperationException("lifecycle reset failed");
        var restartFailure = new InvalidOperationException("docker start failed");

        Func<Task> act = () =>
            RepresentationRestampE2EHarness.RunWithCleanupAsync(
                Context,
                () => Task.FromException(primaryFailure),
                () =>
                    RepresentationRestampE2EHarness.RunWithCleanupAsync(
                        "cleanup",
                        () => Task.FromException(lifecycleFailure),
                        () => Task.FromException(restartFailure)
                    )
            );

        AggregateException thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().Equal(primaryFailure, lifecycleFailure, restartFailure);
    }

    [Test]
    public async Task It_restarts_the_container_even_when_the_lifecycle_reset_fails()
    {
        // The guardrail that keeps a failed disabled-mode lifecycle reset from leaving DMS stopped.
        var restartRan = false;

        Func<Task> act = () =>
            RepresentationRestampE2EHarness.RunWithCleanupAsync(
                "cleanup",
                () => Task.FromException(new InvalidOperationException("lifecycle reset failed")),
                () =>
                {
                    restartRan = true;
                    return Task.CompletedTask;
                }
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        restartRan.Should().BeTrue();
    }
}
