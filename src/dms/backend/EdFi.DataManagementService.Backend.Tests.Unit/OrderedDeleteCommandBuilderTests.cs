// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_OrderedDeleteCommandBuilder
{
    [TestCase(
        SqlDialect.Pgsql,
        "DELETE FROM \"edfi\".\"School\"",
        "DELETE FROM dms.\"Document\"",
        "RETURNING \"DocumentId\""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "DELETE FROM [edfi].[School]",
        "DELETE FROM [dms].[Document]",
        "OUTPUT DELETED.[DocumentId]"
    )]
    public void It_builds_regular_resource_delete_command_with_root_delete_before_document_delete(
        SqlDialect dialect,
        string rootDeleteFragment,
        string documentDeleteFragment,
        string finalResultFragment
    )
    {
        var command = OrderedDeleteCommandBuilder.BuildResourceDeleteByDocumentIdCommand(
            dialect,
            new DbTableName(new DbSchemaName("edfi"), "School"),
            123L
        );

        AssertContainsInOrder(command.CommandText, rootDeleteFragment, documentDeleteFragment);
        var finalStatement = SplitStatements(command)[^1];
        finalStatement.Should().Contain(documentDeleteFragment);
        finalStatement.Should().Contain(finalResultFragment);

        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@documentId");
        command.Parameters[0].Value.Should().Be(123L);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "DELETE FROM dms.\"Descriptor\"",
        "DELETE FROM dms.\"Document\"",
        "RETURNING \"DocumentId\""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "DELETE FROM [dms].[Descriptor]",
        "DELETE FROM [dms].[Document]",
        "OUTPUT DELETED.[DocumentId]"
    )]
    public void It_builds_descriptor_delete_command_with_descriptor_delete_before_document_delete(
        SqlDialect dialect,
        string descriptorDeleteFragment,
        string documentDeleteFragment,
        string finalResultFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        const short ResourceKeyId = 101;

        var command = OrderedDeleteCommandBuilder.BuildDescriptorDeleteCommand(
            dialect,
            documentUuid,
            ResourceKeyId
        );

        AssertContainsInOrder(command.CommandText, descriptorDeleteFragment, documentDeleteFragment);
        var finalStatement = SplitStatements(command)[^1];
        finalStatement.Should().Contain(documentDeleteFragment);
        finalStatement.Should().Contain(finalResultFragment);

        command.Parameters.Should().HaveCount(2);
        command
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal("@documentUuid", "@resourceKeyId");
        command.Parameters[0].Value.Should().Be(documentUuid.Value);
        command.Parameters[1].Value.Should().Be(ResourceKeyId);
    }

    [TestCase(SqlDialect.Pgsql, "ed\"fi", "Sch\"ool", "DELETE FROM \"ed\"\"fi\".\"Sch\"\"ool\"")]
    [TestCase(SqlDialect.Mssql, "ed]fi", "Sch]ool", "DELETE FROM [ed]]fi].[Sch]]ool]")]
    public void It_escapes_regular_resource_table_identifiers_for_the_selected_dialect(
        SqlDialect dialect,
        string schemaName,
        string tableName,
        string expectedTableFragment
    )
    {
        var command = OrderedDeleteCommandBuilder.BuildResourceDeleteByDocumentIdCommand(
            dialect,
            new DbTableName(new DbSchemaName(schemaName), tableName),
            123L
        );

        command.CommandText.Should().Contain(expectedTableFragment);
    }

    private static string[] SplitStatements(RelationalCommand command) =>
        command.CommandText.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    private static void AssertContainsInOrder(string commandText, string firstFragment, string secondFragment)
    {
        commandText.Should().Contain(firstFragment);
        commandText.Should().Contain(secondFragment);
        commandText
            .IndexOf(firstFragment, StringComparison.Ordinal)
            .Should()
            .BeLessThan(commandText.IndexOf(secondFragment, StringComparison.Ordinal));
    }
}
