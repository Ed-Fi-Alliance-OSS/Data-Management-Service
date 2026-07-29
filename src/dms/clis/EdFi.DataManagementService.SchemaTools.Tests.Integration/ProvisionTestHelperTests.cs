// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
public class Given_ProvisionTestHelper_When_Building_Sqlcmd_Arguments
{
    [Test]
    public void It_uses_the_script_file_and_fail_fast_sqlcmd_options()
    {
        var args = ProvisionTestHelper.BuildSqlcmdArguments(
            "Server=localhost,14333;Initial Catalog=dms_test;User Id=sa;Password=Secret1!;TrustServerCertificate=true",
            "/tmp/mssql.sql"
        );

        args.Should()
            .ContainInOrder(
                "-S",
                "localhost,14333",
                "-d",
                "dms_test",
                "-b",
                "-I",
                "-r",
                "1",
                "-i",
                "/tmp/mssql.sql"
            );
        args.Should().ContainInOrder("-U", "sa", "-P", "Secret1!");
        args.Should().Contain("-C");
        args.Should().NotContain("-E");
    }

    [Test]
    public void It_uses_trusted_authentication_without_password_arguments()
    {
        var args = ProvisionTestHelper.BuildSqlcmdArguments(
            "Server=.;Database=dms_test;Integrated Security=true",
            "/tmp/mssql.sql"
        );

        args.Should().Contain("-E");
        args.Should().NotContain("-U");
        args.Should().NotContain("-P");
    }
}
