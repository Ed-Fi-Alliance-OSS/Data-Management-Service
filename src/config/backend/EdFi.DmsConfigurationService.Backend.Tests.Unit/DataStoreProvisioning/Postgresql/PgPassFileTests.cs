// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;
using EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Postgresql;

/// <summary>
/// The password file of D-10: <c>libpq</c> escaping of <c>:</c> and <c>\</c>, one line per endpoint with
/// that endpoint's port, created with no sharing and mode <c>0600</c> inside the operation directory, an
/// existing file refused untouched, a file created by a write that then fails deleted again, and deletion
/// on request.
/// </summary>
public class PgPassFileTests
{
    private const string Password = "SENTINEL-PASSWORD-7f3a";
    private const string Database = "edfi_dms_provision_stage_0123456789ab";

    private static LibpqConnectionSettings TwoEndpoints() =>
        LibpqConnectionSettings.From(
            new NpgsqlConnectionStringBuilder(
                "Host=primary.example.test:5433,[::1];Port=5434;Username=dms_admin;Password=" + Password
            )
        );

    [TestFixture]
    public class Given_fields_with_the_characters_libpq_reserves
    {
        private string _escaped = null!;
        private string _line = null!;
        private Exception? _lineBreak;

        [SetUp]
        public void Setup()
        {
            _escaped = PgPassFile.Escape(@"a:b\c");
            _line = PgPassFile.FormatLine("db:host", 5433, Database, @"dom\user", @"p:a\ss");
            _lineBreak = Record.Exception(() => PgPassFile.Escape("line\nbreak"));
        }

        [Test]
        public void It_prefixes_colons_and_backslashes_with_a_backslash() => _escaped.Should().Be(@"a\:b\\c");

        [Test]
        public void It_joins_the_five_fields_with_colons_escaping_each_text_field() =>
            _line.Should().Be(@"db\:host:5433:" + Database + @":dom\\user:p\:a\\ss");

        [Test]
        public void It_refuses_a_field_with_a_line_break() =>
            _lineBreak.Should().BeOfType<ArgumentException>();
    }

    /// <summary>
    /// Review finding 3: <c>libpq</c> looks a socket in its compiled-in default directory up as <c>localhost</c>,
    /// and any other directory under its own path. A socket endpoint therefore gets both lines, with its own port
    /// and the one configured user and password; a network endpoint keeps exactly its one line.
    /// </summary>
    [TestFixture]
    public class Given_the_lines_for_unix_socket_and_network_endpoints
    {
        private string _directorySocket = null!;
        private string _abstractSocket = null!;
        private string _network = null!;

        private static string ContentFor(string connectionString) =>
            PgPassFile.Content(
                LibpqConnectionSettings.From(new NpgsqlConnectionStringBuilder(connectionString)),
                Database
            );

        [SetUp]
        public void Setup()
        {
            _directorySocket = ContentFor(
                "Host=/var/run/postgresql;Port=5433;Username=dms_admin;Password=" + Password
            );
            _abstractSocket = ContentFor("Host=@pgsocket;Username=dms_admin;Password=" + Password);
            _network = PgPassFile.Content(TwoEndpoints(), Database);
        }

        [Test]
        public void It_writes_the_directory_line_then_a_localhost_line_with_the_same_port() =>
            _directorySocket
                .Should()
                .Be(
                    "/var/run/postgresql:5433:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                        + "localhost:5433:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                );

        [Test]
        public void It_treats_an_abstract_socket_the_same_way() =>
            _abstractSocket
                .Should()
                .Be(
                    "@pgsocket:5432:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                        + "localhost:5432:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                );

        [Test]
        public void It_adds_nothing_for_network_endpoints() =>
            _network
                .Should()
                .Be(
                    "primary.example.test:5433:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                        + @"\:\:1:5434:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                );
    }

    [TestFixture]
    public class Given_a_password_file_written_for_two_endpoints
    {
        private string _root = null!;
        private PrivateOperationDirectory _directory = null!;
        private string _path = null!;
        private byte[] _bytes = null!;
        private byte[] _bytesAfterRefusedWrite = null!;
        private UnixFileMode? _mode;
        private Exception? _secondWrite;

