// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

/// <summary>
/// Exercises the atomic endpoint-override contract on 'ddl provision': --override-host and --override-port
/// must be supplied together, the host must be non-blank, and the port must be in 1-65535. These are validated
/// up front - before any schema load or database connection - so they fail fast with a usage message and a
/// non-zero exit, without needing a live database. (The positive rewrite is covered at the provider level by
/// ConnectionInspectorTests.ApplyEndpointOverride.)
/// </summary>
[TestFixture]
public class DdlProvisionOverrideTests
{
    // A schema path that is never read: the override contract is validated before schema loading.
    private const string UnusedSchemaPath = "does-not-exist-schema.json";
    private const string DummyConnectionString = "Host=dms-postgresql;Database=edfi;Username=postgres";

    private static (int ExitCode, string Output, string Error) RunProvision(params string[] overrideArgs)
    {
        string[] baseArgs =
        [
            "ddl",
            "provision",
            "--schema",
            UnusedSchemaPath,
            "--connection-string",
            DummyConnectionString,
            "--dialect",
            "pgsql",
        ];
        return CliTestHelper.RunCli([.. baseArgs, .. overrideArgs]);
    }

    [TestFixture]
    public class Given_Override_Host_Without_Port : DdlProvisionOverrideTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp() => (_exitCode, _, _error) = RunProvision("--override-host", "localhost");

        [Test]
        public void It_returns_a_usage_error_exit_code()
        {
            _exitCode.Should().NotBe(0);
        }

        [Test]
        public void It_requires_both_options_together()
        {
            _error.Should().Contain("must be supplied together");
        }
    }

    [TestFixture]
    public class Given_Override_Port_Without_Host : DdlProvisionOverrideTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp() => (_exitCode, _, _error) = RunProvision("--override-port", "5432");

        [Test]
        public void It_returns_a_usage_error_exit_code()
        {
            _exitCode.Should().NotBe(0);
        }

        [Test]
        public void It_requires_both_options_together()
        {
            _error.Should().Contain("must be supplied together");
        }
    }

    [TestFixture]
    public class Given_Override_With_Blank_Host : DdlProvisionOverrideTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp() =>
            (_exitCode, _, _error) = RunProvision("--override-host", "", "--override-port", "5432");

        [Test]
        public void It_returns_a_usage_error_exit_code()
        {
            _exitCode.Should().NotBe(0);
        }

        [Test]
        public void It_requires_a_non_blank_host()
        {
            _error.Should().Contain("non-blank host");
        }
    }

    [TestFixture]
    public class Given_Override_With_Port_Below_Range : DdlProvisionOverrideTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp() =>
            (_exitCode, _, _error) = RunProvision("--override-host", "localhost", "--override-port", "0");

        [Test]
        public void It_returns_a_usage_error_exit_code()
        {
            _exitCode.Should().NotBe(0);
        }

        [Test]
        public void It_rejects_the_out_of_range_port()
        {
            _error.Should().Contain("between 1 and 65535");
        }
    }

    [TestFixture]
    public class Given_Override_With_Port_Above_Range : DdlProvisionOverrideTests
    {
        private int _exitCode;
        private string _error = null!;

        [SetUp]
        public void SetUp() =>
            (_exitCode, _, _error) = RunProvision("--override-host", "localhost", "--override-port", "70000");

        [Test]
        public void It_returns_a_usage_error_exit_code()
        {
            _exitCode.Should().NotBe(0);
        }

        [Test]
        public void It_rejects_the_out_of_range_port()
        {
            _error.Should().Contain("between 1 and 65535");
        }
    }

    [TestFixture]
    public class Given_Override_With_A_Provider_Invalid_Connection_Carrying_A_Secret
        : DdlProvisionOverrideTests
    {
        private int _exitCode;
        private string _output = null!;
        private string _error = null!;

        [SetUp]
        public void SetUp()
        {
            // Valid override options, but the connection string carries an unsupported keyword (so the exact
            // provider rejects it) plus a sentinel password. The failure must be controlled - a non-zero exit,
            // an actionable provider diagnostic, no stack trace, and no connection string or password leaked.
            (_exitCode, _output, _error) = CliTestHelper.RunCli(
                "ddl",
                "provision",
                "--schema",
                UnusedSchemaPath,
                "--connection-string",
                "Host=dms-postgresql;Database=edfi;Password=sup3rSecretValue;Bogus=x",
                "--dialect",
                "pgsql",
                "--override-host",
                "localhost",
                "--override-port",
                "5432"
            );
        }

        [Test]
        public void It_fails_with_a_controlled_nonzero_exit()
        {
            _exitCode.Should().NotBe(0);
        }

        [Test]
        public void It_reports_an_actionable_provider_diagnostic()
        {
            _error.Should().Contain("not a valid 'postgresql' connection");
        }

        [Test]
        public void It_does_not_present_a_stack_trace()
        {
            _error.Should().NotContain("at EdFi.DataManagementService");
        }

        [Test]
        public void It_leaks_neither_the_connection_string_nor_the_password()
        {
            _output.Should().NotContain("sup3rSecretValue");
            _error.Should().NotContain("sup3rSecretValue");
        }
    }
}
