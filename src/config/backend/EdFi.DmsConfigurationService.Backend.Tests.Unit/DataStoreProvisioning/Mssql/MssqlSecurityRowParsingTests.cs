// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Mssql;

/// <summary>
/// The security-state rows split as <c>Assert-MssqlArtifactSecurityState</c> splits them: a member row into
/// at most five fields, a permission row into at most nine with the last one kept whole, the numeric fields
/// parsed as integers, blank rows skipped, and any malformed row refusing the whole read.
/// </summary>
public class MssqlSecurityRowParsingTests
{
    private static (
        bool Parsed,
        List<DmsMssqlPermissionRow> Permissions,
        List<DmsMssqlRoleMemberRow> Members
    ) Parse(params string?[] rows)
    {
        bool parsed = MssqlSecurityQueries.TryParseRows(
            rows,
            out List<DmsMssqlPermissionRow> permissions,
            out List<DmsMssqlRoleMemberRow> members
        );
        return (parsed, permissions, members);
    }

    [TestFixture]
    public class Given_well_formed_rows
    {
        private (
            bool Parsed,
            List<DmsMssqlPermissionRow> Permissions,
            List<DmsMssqlRoleMemberRow> Members
        ) _result;

        [SetUp]
        public void Setup() =>
            _result = Parse(
                "member|16384|1|db_owner|dbo",
                "",
                "   ",
                null,
                "permission|0|CONNECT|GRANT|1|0|0|dbo|DATABASE",
                "  permission|1|SELECT|GRANT|0|-385|0|public|OBJECT::sys.objects  ",
                "permission|1|SELECT|GRANT|5|1234|2|odd|name|OBJECT::dms.Document.Doc|Json"
            );

        [Test]
        public void It_parses_every_non_blank_row() => _result.Parsed.Should().BeTrue();

        [Test]
        public void It_reads_the_membership_ids_and_names() =>
            _result.Members.Should().Equal(new DmsMssqlRoleMemberRow(16384, 1, "db_owner", "dbo"));

        [Test]
        public void It_reads_a_database_scope_permission() =>
            _result
                .Permissions[0]
                .Should()
                .Be(new DmsMssqlPermissionRow(0, "CONNECT", "GRANT", 1, 0, 0, "dbo", "DATABASE"));

        [Test]
        public void It_trims_the_row_and_reads_a_negative_object_id() =>
            _result
                .Permissions[1]
                .Should()
                .Be(
                    new DmsMssqlPermissionRow(
                        1,
                        "SELECT",
                        "GRANT",
                        0,
                        -385,
                        0,
                        "public",
                        "OBJECT::sys.objects"
                    )
                );

        [Test]
        public void It_keeps_the_separator_inside_the_last_field() =>
            (_result.Permissions[2].Grantee, _result.Permissions[2].Securable)
                .Should()
                .Be(("odd", "name|OBJECT::dms.Document.Doc|Json"));
    }

    [TestFixture]
    public class Given_malformed_rows
    {
        private Dictionary<string, bool> _parsed = [];

        [SetUp]
        public void Setup()
        {
            string[] rows =
            [
                "member|16384|1|db_owner",
                "member|x|1|db_owner|dbo",
                "member|16384||db_owner|dbo",
                "permission|0|CONNECT|GRANT|1|0|0|dbo",
                "permission|zero|CONNECT|GRANT|1|0|0|dbo|DATABASE",
                "permission|0|CONNECT|GRANT|one|0|0|dbo|DATABASE",
                "permission|0|CONNECT|GRANT|1|0.5|0|dbo|DATABASE",
                "permission|0|CONNECT|GRANT|1|0|99999999999|dbo|DATABASE",
                "Permission|0|CONNECT|GRANT|1|0|0|dbo|DATABASE",
                "grant|0|CONNECT|GRANT|1|0|0|dbo|DATABASE",
            ];
            _parsed = rows.ToDictionary(row => row, row => Parse("member|16384|1|db_owner|dbo", row).Parsed);
        }

        [Test]
        public void It_refuses_every_malformed_row() =>
            _parsed.Should().AllSatisfy(pair => pair.Value.Should().BeFalse(pair.Key));

        [Test]
        public void It_covers_each_malformation() => _parsed.Count.Should().Be(10);
    }
}