        [SetUp]
        public async Task Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "dms-1438-pgpass-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _directory = PrivateOperationDirectory.Create(_root, DmsOperationId.New());

            _path = await PgPassFile.WriteAsync(_directory, TwoEndpoints(), Database, CancellationToken.None);
            _bytes = await File.ReadAllBytesAsync(_path);
            if (!OperatingSystem.IsWindows())
            {
                _mode = File.GetUnixFileMode(_path);
            }

            _secondWrite = await Record.ExceptionAsync(() =>
                PgPassFile.WriteAsync(_directory, TwoEndpoints(), "other", CancellationToken.None)
            );
            _bytesAfterRefusedWrite = await File.ReadAllBytesAsync(_path);
        }

        [TearDown]
        public void TearDown()
        {
            _directory.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void It_is_named_pgpass_inside_the_operation_directory() =>
            _path.Should().Be(Path.Combine(_directory.Path, "pgpass"));

        [Test]
        public void It_holds_one_line_per_endpoint_with_its_own_port_and_the_password_last() =>
            System
                .Text.Encoding.UTF8.GetString(_bytes)
                .Should()
                .Be(
                    "primary.example.test:5433:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                        + @"\:\:1:5434:"
                        + Database
                        + ":dms_admin:"
                        + Password
                        + "\n"
                );

        [Test]
        public void It_writes_no_byte_order_mark() => _bytes[0].Should().Be((byte)'p');

        [Test]
        public void It_refuses_to_overwrite_an_existing_file_and_leaves_it_as_it_was()
        {
            _secondWrite.Should().BeOfType<IOException>();
            _bytesAfterRefusedWrite.Should().Equal(_bytes);
        }

        [Test]
        [Platform(Exclude = "Win")]
        public void It_is_readable_and_writable_by_the_owner_only() =>
            _mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        [Test]
        public void It_can_be_deleted_and_deleting_again_still_reports_it_gone()
        {
            PgPassFile.TryDelete(_path).Should().BeTrue();
            File.Exists(_path).Should().BeFalse();
            PgPassFile.TryDelete(_path).Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_writes_that_fail_after_the_file_was_created
    {
        private string _root = null!;
        private PrivateOperationDirectory _directory = null!;
        private Exception? _cancelled;
        private bool _existedAfterCancellation;
        private Exception? _writeFailed;
        private bool _existedAfterWriteFailure;
        private bool _fileSeenByTheSeam;
        private string _pathAfterwards = null!;

        [SetUp]
        public async Task Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "dms-1438-pgpass-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _directory = PrivateOperationDirectory.Create(_root, DmsOperationId.New());
            string path = _directory.PathOf(PgPassFile.FileName);

            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();
            _cancelled = await Record.ExceptionAsync(() =>
                PgPassFile.WriteAsync(_directory, TwoEndpoints(), Database, cancellation.Token)
            );
            _existedAfterCancellation = File.Exists(path);

            _writeFailed = await Record.ExceptionAsync(() =>
                PgPassFile.WriteAsync(
                    _directory,
                    TwoEndpoints(),
                    Database,
                    stream =>
                    {
                        _fileSeenByTheSeam = File.Exists(stream.Name);
                        throw new IOException("The disk is full.");
                    },
                    CancellationToken.None
                )
            );
            _existedAfterWriteFailure = File.Exists(path);

            _pathAfterwards = await PgPassFile.WriteAsync(
                _directory,
                TwoEndpoints(),
                Database,
                CancellationToken.None
            );
        }

        [TearDown]
        public void TearDown()
        {
            _directory.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void It_deletes_the_file_when_cancellation_arrives_after_creation()
        {
            _cancelled.Should().BeAssignableTo<OperationCanceledException>();
            _existedAfterCancellation.Should().BeFalse();
        }

        [Test]
        public void It_deletes_the_file_when_the_write_itself_fails()
        {
            _fileSeenByTheSeam.Should().BeTrue("the seam runs after the file exists");
            _writeFailed.Should().BeOfType<IOException>();
            _existedAfterWriteFailure.Should().BeFalse();
        }

        [Test]
        public void It_leaves_no_residue_that_would_refuse_the_next_write() =>
            File.Exists(_pathAfterwards).Should().BeTrue();
    }
}
