// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public partial class Given_DescriptorReadHandler
{
    private static string PageSelectionPagingFragment(SqlDialect dialect) =>
        dialect == SqlDialect.Mssql ? "FETCH NEXT" : "LIMIT @";

    private static DescriptorReadCommandExecutor CreateLimitZeroExecutor(
        SqlDialect dialect,
        bool totalCount
    ) =>
        new(
            [
                new InMemoryRelationalCommandExecution(
                    totalCount
                        ?
                        [
                            InMemoryRelationalResultSet.Create(
                                RelationalAccessTestData.CreateRow(("TotalCount", 7L))
                            ),
                            InMemoryRelationalResultSet.Create(),
                        ]
                        : [InMemoryRelationalResultSet.Create()]
                ),
            ],
            dialect
        );

    [TestCase(SqlDialect.Mssql)]
    [TestCase(SqlDialect.Pgsql)]
    public async Task It_omits_page_selection_from_the_descriptor_query_command_for_a_zero_limit(
        SqlDialect dialect
    )
    {
        var commandExecutor = CreateLimitZeroExecutor(dialect, totalCount: true);
        var sut = CreateHandler(commandExecutor);

        await sut.HandleQueryAsync(CreateQueryRequest(dialect, totalCount: true, limit: 0));

        string sql = commandExecutor.Commands.Should().ContainSingle().Subject.CommandText;
        sql.Should().NotContain("INSERT INTO");
        sql.Should().NotContain(PageSelectionPagingFragment(dialect));
        sql.Should().Contain("COUNT(");
    }

    [TestCase(SqlDialect.Mssql)]
    [TestCase(SqlDialect.Pgsql)]
    public async Task It_omits_page_selection_without_a_total_count_for_a_zero_limit(SqlDialect dialect)
    {
        var commandExecutor = CreateLimitZeroExecutor(dialect, totalCount: false);
        var sut = CreateHandler(commandExecutor);

        await sut.HandleQueryAsync(CreateQueryRequest(dialect, totalCount: false, limit: 0));

        string sql = commandExecutor.Commands.Should().ContainSingle().Subject.CommandText;
        sql.Should().NotContain("INSERT INTO");
        sql.Should().NotContain(PageSelectionPagingFragment(dialect));
    }

    [TestCase(SqlDialect.Mssql)]
    [TestCase(SqlDialect.Pgsql)]
    public async Task It_omits_page_selection_from_the_cached_candidate_command_for_a_zero_limit(
        SqlDialect dialect
    )
    {
        var commandExecutor = CreateLimitZeroExecutor(dialect, totalCount: true);
        var readAccelerationCoordinator = A.Fake<IDocumentCacheReadAccelerationCoordinator>();
        DocumentCacheReadAccelerationQuerySelectionResult selectionResult = null!;
        A.CallTo(() =>
                readAccelerationCoordinator.QueryAsync(
                    A<DocumentCacheReadAccelerationQueryRequest>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(async call =>
            {
                var request = call.GetArgument<DocumentCacheReadAccelerationQueryRequest>(0)!;
                var cancellationToken = call.GetArgument<CancellationToken>(1);
                selectionResult = await request
                    .SelectAuthorizedCandidatePage(cancellationToken)
                    .ConfigureAwait(false);
                return selectionResult
                    .Should()
                    .BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>()
                    .Subject.Result;
            });
        var sut = CreateHandler(commandExecutor, readAccelerationCoordinator: readAccelerationCoordinator);

        var result = await sut.HandleQueryAsync(CreateQueryRequest(dialect, totalCount: true, limit: 0));

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(7);
        selectionResult.Should().BeOfType<DocumentCacheReadAccelerationQuerySelectionResult.Complete>();
        string sql = commandExecutor.Commands.Should().ContainSingle().Subject.CommandText;
        sql.Should().NotContain("INSERT INTO");
        sql.Should().NotContain(PageSelectionPagingFragment(dialect));
    }

    [TestCase(SqlDialect.Mssql)]
    [TestCase(SqlDialect.Pgsql)]
    public async Task It_keeps_page_selection_in_the_descriptor_query_command_for_a_positive_limit(
        SqlDialect dialect
    )
    {
        var commandExecutor = CreateLimitZeroExecutor(dialect, totalCount: false);
        var sut = CreateHandler(commandExecutor);

        await sut.HandleQueryAsync(CreateQueryRequest(dialect, limit: 25));

        string sql = commandExecutor.Commands.Should().ContainSingle().Subject.CommandText;
        sql.Should().Contain("INSERT INTO");
        sql.Should().Contain(PageSelectionPagingFragment(dialect));
    }
}
