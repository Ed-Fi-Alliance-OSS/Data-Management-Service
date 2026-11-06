// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The environment allowlist of D-10 and the <c>libpq</c> view of the administrative connection: the
/// inherited environment is discarded, exactly the allowlisted names are set, the password never becomes a
/// value, and every admitted connection-string key reaches <c>psql</c> through an argument or a variable.
/// </summary>
public class LibpqEnvironmentTests
{
    private const string Password = "SENTINEL-PASSWORD-7f3a";

    private const string ConnectionString =
        "Host=db.example.test;Port=5433;Username=dms_admin;Password="
        + Password
        + ";SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/root.crt;Timeout=20";

    [TestFixture]
    public class Given_a_full_connection_applied_to_a_start_info
    {
        private const string PasswordFilePath = "/tmp/dms-provision-0123456789ab/pgpass";
        private const string ApplicationName = "dms-provision:0123456789ab";

        private string _inheritedVariable = null!;
        private ProcessStartInfo _startInfo = null!;

        [SetUp]
        public void Setup()
        {
            _inheritedVariable = "DMS1438_INHERITED_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(_inheritedVariable, "would leak");
            _startInfo = new ProcessStartInfo("psql");
            _startInfo
                .Environment.Should()
                .ContainKey(
                    _inheritedVariable,
                    "the start info inherits this process's environment before Apply"
                );

            LibpqConnectionSettings connection = LibpqConnectionSettings.From(
                new NpgsqlConnectionStringBuilder(ConnectionString)
            );
            LibpqEnvironment.Apply(_startInfo, connection, PasswordFilePath, ApplicationName);
        }

        [TearDown]
        public void TearDown() => Environment.SetEnvironmentVariable(_inheritedVariable, null);

        [Test]
        public void It_sets_exactly_the_allowlisted_variables() =>
            _startInfo.Environment.Keys.Should().BeEquivalentTo(LibpqEnvironment.AllowedVariables);

        [Test]
        public void It_drops_the_inherited_environment() =>
            _startInfo.Environment.Should().NotContainKey(_inheritedVariable);

        [Test]
        public void It_copies_the_path_of_this_process() =>
            _startInfo.Environment["PATH"].Should().Be(Environment.GetEnvironmentVariable("PATH"));

        [Test]
        public void It_names_the_password_file() =>
            _startInfo.Environment["PGPASSFILE"].Should().Be(PasswordFilePath);

        [Test]
        public void It_names_the_operation_as_the_application() =>
            _startInfo.Environment["PGAPPNAME"].Should().Be(ApplicationName);

        [Test]
        public void It_spells_the_ssl_mode_as_libpq_does() =>
            _startInfo.Environment["PGSSLMODE"].Should().Be("verify-full");

        [Test]
        public void It_passes_the_root_certificate() =>
            _startInfo.Environment["PGSSLROOTCERT"].Should().Be("/etc/ssl/certs/root.crt");

        [Test]
        public void It_passes_the_connect_timeout_in_seconds() =>
            _startInfo.Environment["PGCONNECT_TIMEOUT"].Should().Be("20");

        [Test]
        public void It_fixes_the_locale_and_the_client_encoding()
        {
            _startInfo.Environment["LC_ALL"].Should().Be("C");
            _startInfo.Environment["PGCLIENTENCODING"].Should().Be("UTF8");
        }

        [Test]
        public void It_never_carries_the_password() =>
            _startInfo.Environment.Values.Should().NotContain(value => value!.Contains(Password));
    }

    [TestFixture]
    public class Given_a_connection_without_a_root_certificate
    {
        private ProcessStartInfo _startInfo = null!;

        [SetUp]
        public void Setup()
        {
            _startInfo = new ProcessStartInfo("psql");
            LibpqConnectionSettings connection = LibpqConnectionSettings.From(
                new NpgsqlConnectionStringBuilder("Host=db.example.test;Username=dms_admin;Password=x")
            );
            LibpqEnvironment.Apply(_startInfo, connection, "/tmp/pgpass", "dms-provision:0123456789ab");
        }

