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
public class Given_RelationalReadTargetLookupService
{
    private static readonly DbTableName _studentRootTable = new(new DbSchemaName("edfi"), "Student");

    /// <summary>
    /// The GET-by-id target probe is a single seek on the resource root table's
    /// <c>UX_&lt;Root&gt;_DocumentUuid</c> unique index. The route already names the resource, so the
    /// probe is resource-scoped by construction: a uuid belonging to another resource is simply
    /// absent from this root table.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_probes_the_resource_root_table_by_document_uuid(SqlDialect dialect)
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor(
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])],
            dialect
        );
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        await sut.ResolveForGetByIdAsync(_studentRootTable, documentUuid);

        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Be(ExpectedProbeSql(dialect));
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .Equal("@documentUuid");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_existing_document_with_the_probed_content_version(SqlDialect dialect)
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor(
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(
                            ("DocumentId", 404L),
                            ("DocumentUuid", documentUuid.Value),
                            ("ContentVersion", 907L)
                        )
                    ),
                ]),
            ],
            dialect
        );
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        var result = await sut.ResolveForGetByIdAsync(_studentRootTable, documentUuid);

        result
            .Should()
            .BeEquivalentTo(new RelationalReadTargetLookupResult.ExistingDocument(404L, documentUuid, 907L));
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_not_found_when_the_root_table_has_no_row_for_the_document_uuid(
        SqlDialect dialect
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor(
            [new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()])],
            dialect
        );
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        var result = await sut.ResolveForGetByIdAsync(_studentRootTable, documentUuid);

        result.Should().BeOfType<RelationalReadTargetLookupResult.NotFound>();
    }

    /// <summary>
    /// <c>UX_&lt;Root&gt;_DocumentUuid</c> makes a second row impossible, but the defensive read stays:
    /// a multi-row probe result means the index is missing or corrupt, and that must fail loudly
    /// rather than silently serve an arbitrary row.
    /// </summary>
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_throws_when_the_root_table_probe_returns_more_than_one_row(SqlDialect dialect)
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor(
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(
                            ("DocumentId", 404L),
                            ("DocumentUuid", documentUuid.Value),
                            ("ContentVersion", 907L)
                        ),
                        RelationalAccessTestData.CreateRow(
                            ("DocumentId", 405L),
                            ("DocumentUuid", documentUuid.Value),
                            ("ContentVersion", 908L)
                        )
                    ),
                ]),
            ],
            dialect
        );
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        var act = async () => await sut.ResolveForGetByIdAsync(_studentRootTable, documentUuid);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage(
                $"*edfi.Student*{documentUuid.Value}*",
                "the defensive multi-row read must name the probed root table and document uuid"
            );
    }

    private static string ExpectedProbeSql(SqlDialect dialect) =>
        dialect switch
        {
            SqlDialect.Pgsql => """
                SELECT
                    root."DocumentId" AS "DocumentId",
                    root."DocumentUuid" AS "DocumentUuid",
                    root."ContentVersion" AS "ContentVersion"
                FROM "edfi"."Student" root
                WHERE root."DocumentUuid" = @documentUuid
                """,
            SqlDialect.Mssql => """
                SELECT
                    root.[DocumentId] AS [DocumentId],
                    root.[DocumentUuid] AS [DocumentUuid],
                    root.[ContentVersion] AS [ContentVersion]
                FROM [edfi].[Student] root
                WHERE root.[DocumentUuid] = @documentUuid
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
}
