// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Reflection;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
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
        operations.Sql.ReadResidualWork.Should().Contain("dms.\"DocumentProjectionWork\"");
        operations.Sql.ReadResidualWork.Should().Contain("LEFT JOIN dms.\"DocumentCache\"");
        operations.Sql.ReadResidualWork.Should().Contain("LIMIT 50");
        operations.Sql.ReadDocumentCacheState.Should().Contain("dms.\"DocumentCacheState\"");
        operations.Sql.ReadDocumentCacheState.Should().Contain("\"CacheAheadRecoveryRequired\"");
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
        operations.Sql.ReadResidualWork.Should().Contain("[dms].[DocumentProjectionWork]");
        operations.Sql.ReadResidualWork.Should().Contain("LEFT JOIN [dms].[DocumentCache]");
        operations.Sql.ReadResidualWork.Should().Contain("SELECT TOP (50)");
        operations.Sql.ReadDocumentCacheState.Should().Contain("[dms].[DocumentCacheState]");
        operations.Sql.ReadDocumentCacheState.Should().Contain("[CacheAheadRecoveryRequired]");
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

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Phase_Budgets
{
    private const string TargetDescription = "'':1";

    [Test]
    public async Task It_names_the_phase_elapsed_and_budget_when_a_phase_times_out()
    {
        TimeSpan budget = TimeSpan.FromMilliseconds(25);

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunPhaseAsync(
                RepresentationRestampE2EHarness.OrdinaryDrainPhase,
                budget,
                TargetDescription,
                async cancellationToken =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }
            );

        TimeoutException exception = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        exception.Message.Should().Contain("phase 'OrdinaryDrain'");
        exception.Message.Should().Contain($"budget {budget}");
        exception.Message.Should().Contain($"for target {TargetDescription}.");
        exception.Message.Should().MatchRegex(@"after \d\d:\d\d:\d\d");
        exception.InnerException.Should().BeOfType<TaskCanceledException>();
    }

    [Test]
    public async Task It_returns_the_phase_result_when_the_phase_completes()
    {
        int result = await RepresentationRestampE2EHarness.RunPhaseAsync(
            RepresentationRestampE2EHarness.ProjectorSetupPhase,
            TimeSpan.FromMinutes(1),
            TargetDescription,
            _ => Task.FromResult(42)
        );

        result.Should().Be(42);
    }

    [Test]
    public async Task It_completes_a_result_free_phase()
    {
        var ran = false;

        await RepresentationRestampE2EHarness.RunPhaseAsync(
            RepresentationRestampE2EHarness.OrdinaryDrainPhase,
            TimeSpan.FromMinutes(1),
            TargetDescription,
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            }
        );

        ran.Should().BeTrue();
    }

    [Test]
    public async Task It_rethrows_cancellation_that_is_not_the_phase_budget()
    {
        using var externalSource = new CancellationTokenSource();
        await externalSource.CancelAsync();

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunPhaseAsync(
                RepresentationRestampE2EHarness.OrdinaryDrainPhase,
                TimeSpan.FromMinutes(1),
                TargetDescription,
                _ => Task.FromCanceled<int>(externalSource.Token)
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        await act.Should().NotThrowAsync<TimeoutException>();
    }

    [Test]
    public async Task It_rethrows_non_cancellation_failures_unchanged()
    {
        var expected = new InvalidOperationException("provider failure");

        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunPhaseAsync(
                RepresentationRestampE2EHarness.ProjectorSetupPhase,
                TimeSpan.FromMinutes(1),
                TargetDescription,
                _ => Task.FromException<int>(expected)
            );

        InvalidOperationException exception = (
            await act.Should().ThrowAsync<InvalidOperationException>()
        ).Which;
        exception.Should().BeSameAs(expected);
    }

    [Test]
    public void It_formats_the_phase_timeout_message()
    {
        string message = RepresentationRestampE2EHarness.PhaseTimeoutMessage(
            RepresentationRestampE2EHarness.ProjectorSetupPhase,
            TimeSpan.FromSeconds(125),
            TimeSpan.FromMinutes(2),
            TargetDescription
        );

        message
            .Should()
            .Be(
                "Timed out in representation-restamp phase 'ProjectorSetup' after 00:02:05 (budget 00:02:00) for target '':1."
            );
    }

    [Test]
    public void It_keeps_separate_two_minute_budgets_for_setup_and_drain()
    {
        RepresentationRestampE2EHarness.ProjectorSetupBudget.Should().Be(TimeSpan.FromMinutes(2));
        RepresentationRestampE2EHarness.OrdinaryDrainBudget.Should().Be(TimeSpan.FromMinutes(2));
    }
}

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Drain_Diagnostics
{
    private static readonly Guid _ownDocument = Guid.Parse("9622f938-2c1a-4f99-9bc4-10970b1c2649");
    private static readonly Guid _foreignDocument = Guid.Parse("3f1d2a6c-0d3f-4d1e-9c2b-7a5e0f1b2c3d");
    private static readonly DateTimeOffset _enqueuedAt = new(2026, 9, 11, 15, 53, 45, TimeSpan.Zero);

    [Test]
    public void It_counts_pages_items_failures_and_consecutive_pages_without_acknowledgement()
    {
        var tally = new RepresentationRestampDrainTally();

        tally.Record(DocumentCacheProjectionDrainPageResult.PageProcessed(2, 1, 1, [7]));
        tally.Record(DocumentCacheProjectionDrainPageResult.PageProcessed(1));
        tally.Record(DocumentCacheProjectionDrainPageResult.PageProcessed(1));

        tally.Pages.Should().Be(3);
        tally.ProcessedItems.Should().Be(4);
        tally.AcknowledgedOrRemovedItems.Should().Be(1);
        tally.DocumentScopedFailures.Should().Be(1);
        tally.ConsecutivePagesWithoutAcknowledgement.Should().Be(2);
        tally.LastOutcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
    }

    [Test]
    public void It_resets_the_consecutive_count_when_a_page_acknowledges_work()
    {
        var tally = new RepresentationRestampDrainTally();

        tally.Record(DocumentCacheProjectionDrainPageResult.PageProcessed(1));
        tally.Record(DocumentCacheProjectionDrainPageResult.PageProcessed(1, 1));

        tally.ConsecutivePagesWithoutAcknowledgement.Should().Be(0);
    }

    [Test]
    public void It_describes_an_empty_tally()
    {
        new RepresentationRestampDrainTally()
            .Describe()
            .Should()
            .Be(
                "Drain pages=0 processed=0 acknowledgedOrRemoved=0 documentScopedFailures=0 consecutivePagesWithoutAcknowledgement=0 lastOutcome=none."
            );
    }

    [Test]
    public void It_formats_drain_diagnostics_with_tallies_and_marks_the_own_document()
    {
        var tally = new RepresentationRestampDrainTally();
        tally.Record(DocumentCacheProjectionDrainPageResult.PageProcessed(2, 1, 1, [1]));
        RepresentationRestampE2EResidualWorkRow[] residualWork =
        [
            new(1, _foreignDocument, 1, 2, null, _enqueuedAt),
            new(2, _ownDocument, 4, 4, 4, _enqueuedAt.AddSeconds(45)),
        ];

        string diagnostics = RepresentationRestampE2EHarness.FormatDrainDiagnostics(
            tally,
            residualWork,
            ["DocumentId=1 WorkAnomaly: Cache writer observed work anomaly."],
            _ownDocument
        );

        diagnostics
            .Should()
            .StartWith("Drain pages=1 processed=2 acknowledgedOrRemoved=1 documentScopedFailures=1");
        diagnostics.Should().Contain($"Residual work rows (2, own document {_ownDocument})");
        diagnostics
            .Should()
            .Contain(
                $"[foreign DocumentId=1 DocumentUuid={_foreignDocument} RequiredContentVersion=1 CanonicalContentVersion=2 CacheContentVersion=absent FirstEnqueuedAt=2026-09-11T15:53:45.0000000+00:00]"
            );
        diagnostics
            .Should()
            .Contain(
                $"[own DocumentId=2 DocumentUuid={_ownDocument} RequiredContentVersion=4 CanonicalContentVersion=4 CacheContentVersion=4"
            );
        diagnostics
            .Should()
            .EndWith(
                "Projector failure diagnostics (1): DocumentId=1 WorkAnomaly: Cache writer observed work anomaly.."
            );
    }

    [Test]
    public void It_formats_an_empty_residual_snapshot()
    {
        string diagnostics = RepresentationRestampE2EHarness.FormatDrainDiagnostics(
            new RepresentationRestampDrainTally(),
            [],
            [],
            _ownDocument
        );

        diagnostics.Should().Contain($"Residual work rows (0, own document {_ownDocument}): none.");
        diagnostics.Should().EndWith("Projector failure diagnostics (0): none.");
    }

    [Test]
    public void It_describes_projector_failures_per_document()
    {
        DocumentCacheProjectionDocumentDiagnostic diagnostic = new(
            17,
            DocumentCacheProjectionDocumentDiagnosticCategory.WorkAnomaly,
            "Cache writer observed work anomaly.",
            _enqueuedAt,
            _enqueuedAt.AddSeconds(1)
        );
        DocumentCacheProjectionFailureDiagnostics diagnostics = new(
            effectiveProjectorPageSize: 10,
            failureCount: 1,
            earliestRetryAt: _enqueuedAt.AddSeconds(1),
            evictionCount: 0,
            documentDiagnostics: [diagnostic]
        );

        RepresentationRestampE2EHarness
            .DescribeProjectorFailures(diagnostics)
            .Should()
            .Equal("DocumentId=17 WorkAnomaly: Cache writer observed work anomaly.");
    }

    [Test]
    public async Task It_appends_the_drain_diagnostics_to_the_phase_timeout()
    {
        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunPhaseAsync(
                RepresentationRestampE2EHarness.OrdinaryDrainPhase,
                TimeSpan.FromMilliseconds(25),
                "'':1",
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                () => Task.FromResult("Drain pages=3 acknowledgedOrRemoved=0.")
            );

        TimeoutException exception = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        exception.Message.Should().Contain("phase 'OrdinaryDrain'");
        exception.Message.Should().EndWith("for target '':1. Drain pages=3 acknowledgedOrRemoved=0.");
    }

    [Test]
    public async Task It_reports_a_failing_describer_without_hiding_the_timeout()
    {
        Func<Task> act = async () =>
            await RepresentationRestampE2EHarness.RunPhaseAsync(
                RepresentationRestampE2EHarness.OrdinaryDrainPhase,
                TimeSpan.FromMilliseconds(25),
                "'':1",
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                () => Task.FromException<string>(new InvalidOperationException("database unreachable"))
            );

        TimeoutException exception = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        exception
            .Message.Should()
            .EndWith("Drain diagnostics unavailable: InvalidOperationException: database unreachable");
    }

    [Test]
    public void It_leaves_the_phase_timeout_message_unchanged_without_detail()
    {
        RepresentationRestampE2EHarness
            .PhaseTimeoutMessage(
                RepresentationRestampE2EHarness.OrdinaryDrainPhase,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                "'':1",
                detail: "   "
            )
            .Should()
            .Be(
                "Timed out in representation-restamp phase 'OrdinaryDrain' after 00:00:01 (budget 00:00:02) for target '':1."
            );
    }
}

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Lifecycle_Restore
{
    private const string TargetDescription = "'':1";

    [TestCase("Disabled", DocumentCacheLifecycleState.Disabled)]
    [TestCase("Resetting", DocumentCacheLifecycleState.Resetting)]
    [TestCase("Rebuilding", DocumentCacheLifecycleState.Rebuilding)]
    [TestCase("Tracking", DocumentCacheLifecycleState.Tracking)]
    public void It_parses_each_supported_lifecycle_state_strictly(
        string lifecycleValue,
        DocumentCacheLifecycleState expected
    )
    {
        RepresentationRestampE2EDocumentCacheStateObservation observation =
            RepresentationRestampE2EHarness.ParseDocumentCacheState(lifecycleValue, true, TargetDescription);

        observation.LifecycleState.Should().Be(expected);
        observation.CacheAheadRecoveryRequired.Should().BeTrue();
    }

    [TestCase("tracking")]
    [TestCase("Paused")]
    [TestCase("")]
    [TestCase("1")]
    public void It_rejects_an_unknown_or_blank_lifecycle_value_with_the_capture_phase_named(
        string lifecycleValue
    )
    {
        Action act = () =>
            RepresentationRestampE2EHarness.ParseDocumentCacheState(lifecycleValue, false, TargetDescription);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*phase 'LifecycleCapture'*")
            .WithMessage($"*for target {TargetDescription}*")
            .WithMessage($"*unsupported value '{lifecycleValue}'*");
    }

    [Test]
    public void It_rejects_a_missing_state_row_with_the_capture_phase_named()
    {
        Action act = () =>
            RepresentationRestampE2EHarness.ParseDocumentCacheState(null, null, TargetDescription);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*phase 'LifecycleCapture'*")
            .WithMessage("*no readable singleton row*");
    }

    [Test]
    public void It_rejects_an_unreadable_latch_with_the_capture_phase_named()
    {
        Action act = () =>
            RepresentationRestampE2EHarness.ParseDocumentCacheState(
                "Tracking",
                DBNull.Value,
                TargetDescription
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*phase 'LifecycleCapture'*")
            .WithMessage("*CacheAheadRecoveryRequired is unreadable*");
    }

    [Test]
    public void It_reads_the_latch_from_a_database_boolean()
    {
        RepresentationRestampE2EHarness
            .ParseDocumentCacheState("Disabled", false, TargetDescription)
            .Should()
            .Be(
                new RepresentationRestampE2EDocumentCacheStateObservation(
                    DocumentCacheLifecycleState.Disabled,
                    false
                )
            );
    }

    [TestCase(DocumentCacheLifecycleState.Disabled, false)]
    [TestCase(DocumentCacheLifecycleState.Tracking, false)]
    [TestCase(DocumentCacheLifecycleState.Tracking, true)]
    [TestCase(DocumentCacheLifecycleState.Rebuilding, true)]
    public async Task It_restores_the_observed_lifecycle_and_latch_for_both_modes(
        DocumentCacheLifecycleState lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        // The restore does not depend on the restamp mode: whatever was observed before the first
        // mutation is written back, latch included, so a set latch is never clobbered to false.
        List<(DocumentCacheLifecycleState LifecycleState, bool Latch)> writes = [];

        await RepresentationRestampE2EHarness.RestoreDocumentCacheStateAsync(
            new RepresentationRestampE2EDocumentCacheStateObservation(
                lifecycleState,
                cacheAheadRecoveryRequired
            ),
            (state, latch) =>
            {
                writes.Add((state, latch));
                return Task.CompletedTask;
            }
        );

        writes.Should().Equal((lifecycleState, cacheAheadRecoveryRequired));
    }

    [Test]
    public async Task It_skips_restore_when_nothing_was_observed()
    {
        var wrote = false;

        await RepresentationRestampE2EHarness.RestoreDocumentCacheStateAsync(
            null,
            (_, _) =>
            {
                wrote = true;
                return Task.CompletedTask;
            }
        );

        wrote.Should().BeFalse();
    }
}

[TestFixture]
public sealed class Given_Representation_Restamp_E2E_Harness_Drain_Loop
{
    private const string TargetDescription = "'':1";
    private const string Diagnostics = "Drain pages=1. Residual work rows (1): [foreign DocumentId=1].";

    private static Func<CancellationToken, Task<DocumentCacheProjectionDrainPageResult>> Pages(
        params DocumentCacheProjectionDrainPageResult[] results
    )
    {
        var index = 0;
        return _ =>
        {
            DocumentCacheProjectionDrainPageResult result = results[Math.Min(index, results.Length - 1)];
            index++;
            return Task.FromResult(result);
        };
    }

    private static Func<CancellationToken, Task<bool>> OwnDocumentProjected(params bool[] answers)
    {
        var index = 0;
        return _ =>
        {
            bool answer = answers[Math.Min(index, answers.Length - 1)];
            index++;
            return Task.FromResult(answer);
        };
    }

    private static Task<string> DescribeDiagnostics() => Task.FromResult(Diagnostics);

    private static Task Drain(
        RepresentationRestampDrainTally tally,
        Func<CancellationToken, Task<DocumentCacheProjectionDrainPageResult>> processPageAsync,
        Func<CancellationToken, Task<bool>> isOwnDocumentProjectedAsync,
        int maxConsecutivePagesWithoutAcknowledgement = 20,
        Func<Task<string>>? describeDiagnosticsAsync = null
    ) =>
        RepresentationRestampE2EHarness.DrainUntilOwnWorkProjectedAsync(
            tally,
            processPageAsync,
            isOwnDocumentProjectedAsync,
            describeDiagnosticsAsync ?? DescribeDiagnostics,
            TargetDescription,
            maxConsecutivePagesWithoutAcknowledgement,
            CancellationToken.None
        );

    [Test]
    public void It_uses_a_fixed_named_stall_threshold_of_twenty_pages()
    {
        RepresentationRestampE2EHarness.MaxConsecutivePagesWithoutAcknowledgement.Should().Be(20);
        RepresentationRestampE2EHarness.ResidualWorkReadBudget.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task It_completes_when_the_own_document_is_projected_even_if_foreign_work_remains()
    {
        // The page acknowledged the own row and left a foreign row behind; the drain must not wait
        // for that foreign row.
        var tally = new RepresentationRestampDrainTally();
        var describeCalls = 0;

        await Drain(
            tally,
            Pages(DocumentCacheProjectionDrainPageResult.PageProcessed(2, 1, 1, [1])),
            OwnDocumentProjected(true),
            describeDiagnosticsAsync: () =>
            {
                describeCalls++;
                return DescribeDiagnostics();
            }
        );

        tally.Pages.Should().Be(1);
        describeCalls.Should().Be(0);
    }

    [Test]
    public async Task It_keeps_paging_until_the_own_document_is_projected()
    {
        var tally = new RepresentationRestampDrainTally();

        await Drain(
            tally,
            Pages(
                DocumentCacheProjectionDrainPageResult.PageProcessed(1, 1),
                DocumentCacheProjectionDrainPageResult.PageProcessed(1, 1)
            ),
            OwnDocumentProjected(false, true)
        );

        tally.Pages.Should().Be(2);
    }

    [Test]
    public async Task It_completes_on_no_eligible_work_when_the_own_document_is_projected()
    {
        var tally = new RepresentationRestampDrainTally();

        await Drain(
            tally,
            Pages(DocumentCacheProjectionDrainPageResult.NoEligibleWork),
            OwnDocumentProjected(true)
        );

        tally.LastOutcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.NoEligibleWork);
    }

    [Test]
    public async Task It_fails_with_diagnostics_on_no_eligible_work_when_the_own_document_is_not_projected()
    {
        Func<Task> act = () =>
            Drain(
                new RepresentationRestampDrainTally(),
                Pages(
                    DocumentCacheProjectionDrainPageResult.NoEligibleWorkWithRetry(
                        DateTimeOffset.UtcNow.AddSeconds(1)
                    )
                ),
                OwnDocumentProjected(false)
            );

        InvalidOperationException exception = (
            await act.Should().ThrowAsync<InvalidOperationException>()
        ).Which;
        exception
            .Message.Should()
            .StartWith(
                $"Representation-restamp phase 'OrdinaryDrain' failed for target {TargetDescription}: "
            );
        exception
            .Message.Should()
            .Contain("reported NoEligibleWork but the Tracking restamp's own document is not projected");
        exception.Message.Should().EndWith(Diagnostics);
    }

    [Test]
    public async Task It_fails_fast_after_the_stall_threshold_without_acknowledgements()
    {
        var tally = new RepresentationRestampDrainTally();

        Func<Task> act = () =>
            Drain(
                tally,
                Pages(DocumentCacheProjectionDrainPageResult.PageProcessed(1)),
                OwnDocumentProjected(false),
                maxConsecutivePagesWithoutAcknowledgement: 3
            );

        InvalidOperationException exception = (
            await act.Should().ThrowAsync<InvalidOperationException>()
        ).Which;
        exception.Message.Should().Contain("made no progress: 3 consecutive page(s) acknowledged no work");
        exception.Message.Should().EndWith(Diagnostics);
        tally.Pages.Should().Be(3);
    }

    [Test]
    public async Task It_resets_the_stall_count_when_a_page_acknowledges_work()
    {
        // Two idle pages, one page that acknowledges foreign work, then two more idle pages: the
        // threshold of three is never reached consecutively, so the own-document check decides.
        var tally = new RepresentationRestampDrainTally();

        await Drain(
            tally,
            Pages(
                DocumentCacheProjectionDrainPageResult.PageProcessed(1),
                DocumentCacheProjectionDrainPageResult.PageProcessed(1),
                DocumentCacheProjectionDrainPageResult.PageProcessed(1, 1),
                DocumentCacheProjectionDrainPageResult.PageProcessed(1),
                DocumentCacheProjectionDrainPageResult.PageProcessed(1)
            ),
            OwnDocumentProjected(false, false, false, false, true),
            maxConsecutivePagesWithoutAcknowledgement: 3
        );

        tally.Pages.Should().Be(5);
    }

    private static IEnumerable<TestCaseData> NonPageOutcomes()
    {
        yield return new TestCaseData(
            DocumentCacheProjectionDrainPageResult.TargetBackoff(
                new DateTimeOffset(2026, 9, 11, 16, 0, 0, TimeSpan.Zero)
            )
        ).SetName("It_fails_naming_a_non_page_outcome(TargetBackoff)");
        yield return new TestCaseData(DocumentCacheProjectionDrainPageResult.LifecycleFenced).SetName(
            "It_fails_naming_a_non_page_outcome(LifecycleFenced)"
        );
        yield return new TestCaseData(DocumentCacheProjectionDrainPageResult.TargetPaused(1)).SetName(
            "It_fails_naming_a_non_page_outcome(TargetPaused)"
        );
        yield return new TestCaseData(
            DocumentCacheProjectionDrainPageResult.AdministrativeFailureResult(
                0,
                0,
                0,
                [],
                new DocumentCacheAdministrativeDrainFailure(
                    DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
                    DocumentCacheAdministrativeCommandClassification.TargetNotConfigured,
                    DocumentCacheAdministrativeDiagnosticCategory.TargetNotConfigured,
                    "target not configured",
                    retryable: false
                )
            )
        ).SetName("It_fails_naming_a_non_page_outcome(AdministrativeFailure)");
    }

    [TestCaseSource(nameof(NonPageOutcomes))]
    public async Task It_fails_naming_a_non_page_outcome(DocumentCacheProjectionDrainPageResult result)
    {
        var ownDocumentChecks = 0;

        Func<Task> act = () =>
            Drain(
                new RepresentationRestampDrainTally(),
                Pages(result),
                _ =>
                {
                    ownDocumentChecks++;
                    return Task.FromResult(true);
                }
            );

        InvalidOperationException exception = (
            await act.Should().ThrowAsync<InvalidOperationException>()
        ).Which;
        exception
            .Message.Should()
            .Contain($"ended with outcome '{result.Outcome}' instead of PageProcessed or NoEligibleWork");
        exception.Message.Should().EndWith(Diagnostics);
        ownDocumentChecks.Should().Be(0);
    }

    [Test]
    public async Task It_reports_a_failing_describer_inside_the_drain_failure()
    {
        Func<Task> act = () =>
            Drain(
                new RepresentationRestampDrainTally(),
                Pages(DocumentCacheProjectionDrainPageResult.LifecycleFenced),
                OwnDocumentProjected(true),
                describeDiagnosticsAsync: () =>
                    Task.FromException<string>(new TimeoutException("residual read timed out"))
            );

        InvalidOperationException exception = (
            await act.Should().ThrowAsync<InvalidOperationException>()
        ).Which;
        exception.Message.Should().Contain("ended with outcome 'LifecycleFenced'");
        exception
            .Message.Should()
            .EndWith("Drain diagnostics unavailable: TimeoutException: residual read timed out");
    }

    [Test]
    public async Task It_reports_the_drain_phase_when_the_budget_expires_mid_page()
    {
        var tally = new RepresentationRestampDrainTally();

        Func<Task> act = () =>
            RepresentationRestampE2EHarness.RunPhaseAsync(
                RepresentationRestampE2EHarness.OrdinaryDrainPhase,
                TimeSpan.FromMilliseconds(25),
                TargetDescription,
                cancellationToken =>
                    RepresentationRestampE2EHarness.DrainUntilOwnWorkProjectedAsync(
                        tally,
                        async token =>
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                            return DocumentCacheProjectionDrainPageResult.NoEligibleWork;
                        },
                        OwnDocumentProjected(false),
                        DescribeDiagnostics,
                        TargetDescription,
                        20,
                        cancellationToken
                    ),
                DescribeDiagnostics
            );

        TimeoutException exception = (await act.Should().ThrowAsync<TimeoutException>()).Which;
        exception.Message.Should().Contain("phase 'OrdinaryDrain'");
        exception.Message.Should().EndWith($"for target {TargetDescription}. {Diagnostics}");
        tally.Pages.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_a_non_positive_stall_threshold()
    {
        Func<Task> act = () =>
            Drain(
                new RepresentationRestampDrainTally(),
                Pages(DocumentCacheProjectionDrainPageResult.NoEligibleWork),
                OwnDocumentProjected(true),
                maxConsecutivePagesWithoutAcknowledgement: 0
            );

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