        [Test]
        public void It_omits_the_root_certificate_variable() =>
            _startInfo.Environment.Should().NotContainKey("PGSSLROOTCERT");

        [Test]
        public void It_sets_every_other_allowlisted_variable() =>
            _startInfo
                .Environment.Keys.Should()
                .BeEquivalentTo(LibpqEnvironment.AllowedVariables.Where(name => name != "PGSSLROOTCERT"));

        [Test]
        public void It_uses_the_npgsql_defaults_for_the_mode_and_the_timeout()
        {
            _startInfo.Environment["PGSSLMODE"].Should().Be("prefer");
            _startInfo.Environment["PGCONNECT_TIMEOUT"].Should().Be("15");
        }
    }

    [TestFixture]
    public class Given_no_connection_as_the_version_probe_applies_it
    {
        private ProcessStartInfo _startInfo = null!;

        [SetUp]
        public void Setup()
        {
            _startInfo = new ProcessStartInfo("psql");
            LibpqEnvironment.Apply(
                _startInfo,
                connection: null,
                passwordFilePath: null,
                applicationName: null
            );
        }

        [Test]
        public void It_sets_only_the_fixed_variables() =>
            _startInfo.Environment.Keys.Should().BeEquivalentTo("PATH", "LC_ALL", "PGCLIENTENCODING");
    }

    [TestFixture]
    public class Given_every_npgsql_ssl_mode
    {
        private Dictionary<SslMode, string> _names = null!;

        [SetUp]
        public void Setup() =>
            _names = Enum.GetValues<SslMode>()
                .ToDictionary(mode => mode, LibpqConnectionSettings.SslModeName);

        [Test]
        public void It_spells_each_mode_as_libpq_does() =>
            _names
                .Should()
                .BeEquivalentTo(
                    new Dictionary<SslMode, string>
                    {
                        [SslMode.Disable] = "disable",
                        [SslMode.Allow] = "allow",
                        [SslMode.Prefer] = "prefer",
                        [SslMode.Require] = "require",
                        [SslMode.VerifyCA] = "verify-ca",
                        [SslMode.VerifyFull] = "verify-full",
                    }
                );
    }

    [TestFixture]
    public class Given_the_libpq_view_of_a_multi_host_connection
    {
        private LibpqConnectionSettings _connection = null!;

        [SetUp]
        public void Setup() =>
            _connection = LibpqConnectionSettings.From(
                new NpgsqlConnectionStringBuilder(
                    "Host=primary.example.test, standby.example.test;Username=dms_admin;Password=" + Password
                )
            );

        [Test]
        public void It_lists_each_host_trimmed_and_in_order() =>
            _connection.Hosts.Should().Equal("primary.example.test", "standby.example.test");

        [Test]
        public void It_keeps_the_host_value_for_the_command_line() =>
            _connection.Host.Should().Be("primary.example.test, standby.example.test");

        [Test]
        public void It_prints_only_its_type_name() =>
            _connection.ToString().Should().Be(nameof(LibpqConnectionSettings));

        [Test]
        public void It_defaults_a_missing_root_certificate_to_null() =>
            _connection.RootCertificate.Should().BeNull();
    }

    [TestFixture]
    public class Given_connection_strings_libpq_could_not_be_handed
    {
        private Exception? _withoutHost;
        private Exception? _withoutUser;

        [SetUp]
        public void Setup()
        {
            _withoutHost = Packaging.Record.Exception(() =>
                LibpqConnectionSettings.From(
                    new NpgsqlConnectionStringBuilder("Username=dms_admin;Password=x")
                )
            );
            _withoutUser = Packaging.Record.Exception(() =>
                LibpqConnectionSettings.From(
                    new NpgsqlConnectionStringBuilder("Host=db.example.test;Password=x")
                )
            );
        }

        [Test]
        public void It_refuses_a_missing_host() =>
            _withoutHost.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_refuses_a_missing_user() =>
            _withoutUser.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_never_quotes_the_password_in_the_refusal()
        {
            _withoutHost!.Message.Should().NotContain("x");
            _withoutUser!.Message.Should().NotContain("x");
        }
    }
}
