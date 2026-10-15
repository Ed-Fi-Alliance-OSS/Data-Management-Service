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

public abstract class DdlRestampCommandTests
{
    protected const string ConnectionString = "Host=localhost;Database=restamp_target";
    protected IApiSchemaFileLoader _fileLoader = null!;
    protected EffectiveSchemaSetBuilder _schemaSetBuilder = null!;
    protected ISchemaRestamper _restamper = null!;
    protected StringWriter _output = null!;
    protected StringWriter _error = null!;

    [SetUp]
    public void Initialize()
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

    [TestFixture]
    public sealed class Given_Committed_Transition : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() =>
                    _restamper.RestampAsync(
                        SqlDialect.Pgsql,
                        ConnectionString,
                        300,
                        A<EffectiveSchemaInfo>._,
                        true,
                        A<CancellationToken>._
                    )
                )
                .Returns(new SchemaRestampResult(true, new string('a', 64), new string('b', 64), 1));

            _exitCode = await InvokeAsync(
                CancellationToken.None,
                "ddl",
                "re-stamp",
                "-s",
                MinimalSchemaPath,
                "-c",
                ConnectionString,
                "-d",
                "pgsql",
                "--migration-completed"
            );
        }

        [Test]
        public void It_returns_success()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_the_commit()
        {
            _output.ToString().Should().Contain("Effective schema re-stamp committed.");
        }

        [Test]
        public void It_prints_the_target_hash()
        {
            _output.ToString().Should().Contain(new string('b', 64));
        }

        [Test]
        public void It_advises_a_provision_followup()
        {
            _output.ToString().Should().Contain("provision");
        }

        [Test]
        public void It_writes_no_error()
        {
            _error.ToString().Should().BeEmpty();
        }

        [Test]
        public void It_invokes_the_service_once()
        {
            A.CallTo(() =>
                    _restamper.RestampAsync(
                        SqlDialect.Pgsql,
                        ConnectionString,
                        300,
                        A<EffectiveSchemaInfo>._,
                        true,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture("Host=localhost;Database=restamp_target", "pgsql", SqlDialect.Pgsql)]
    [TestFixture("Server=localhost;Database=restamp_target", "mssql", SqlDialect.Mssql)]
    [TestFixture("Server=localhost;Initial Catalog=restamp_target", "mssql", SqlDialect.Mssql)]
    public sealed class Given_Matching_Stamp(string connectionString, string dialectName, SqlDialect dialect)
        : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() =>
                    _restamper.RestampAsync(
                        dialect,
                        connectionString,
                        300,
                        A<EffectiveSchemaInfo>._,
                        false,
                        A<CancellationToken>._
                    )
                )
                .Returns(new SchemaRestampResult(false, new string('a', 64), new string('a', 64), 1));

            _exitCode = await InvokeAsync(
                CancellationToken.None,
                "ddl",
                "re-stamp",
                "--schema",
                MinimalSchemaPath,
                "--connection-string",
                connectionString,
                "--dialect",
                dialectName
            );
        }

        [Test]
        public void It_returns_success()
        {
            _exitCode.Should().Be(0);
        }

        [Test]
        public void It_reports_no_changes()
        {
            _output.ToString().Should().Contain("Already stamped; no changes.");
        }

        [Test]
        public void It_prints_the_target_hash_for_comparison_with_runtime_preflight()
        {
            _output.ToString().Should().Contain($"Target effective schema hash: {new string('a', 64)}");
        }

        [Test]
        public void It_writes_no_error()
        {
            _error.ToString().Should().BeEmpty();
        }
    }

    [TestFixture]
    public sealed class Given_Different_Target_Inputs : DdlRestampCommandTests
    {
        private int _firstExitCode;
        private int _secondExitCode;
        private List<string> _targetHashes = [];

        [SetUp]
        public async Task Setup()
        {
            _targetHashes = [];
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
                    (
                        SqlDialect _,
                        string _,
                        int _,
                        EffectiveSchemaInfo target,
                        bool _,
                        CancellationToken _
                    ) =>
                    {
                        _targetHashes.Add(target.EffectiveSchemaHash);
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

            _firstExitCode = await InvokeAsync(
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
            _secondExitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_accepts_the_first_target()
        {
            _firstExitCode.Should().Be(0);
        }

        [Test]
        public void It_accepts_the_second_target()
        {
            _secondExitCode.Should().Be(0);
        }

        [Test]
        public void It_computes_two_targets()
        {
            _targetHashes.Should().HaveCount(2);
        }

        [Test]
        public void It_varies_the_hash_with_schema_input()
        {
            _targetHashes[0].Should().NotBe(_targetHashes[1]);
        }

        [Test]
        public void It_passes_the_timeout_and_confirmation()
        {
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
    }

    [TestFixture]
    public sealed class Given_Cancelled_Invocation : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
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
            _exitCode = await invocation;
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_reports_cancellation()
        {
            _error.ToString().Should().Contain("cancelled");
        }
    }

    [TestFixture]
    public sealed class Given_Unexpected_Service_Error : DdlRestampCommandTests
    {
        private int _exitCode;
        private const string secret = "provider-secret-sentinel";

        [SetUp]
        public async Task Setup()
        {
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

            _exitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_redacts_provider_details()
        {
            (_output.ToString() + _error.ToString()).Should().NotContain(secret);
        }

        [Test]
        public void It_identifies_the_exception_category()
        {
            _error.ToString().Should().Contain("InvalidOperationException");
        }
    }

    [TestFixture]
    public sealed class Given_Nonpositive_Timeout : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
        {
            _exitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_explains_the_timeout_requirement()
        {
            _error.ToString().Should().Contain("positive");
        }

        [Test]
        public void It_does_not_invoke_the_service()
        {
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
    }

    [TestFixture("Host=localhost", "pgsql")]
    [TestFixture("Host=localhost;Database=", "pgsql")]
    [TestFixture("Host=localhost;Database=restamp_target;invalid", "pgsql")]
    [TestFixture("Server=localhost", "mssql")]
    [TestFixture("Server=localhost;Initial Catalog=", "mssql")]
    [TestFixture("Server=localhost;Database=restamp_target;invalid", "mssql")]
    public sealed class Given_Invalid_Database_Option(string connectionString, string dialect)
        : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
        {
            _fileLoader = A.Fake<IApiSchemaFileLoader>();
            _exitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_does_not_load_schema_inputs()
        {
            A.CallTo(() => _fileLoader.Load(A<string>._, A<IReadOnlyList<string>>._)).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_invoke_the_service()
        {
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

        [Test]
        public void It_keeps_connection_details_private()
        {
            (_output.ToString() + _error.ToString()).Should().NotContain("localhost");
        }
    }

    [TestFixture]
    public sealed class Given_Unknown_Parser_Token : DdlRestampCommandTests
    {
        private int _exitCode;
        private const string sentinel = "credential-sentinel";

        [SetUp]
        public async Task Setup()
        {
            _exitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_keeps_unknown_values_private()
        {
            (_output.ToString() + _error.ToString()).Should().NotContain(sentinel);
        }
    }

    [TestFixture("--dialect", "credential-sentinel", "pgsql or mssql")]
    [TestFixture("-d", "credential-sentinel", "pgsql or mssql")]
    [TestFixture("--dialect", "", "pgsql or mssql")]
    [TestFixture("--timeout", "credential-sentinel", "integer")]
    [TestFixture("-t", "credential-sentinel", "integer")]
    [TestFixture("--timeout", "", "integer")]
    public sealed class Given_Invalid_Parser_Option(string option, string value, string expectedGuidance)
        : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
        {
            List<string> arguments =
            [
                "ddl",
                "re-stamp",
                "--schema",
                MinimalSchemaPath,
                "--connection-string",
                ConnectionString,
            ];
            if (option is not ("--dialect" or "-d"))
            {
                arguments.AddRange(["--dialect", "pgsql"]);
            }
            arguments.Add(option);
            if (value.Length > 0)
            {
                arguments.Add(value);
            }
            _exitCode = await InvokeAsync(CancellationToken.None, arguments.ToArray());
        }

        [Test]
        public void It_returns_failure() => _exitCode.Should().Be(1);

        [Test]
        public void It_identifies_the_failed_option_and_expected_input() =>
            _error.ToString().Should().Contain(option).And.Contain(expectedGuidance);

        [Test]
        public void It_does_not_misreport_supplied_required_options_as_missing() =>
            _error.ToString().Should().NotContain("Missing required").And.NotContain("Required options:");

        [Test]
        public void It_keeps_argument_values_private() =>
            (_output.ToString() + _error.ToString()).Should().NotContain("credential-sentinel");
    }

    [TestFixture]
    public sealed class Given_Hostile_Schema_Path : DdlRestampCommandTests
    {
        private int _exitCode;
        private const string hostilePath = "missing\u001b[31m-schema.json";

        [SetUp]
        public async Task Setup()
        {
            _exitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_strips_terminal_control_characters()
        {
            (_output.ToString() + _error.ToString()).Should().NotContain("\u001b");
        }

        [Test]
        public void It_does_not_echo_the_raw_path()
        {
            (_output.ToString() + _error.ToString()).Should().NotContain(hostilePath);
        }
    }

    [TestFixture("--raw-hash")]
    [TestFixture("--create-database")]
    [TestFixture("--force")]
    [TestFixture("--managed-cdc")]
    public sealed class Given_Unsupported_Option(string unsupportedOption) : DdlRestampCommandTests
    {
        private int _exitCode;

        [SetUp]
        public async Task Setup()
        {
            _exitCode = await InvokeAsync(
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
        }

        [Test]
        public void It_returns_failure()
        {
            _exitCode.Should().Be(1);
        }

        [Test]
        public void It_does_not_invoke_the_service()
        {
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
    }

    protected async Task<int> InvokeAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        var command = DdlRestampCommand.Create(_fileLoader, _schemaSetBuilder, _restamper, _output, _error);
        var root = new RootCommand();
        root.Options.Add(new Option<bool>("--verbose") { Recursive = true });
        var ddl = new Command("ddl");
        ddl.Subcommands.Add(command);
        root.Subcommands.Add(ddl);
        var parseResult = root.Parse(arguments);
        return await DdlRestampCommand.InvokeAsync(parseResult, _output, _error, cancellationToken);
    }

    protected static string MinimalSchemaPath => FindRepositoryFile("minimal-api-schema.json");

    protected static string MinimalAlternateSchemaPath => FindRepositoryFile("minimal-api-schema-alt.json");

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

[TestFixture("missing", "Schema file not found: schema[31m.json")]
[TestFixture(
    "unreadable",
    "Schema file could not be read: schema[31m.json. Check access and file permissions."
)]
[TestFixture("json", "Invalid JSON in schema file: schema[31m.json.")]
[TestFixture("project", "Schema project metadata is missing or malformed.")]
[TestFixture("version", "Schema input format versions do not match.")]
[TestFixture("collision", "Schema project endpoint names collide.")]
public sealed class Given_SchemaRestamp_Input_Failure(string kind, string expectedDiagnostic)
    : DdlRestampCommandTests
{
    private int _exitCode;

    [SetUp]
    public async Task Setup()
    {
        const string path = "schema\u001b[31m\r\n\t.json";
        const string privateDetail = "private-detail-sentinel";
        ApiSchemaFileLoadResult result = kind switch
        {
            "missing" => new ApiSchemaFileLoadResult.FileNotFoundResult(path),
            "unreadable" => new ApiSchemaFileLoadResult.FileReadErrorResult(path, privateDetail),
            "json" => new ApiSchemaFileLoadResult.InvalidJsonResult(path, privateDetail),
            "project" => new ApiSchemaFileLoadResult.NormalizationFailureResult(
                new ApiSchemaNormalizationResult.MissingOrMalformedProjectSchemaResult(
                    privateDetail,
                    privateDetail
                )
            ),
            "version" => new ApiSchemaFileLoadResult.NormalizationFailureResult(
                new ApiSchemaNormalizationResult.ApiSchemaVersionMismatchResult(
                    privateDetail,
                    privateDetail,
                    privateDetail
                )
            ),
            _ => new ApiSchemaFileLoadResult.NormalizationFailureResult(
                new ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult([
                    new(privateDetail, [privateDetail]),
                ])
            ),
        };
        _fileLoader = A.Fake<IApiSchemaFileLoader>();
        A.CallTo(() => _fileLoader.Load(A<string>._, A<IReadOnlyList<string>>._)).Returns(result);
        _exitCode = await InvokeAsync(
            CancellationToken.None,
            "ddl",
            "re-stamp",
            "-s",
            path,
            "-c",
            ConnectionString,
            "-d",
            "pgsql",
            "--verbose"
        );
    }

    [Test]
    public void It_reports_failure() => _exitCode.Should().Be(1);

    [Test]
    public void It_reports_only_the_typed_sanitized_diagnostic() =>
        _error.ToString().Should().Be($"{expectedDiagnostic}{Environment.NewLine}");

    [Test]
    public void It_writes_no_output() => _output.ToString().Should().BeEmpty();

    [Test]
    public void It_sanitizes_paths() => _error.ToString().Should().NotContain("\u001b");

    [Test]
    public void It_keeps_raw_loader_details_private() =>
        _error.ToString().Should().NotContain("private-detail-sentinel");
}

[TestFixture]
public sealed class Given_SchemaRestamp_Build_Failure : DdlRestampCommandTests
{
    private int _exitCode;

    [SetUp]
    public async Task Setup()
    {
        var hashProvider = A.Fake<IEffectiveSchemaHashProvider>();
        A.CallTo(() => hashProvider.ComputeHash(A<string>._, A<IReadOnlyList<ProjectSchemaMetadata>>._))
            .Throws(new InvalidOperationException("private-detail-sentinel"));
        _schemaSetBuilder = new EffectiveSchemaSetBuilder(
            hashProvider,
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );
        _exitCode = await InvokeAsync(
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
    }

    [Test]
    public void It_reports_failure() => _exitCode.Should().Be(1);

    [Test]
    public void It_names_the_failed_stage() =>
        _error.ToString().Should().Contain("Target schema metadata could not be built");

    [Test]
    public void It_keeps_raw_builder_details_private() =>
        _error.ToString().Should().NotContain("private-detail-sentinel");
}

[TestFixture]
public sealed class Given_SchemaRestamp_Missing_Options : DdlRestampCommandTests
{
    [SetUp]
    public async Task Setup() => await InvokeAsync(CancellationToken.None, "ddl", "re-stamp");

    [Test]
    public void It_names_the_required_schema_option() => _error.ToString().Should().Contain("--schema");

    [Test]
    public void It_names_the_required_connection_option() =>
        _error.ToString().Should().Contain("--connection-string");

    [Test]
    public void It_names_the_required_dialect_option() => _error.ToString().Should().Contain("--dialect");

    [Test]
    public void It_points_to_help() => _error.ToString().Should().Contain("--help");
}
