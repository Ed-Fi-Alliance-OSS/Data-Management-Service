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
/// The password file of D-10: <c>libpq</c> escaping of <c>:</c> and <c>\</c>, one line per host, created
/// with no sharing and mode <c>0600</c> inside the operation directory, and deleted on request.
/// </summary>
public class PgPassFileTests
{
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
            _line = PgPassFile.FormatLine(
                "db:host",
                5433,
                "edfi_dms_provision_stage_0123456789ab",
                @"dom\user",
                @"p:a\ss"
            );
            _lineBreak = Record.Exception(() => PgPassFile.Escape("line\nbreak"));
        }

        [Test]
        public void It_prefixes_colons_and_backslashes_with_a_backslash() => _escaped.Should().Be(@"a\:b\\c");

        [Test]
        public void It_joins_the_five_fields_with_colons_escaping_each_text_field() =>
            _line.Should().Be(@"db\:host:5433:edfi_dms_provision_stage_0123456789ab:dom\\user:p\:a\\ss");

        [Test]
        public void It_refuses_a_field_with_a_line_break() =>
            _lineBreak.Should().BeOfType<ArgumentException>();
    }

    [TestFixture]
    public class Given_a_password_file_written_for_a_two_host_connection
    {
        private const string Password = "SENTINEL-PASSWORD-7f3a";

        private string _root = null!;
        private PrivateOperationDirectory _directory = null!;
        private string _path = null!;
        private byte[] _bytes = null!;
        private UnixFileMode? _mode;
        private Exception? _secondWrite;

        [SetUp]
        public async Task Setup()
        {
            _root = Path.Combine(Path.GetTempPath(), "dms-1438-pgpass-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _directory = PrivateOperationDirectory.Create(_root, DmsOperationId.New());
            LibpqConnectionSettings connection = LibpqConnectionSettings.From(
                new NpgsqlConnectionStringBuilder(
                    "Host=primary.example.test,standby.example.test;Port=5433;Username=dms_admin;Password="
                        + Password
                )
            );

            _path = await PgPassFile.WriteAsync(
                _directory,
                connection,
                "edfi_dms_provision_stage_0123456789ab",
                CancellationToken.None
            );
            _bytes = await File.ReadAllBytesAsync(_path);
            if (!OperatingSystem.IsWindows())
            {
                _mode = File.GetUnixFileMode(_path);
            }

            _secondWrite = await Record.ExceptionAsync(() =>
                PgPassFile.WriteAsync(_directory, connection, "other", CancellationToken.None)
            );
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
        public void It_holds_one_line_per_host_with_the_password_last() =>
            System
                .Text.Encoding.UTF8.GetString(_bytes)
                .Should()
                .Be(
                    "primary.example.test:5433:edfi_dms_provision_stage_0123456789ab:dms_admin:"
                        + Password
                        + "\n"
                        + "standby.example.test:5433:edfi_dms_provision_stage_0123456789ab:dms_admin:"
                        + Password
                        + "\n"
                );

        [Test]
        public void It_writes_no_byte_order_mark() => _bytes[0].Should().Be((byte)'p');

        [Test]
        public void It_refuses_to_overwrite_an_existing_file() =>
            _secondWrite.Should().BeOfType<IOException>();

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
}
