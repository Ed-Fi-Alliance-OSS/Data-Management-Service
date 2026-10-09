// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.SchemaTools.Commands;
using EdFi.DataManagementService.SchemaTools.Restamping;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_SchemaRestamp_Command
{
    private const string ConnectionString = "Host=localhost;Database=restamp_target";
    private IApiSchemaFileLoader _fileLoader = null!;
    private EffectiveSchemaSetBuilder _schemaSetBuilder = null!;
    private ISchemaRestamper _restamper = null!;
    private StringWriter _output = null!;
    private StringWriter _error = null!;

    [SetUp]
    public void Setup()
    {
        _fileLoader = new ApiSchemaFileLoader(
            new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance),
            NullLogger<ApiSchemaFileLoader>.Instance
        );
        _schemaSetBuilder = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );
        _restamper = A.Fake<ISchemaRestamper>();
        _output = new StringWriter();
        _error = new StringWriter();
    }

    [Test]
    public async Task It_reports_a_committed_transition_after_service_success()
    {
        A.CallTo(() =>
                _restamper.RestampAsync(
                    SqlDialect.Pgsql,
                    ConnectionString,
                    300,
                    A<EffectiveSchemaInfo>._,
                    false,
                    A<CancellationToken>._
                )
            )
            .Returns(new SchemaRestampResult(true, new string('a', 64), new string('b', 64), 1));

        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "-s",
            MinimalSchemaPath,
            "-c",
            ConnectionString,
            "-d",
            "pgsql"
        );

        exitCode.Should().Be(0);
        _output.ToString().Should().Contain("Effective schema re-stamp committed.");
        _output.ToString().Should().Contain(new string('b', 64));
        _output.ToString().Should().Contain("provision");
        _error.ToString().Should().BeEmpty();
        A.CallTo(() =>
                _restamper.RestampAsync(
                    SqlDialect.Pgsql,
                    ConnectionString,
                    300,
                    A<EffectiveSchemaInfo>._,
                    false,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task It_reports_a_valid_match_without_confirmation()
    {
        A.CallTo(() =>
                _restamper.RestampAsync(
                    SqlDialect.Pgsql,
                    ConnectionString,
                    300,
                    A<EffectiveSchemaInfo>._,
                    false,
                    A<CancellationToken>._
                )
            )
            .Returns(new SchemaRestampResult(false, new string('a', 64), new string('a', 64), 1));

        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql"
        );

        exitCode.Should().Be(0);
        _output.ToString().Should().Contain("Already stamped; no changes.");
        _error.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task It_passes_the_computed_target_and_confirmation_token_to_the_service()
    {
        List<string> targetHashes = [];
        A.CallTo(() =>
                _restamper.RestampAsync(
                    SqlDialect.Pgsql,
                    ConnectionString,
                    45,
                    A<EffectiveSchemaInfo>._,
                    true,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (SqlDialect _, string _, int _, EffectiveSchemaInfo target, bool _, CancellationToken _) =>
                {
                    targetHashes.Add(target.EffectiveSchemaHash);
                    return Task.FromResult(
                        new SchemaRestampResult(
                            false,
                            target.EffectiveSchemaHash,
                            target.EffectiveSchemaHash,
                            0
                        )
                    );
                }
            );

        var firstExitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql",
            "--timeout",
            "45",
            "--migration-completed"
        );
        var secondExitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalAlternateSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql",
            "--timeout",
            "45",
            "--migration-completed"
        );

        firstExitCode.Should().Be(0);
        secondExitCode.Should().Be(0);
        targetHashes.Should().HaveCount(2);
        targetHashes[0].Should().NotBe(targetHashes[1]);
        A.CallTo(() =>
                _restamper.RestampAsync(
                    SqlDialect.Pgsql,
                    ConnectionString,
                    45,
                    A<EffectiveSchemaInfo>._,
                    true,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedTwiceExactly();
    }

    [Test]
    public async Task It_propagates_cancellation_to_the_service()
    {
        var serviceEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        A.CallTo(() =>
                _restamper.RestampAsync(
                    A<SqlDialect>._,
                    A<string>._,
                    A<int>._,
                    A<EffectiveSchemaInfo>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(
                (SqlDialect _, string _, int _, EffectiveSchemaInfo _, bool _, CancellationToken token) =>
                {
                    serviceEntered.TrySetResult();
                    var completion = new TaskCompletionSource<SchemaRestampResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    token.Register(() => completion.TrySetCanceled(token));
                    return completion.Task;
                }
            );

        var invocation = InvokeAsync(
            cancellation.Token,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql"
        );
        await serviceEntered.Task;
        await cancellation.CancelAsync();
        var exitCode = await invocation;

        exitCode.Should().Be(1);
        _error.ToString().Should().Contain("cancelled");
    }

    [Test]
    public async Task It_contains_provider_error_text()
    {
        const string secret = "provider-secret-sentinel";
        A.CallTo(() =>
                _restamper.RestampAsync(
                    A<SqlDialect>._,
                    A<string>._,
                    A<int>._,
                    A<EffectiveSchemaInfo>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .Throws(new InvalidOperationException(secret));

        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql",
            "--verbose"
        );

        exitCode.Should().Be(1);
        (_output.ToString() + _error.ToString()).Should().NotContain(secret);
        _error.ToString().Should().Contain("InvalidOperationException");
    }

    [Test]
    public async Task It_rejects_nonpositive_timeout_before_the_service()
    {
        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql",
            "--timeout",
            "0"
        );

        exitCode.Should().Be(1);
        _error.ToString().Should().Contain("positive");
        A.CallTo(() =>
                _restamper.RestampAsync(
                    A<SqlDialect>._,
                    A<string>._,
                    A<int>._,
                    A<EffectiveSchemaInfo>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [TestCase("Host=localhost", "pgsql")]
    [TestCase("Host=localhost;Database=", "pgsql")]
    [TestCase("Host=localhost;Database=restamp_target;invalid", "pgsql")]
    [TestCase("Server=localhost", "mssql")]
    public async Task It_rejects_a_missing_or_empty_database_before_the_service(
        string connectionString,
        string dialect
    )
    {
        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            connectionString,
            "--dialect",
            dialect,
            "--migration-completed"
        );

        exitCode.Should().Be(1);
        A.CallTo(() =>
                _restamper.RestampAsync(
                    A<SqlDialect>._,
                    A<string>._,
                    A<int>._,
                    A<EffectiveSchemaInfo>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
        (_output.ToString() + _error.ToString()).Should().NotContain("localhost");
    }

    [Test]
    public async Task It_does_not_echo_values_from_parser_errors()
    {
        const string sentinel = "credential-sentinel";

        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql",
            "--unknown-option",
            sentinel
        );

        exitCode.Should().Be(1);
        (_output.ToString() + _error.ToString()).Should().NotContain(sentinel);
    }

    [Test]
    public async Task It_does_not_echo_control_characters_in_schema_paths()
    {
        const string hostilePath = "missing\u001b[31m-schema.json";

        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            hostilePath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql"
        );

        exitCode.Should().Be(1);
        (_output.ToString() + _error.ToString()).Should().NotContain("\u001b");
        (_output.ToString() + _error.ToString()).Should().NotContain(hostilePath);
    }

    [TestCase("--raw-hash")]
    [TestCase("--create-database")]
    [TestCase("--force")]
    [TestCase("--managed-cdc")]
    public async Task It_rejects_out_of_scope_options(string unsupportedOption)
    {
        var exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "--schema",
            MinimalSchemaPath,
            "--connection-string",
            ConnectionString,
            "--dialect",
            "pgsql",
            unsupportedOption
        );

        exitCode.Should().Be(1);
        A.CallTo(() =>
                _restamper.RestampAsync(
                    A<SqlDialect>._,
                    A<string>._,
                    A<int>._,
                    A<EffectiveSchemaInfo>._,
                    A<bool>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    private async Task<int> InvokeAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var command = DdlRestampCommand.Create(
            NullLogger.Instance,
            _fileLoader,
            _schemaSetBuilder,
            _restamper,
            _output,
            _error
        );
        var root = new RootCommand();
        root.Options.Add(new Option<bool>("--verbose") { Recursive = true });
        var ddl = new Command("ddl");
        ddl.Subcommands.Add(command);
        root.Subcommands.Add(ddl);
        var parseResult = root.Parse(arguments);
        return await DdlRestampCommand.InvokeAsync(parseResult, _output, _error, cancellationToken);
    }

    private static string MinimalSchemaPath
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "src",
                    "dms",
                    "clis",
                    "EdFi.DataManagementService.SchemaTools.Tests.Integration",
                    "Fixtures",
                    "minimal-api-schema.json"
                );
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("The minimal schema fixture could not be located.");
        }
    }

    private static string MinimalAlternateSchemaPath => FindRepositoryFile("minimal-api-schema-alt.json");

    private static string FindRepositoryFile(string fileName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                current.FullName,
                "src",
                "dms",
                "clis",
                "EdFi.DataManagementService.SchemaTools.Tests.Integration",
                "Fixtures",
                fileName
            );
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The schema fixture could not be located.");
    }
}
