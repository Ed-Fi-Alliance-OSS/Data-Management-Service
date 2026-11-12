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
/// value, every admitted connection-string key reaches <c>psql</c> through an argument or a variable, and
/// Npgsql's <c>Host</c> syntax is normalized into the host and port lists <c>libpq</c> takes.
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
            _startInfo.Environment["PGPASSWORD"] = Password;
            _startInfo.Environment["PGSERVICE"] = "operator-service";
            _startInfo.Environment["PSQLRC"] = "/tmp/operator-psqlrc";

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
        public void It_drops_password_and_service_variables_set_before_apply() =>
            _startInfo.Environment.Keys.Should().NotContain(["PGPASSWORD", "PGSERVICE", "PSQLRC"]);

        [Test]
        public void It_copies_the_system_root_on_windows_only()
        {
            if (OperatingSystem.IsWindows())
            {
                string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                systemRoot.Should().NotBeNullOrEmpty("a Windows process always has SystemRoot");
                _startInfo.Environment["SystemRoot"].Should().Be(systemRoot);
            }
            else
            {
                _startInfo.Environment.Should().NotContainKey("SystemRoot");
            }
        }

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
        public void It_names_a_revocation_list_beneath_the_password_file_so_no_home_directory_list_is_loaded() =>
            _startInfo.Environment["PGSSLCRL"].Should().Be(Path.Combine(PasswordFilePath, "absent"));

        [Test]
        public void It_sends_no_client_certificate() =>
            _startInfo.Environment["PGSSLCERTMODE"].Should().Be("disable");

        // libpq prefers GSS encryption to TLS whenever Kerberos credentials are usable, regardless of sslmode
        // (verify-full included); Npgsql 8 never negotiates it.
        [Test]
        public void It_disables_gss_encryption_so_the_configured_tls_verification_applies() =>
            _startInfo.Environment["PGGSSENCMODE"].Should().Be("disable");

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
        private const string PasswordFilePath = "/tmp/pgpass";

        private ProcessStartInfo _startInfo = null!;

        [SetUp]
        public void Setup()
        {
            _startInfo = new ProcessStartInfo("psql");
            LibpqConnectionSettings connection = LibpqConnectionSettings.From(
                new NpgsqlConnectionStringBuilder("Host=db.example.test;Username=dms_admin;Password=x")
            );
            LibpqEnvironment.Apply(_startInfo, connection, PasswordFilePath, "dms-provision:0123456789ab");
        }

        // libpq treats an unset or empty PGSSLROOTCERT alike and falls back to the home directory's root.crt,
        // so only an explicit path keeps an unrelated file from turning prefer or require into verification.
        [Test]
        public void It_points_the_root_certificate_beneath_the_password_file() =>
            _startInfo.Environment["PGSSLROOTCERT"].Should().Be(Path.Combine(PasswordFilePath, "absent"));

        [Test]
        public void It_sets_exactly_the_allowlisted_variables() =>
            _startInfo.Environment.Keys.Should().BeEquivalentTo(LibpqEnvironment.AllowedVariables);

        [Test]
        public void It_uses_the_npgsql_defaults_for_the_mode_and_the_timeout()
        {
            _startInfo.Environment["PGSSLMODE"].Should().Be("prefer");
            _startInfo.Environment["PGCONNECT_TIMEOUT"].Should().Be("15");
        }
    }

    /// <summary>The path the root certificate and revocation list default to cannot hold a file while the password file exists.</summary>
    [TestFixture]
    public class Given_the_absent_file_path_beneath_an_existing_password_file
    {
        private bool _exists;
        private Exception? _creation;

        [SetUp]
        public void Setup()
        {
            string passwordFile = Path.GetTempFileName();
            try
            {
                string absent = LibpqEnvironment.AbsentFilePath(passwordFile);
                _exists = File.Exists(absent) || Directory.Exists(absent);
                _creation = Packaging.Record.Exception(() => File.WriteAllText(absent, "unrelated root"));
            }
            finally
            {
                File.Delete(passwordFile);
            }
        }

        [Test]
        public void It_does_not_exist() => _exists.Should().BeFalse();

        [Test]
        public void It_cannot_be_created() => _creation.Should().BeAssignableTo<IOException>();
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
            _startInfo
                .Environment.Keys.Should()
                .BeEquivalentTo(
                    OperatingSystem.IsWindows()
                        ? ["PATH", "LC_ALL", "PGCLIENTENCODING", "SystemRoot"]
                        : ["PATH", "LC_ALL", "PGCLIENTENCODING"]
                );
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
    public class Given_host_values_in_npgsql_syntax
    {
        private const int DefaultPort = 5432;

        private Dictionary<string, IReadOnlyList<LibpqEndpoint>> _endpoints = null!;
        private LibpqConnectionSettings _twoEndpoints = null!;

        [SetUp]
        public void Setup()
        {
            string[] values =
            [
                "db.example.test",
                "db.example.test:5433",
                " primary.example.test:5433 , standby.example.test ",
                "::1",
                "[::1]:5433",
                "[fe80::1]",
                "2001:db8::1,db.example.test:5433",
                "/var/run/postgresql",
                "/var/run:5433",
            ];
            _endpoints = values.ToDictionary(
                value => value,
                value => LibpqConnectionSettings.ParseEndpoints(value, DefaultPort)
            );
            _twoEndpoints = LibpqConnectionSettings.From(
                new NpgsqlConnectionStringBuilder(
                    "Host=primary.example.test:5433,[::1];Port=5434;Username=dms_admin;Password=" + Password
                )
            );
        }

        [Test]
        public void It_keeps_a_plain_host_on_the_default_port() =>
            _endpoints["db.example.test"].Should().Equal(new LibpqEndpoint("db.example.test", DefaultPort));

        [Test]
        public void It_splits_a_single_host_with_its_own_port_as_npgsql_does() =>
            _endpoints["db.example.test:5433"].Should().Equal(new LibpqEndpoint("db.example.test", 5433));

        [Test]
        public void It_trims_entries_and_gives_each_its_own_or_the_default_port() =>
            _endpoints[" primary.example.test:5433 , standby.example.test "]
                .Should()
                .Equal(
                    new LibpqEndpoint("primary.example.test", 5433),
                    new LibpqEndpoint("standby.example.test", DefaultPort)
                );

        [Test]
        public void It_never_splits_a_bare_ipv6_address()
        {
            _endpoints["::1"].Should().Equal(new LibpqEndpoint("::1", DefaultPort));
            _endpoints["2001:db8::1,db.example.test:5433"]
                .Should()
                .Equal(
                    new LibpqEndpoint("2001:db8::1", DefaultPort),
                    new LibpqEndpoint("db.example.test", 5433)
                );
        }

        [Test]
        public void It_strips_ipv6_brackets_and_takes_the_port_after_them()
        {
            _endpoints["[::1]:5433"].Should().Equal(new LibpqEndpoint("::1", 5433));
            _endpoints["[fe80::1]"].Should().Equal(new LibpqEndpoint("fe80::1", DefaultPort));
        }

        [Test]
        public void It_never_splits_a_lone_unix_socket_directory()
        {
            _endpoints["/var/run/postgresql"]
                .Should()
                .Equal(new LibpqEndpoint("/var/run/postgresql", DefaultPort));
            _endpoints["/var/run:5433"].Should().Equal(new LibpqEndpoint("/var/run:5433", DefaultPort));
        }

        [Test]
        public void It_joins_the_hosts_and_the_ports_into_parallel_lists()
        {
            _twoEndpoints.HostArgument.Should().Be("primary.example.test,::1");
            _twoEndpoints.PortArgument.Should().Be("5433,5434");
        }

        [Test]
        public void It_prints_only_its_type_name() =>
            _twoEndpoints.ToString().Should().Be(nameof(LibpqConnectionSettings));

        [Test]
        public void It_defaults_a_missing_root_certificate_to_null() =>
            _twoEndpoints.RootCertificate.Should().BeNull();
    }

    [TestFixture]
    public class Given_host_values_libpq_could_not_be_handed
    {
        private Dictionary<string, Exception?> _refusals = null!;
        private Exception? _withoutHost;
        private Exception? _withoutUser;

        [SetUp]
        public void Setup()
        {
            string[] values = ["db:abc", "db:", "db:70000", "db:0", "a,,b", "a,", "[::1]:", "db: 5433"];
            _refusals = values.ToDictionary(
                value => value,
                value => Packaging.Record.Exception(() => LibpqConnectionSettings.ParseEndpoints(value, 5432))
            );
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
        public void It_refuses_every_malformed_port_and_empty_entry() =>
            _refusals
                .Values.Should()
                .AllSatisfy(exception => exception.Should().BeOfType<InvalidOperationException>());

        [Test]
        public void It_refuses_a_missing_host() =>
            _withoutHost.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_refuses_a_missing_user() =>
            _withoutUser.Should().BeOfType<InvalidOperationException>();

        [Test]
        public void It_never_quotes_a_value_in_the_refusal()
        {
            _withoutHost!.Message.Should().NotContain("x");
            _withoutUser!.Message.Should().NotContain("x");
            _refusals.Values.Should().AllSatisfy(exception => exception!.Message.Should().NotContain("db"));
        }
    }

    /// <summary>
    /// Server-certificate trust through the administrative connection, which both clients read: a relative
    /// <c>Root Certificate</c> reaches <c>psql</c> as the full path Npgsql's certificate check reads, resolved in
    /// this process's working directory, because <c>psql</c> runs in the operation's directory; and a verifying mode without one is
    /// refused, because Npgsql would then trust sources the replay does not see. A mode that does not verify is
    /// unchanged without one, and refused with one (review finding 2): <c>libpq</c> would verify against the file
    /// where Npgsql verifies nothing, so an unrelated server certificate would pass readiness and fail every replay.
    /// That both clients then trust the same certificates is shown by handshakes in
    /// <see cref="PostgresqlAdminConnectionTests.Given_a_verifying_connection_to_a_server_presenting_a_certificate"/>.
    /// </summary>
    [TestFixture]
    public class Given_the_server_certificate_trust_of_the_admin_connection
    {
        private const string Base = "Host=db;Username=dms_admin;Password=" + Password + ";";

        private string _expectedRoot = string.Empty;
        private string? _libpqRoot;
        private Dictionary<string, Exception?> _verifyingWithoutRoot = [];
        private Exception? _requireWithoutRoot;
        private Dictionary<string, Exception?> _rootWithoutVerifying = [];

        [SetUp]
        public void Setup()
        {
            string relative = Path.Combine("certs", "root.crt");
            _expectedRoot = Path.GetFullPath(relative);
            PostgresqlAdminConnection connection = new(
                Base + "SSL Mode=VerifyFull;Root Certificate=" + relative,
                TimeSpan.FromSeconds(30)
            );
            _libpqRoot = connection.ToLibpqSettings().RootCertificate;

            _verifyingWithoutRoot = new[] { "VerifyCA", "VerifyFull" }.ToDictionary(
                mode => mode,
                mode =>
                    Packaging.Record.Exception(() =>
                        new PostgresqlAdminConnection(Base + "SSL Mode=" + mode, TimeSpan.FromSeconds(30))
                    )
            );
            _requireWithoutRoot = Packaging.Record.Exception(() =>
                new PostgresqlAdminConnection(Base + "SSL Mode=Require", TimeSpan.FromSeconds(30))
            );

            // Rotated in after a first use, so the rule holds for every value the source returns, not only the first.
            _rootWithoutVerifying = new[] { "Require", "Prefer", "Allow", "Disable", null }.ToDictionary(
                mode => mode ?? "(absent)",
                mode =>
                {
                    string current = Base + "SSL Mode=VerifyCA;Root Certificate=/etc/ca.crt";
                    PostgresqlAdminConnection rotating = new(() => current, TimeSpan.FromSeconds(30));
                    _ = rotating.ToLibpqSettings();
                    current =
                        Base
                        + (mode is null ? string.Empty : "SSL Mode=" + mode + ";")
                        + "Root Certificate=/etc/ca.crt";
                    return Packaging.Record.Exception(() => rotating.ToLibpqSettings());
                }
            );
        }

        [Test]
        public void It_hands_psql_the_full_root_certificate_path() => _libpqRoot.Should().Be(_expectedRoot);

        [Test]
        public void It_refuses_a_verifying_mode_without_a_root_certificate() =>
            _verifyingWithoutRoot
                .Values.Should()
                .AllSatisfy(exception =>
                    exception
                        .Should()
                        .BeOfType<InvalidOperationException>()
                        .Which.Message.Should()
                        .NotContain(Password)
                );

        [Test]
        public void It_leaves_a_mode_that_does_not_verify_alone() => _requireWithoutRoot.Should().BeNull();

        [Test]
        public void It_refuses_a_root_certificate_with_a_mode_that_does_not_verify()
        {
            _rootWithoutVerifying.Should().HaveCount(5);
            _rootWithoutVerifying
                .Values.Should()
                .AllSatisfy(exception =>
                    exception
                        .Should()
                        .BeOfType<InvalidOperationException>()
                        .Which.Message.Should()
                        .Contain("VerifyCA or VerifyFull")
                        .And.NotContain(Password)
                );
        }
    }
}
