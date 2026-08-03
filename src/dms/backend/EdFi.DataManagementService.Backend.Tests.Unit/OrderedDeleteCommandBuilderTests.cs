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
    [Test]
    public void It_builds_the_pgsql_regular_resource_delete_returning_the_deleted_document_id()
    {
        var command = OrderedDeleteCommandBuilder.BuildResourceDeleteByDocumentIdCommand(
            SqlDialect.Pgsql,
            new DbTableName(new DbSchemaName("edfi"), "School"),
            123L
        );

        command
            .CommandText.Should()
            .Be(
                """
                DELETE FROM "edfi"."School"
                WHERE "DocumentId" = @documentId
                RETURNING "DocumentId";
                """
            );
        command.CommandText.Should().NotContain("dms.\"Document\"");

        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@documentId");
        command.Parameters[0].Value.Should().Be(123L);
    }

    [Test]
    public void It_builds_the_mssql_regular_resource_delete_outputting_the_deleted_document_id()
    {
        var command = OrderedDeleteCommandBuilder.BuildResourceDeleteByDocumentIdCommand(
            SqlDialect.Mssql,
            new DbTableName(new DbSchemaName("edfi"), "School"),
            123L
        );

        // A plain OUTPUT is illegal on the trigger-bearing root table, so the deleted id lands in a table
        // variable and a trailing SELECT exposes it as the affected-rows signal.
        command
            .CommandText.Should()
            .Be(
                """
                DECLARE @deletedDocumentId TABLE ([DocumentId] bigint);

                DELETE FROM [edfi].[School]
                OUTPUT DELETED.[DocumentId] INTO @deletedDocumentId
                WHERE [DocumentId] = @documentId;

                SELECT [DocumentId] FROM @deletedDocumentId;
                """
            );
        command.CommandText.Should().NotContain("[dms].[Document]");

        command.Parameters.Should().ContainSingle();
        command.Parameters[0].Name.Should().Be("@documentId");
        command.Parameters[0].Value.Should().Be(123L);
    }

    [TestCase(
        SqlDialect.Pgsql,
        "DELETE FROM dms.\"Descriptor\"",
        "\"DocumentUuid\" = @documentUuid",
        "\"ResourceKeyId\" = @resourceKeyId",
        "dms.\"Document\"",
        "RETURNING \"DocumentId\""
    )]
    [TestCase(
        SqlDialect.Mssql,
        "DELETE FROM [dms].[Descriptor]",
        "[DocumentUuid] = @documentUuid",
        "[ResourceKeyId] = @resourceKeyId",
        "[dms].[Document]",
        "OUTPUT DELETED.[DocumentId] INTO @deletedDocumentId"
    )]
    public void It_builds_the_descriptor_delete_returning_the_deleted_document_id(
        SqlDialect dialect,
        string descriptorDeleteFragment,
        string documentUuidPredicateFragment,
        string resourceKeyIdPredicateFragment,
        string documentTableFragment,
        string returnedIdFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        const short ResourceKeyId = 101;

        var command = OrderedDeleteCommandBuilder.BuildDescriptorDeleteCommand(
            dialect,
            documentUuid,
            ResourceKeyId
        );

        // The descriptor delete predicates directly on the descriptor row's own uuid and ResourceKeyId
        // mirrors, and its own returned row is the affected-rows signal — dms.Document is not read or
        // written.
        command.CommandText.Should().Contain(descriptorDeleteFragment);
        command.CommandText.Should().Contain(documentUuidPredicateFragment);
        command.CommandText.Should().Contain(resourceKeyIdPredicateFragment);
        command.CommandText.Should().Contain(returnedIdFragment);
        command.CommandText.Should().NotContain(documentTableFragment);

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
}
