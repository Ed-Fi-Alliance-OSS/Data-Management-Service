// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalDeleteExecution
{
    [Test]
    public async Task It_returns_success_when_the_final_delete_result_set_contains_a_row_after_an_empty_result_set()
    {
        var executor = new RecordingDeleteCommandExecutor([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(new Dictionary<string, object?> { ["DocumentId"] = 123L }),
        ]);

        var result = await RelationalDeleteExecution.TryExecuteAsync(
            executor,
            new RelationalCommand("DELETE first; DELETE second RETURNING \"DocumentId\";"),
            new ConfigurableRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            CreateModelSet(),
            NullLogger.Instance,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("delete-execution-final-result"),
            DeleteTargetKind.Document
        );

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
    }

    [Test]
    public async Task It_returns_not_exists_when_no_result_set_contains_a_deleted_document_row()
    {
        var executor = new RecordingDeleteCommandExecutor([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(),
        ]);

        var result = await RelationalDeleteExecution.TryExecuteAsync(
            executor,
            new RelationalCommand("DELETE first; DELETE second RETURNING \"DocumentId\";"),
            new ConfigurableRelationalWriteExceptionClassifier(),
            A.Fake<IRelationalDeleteConstraintResolver>(),
            CreateModelSet(),
            NullLogger.Instance,
            new DocumentUuid(Guid.NewGuid()),
            new TraceId("delete-execution-no-final-result"),
            DeleteTargetKind.Document
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureNotExists>();
    }

    private static DerivedRelationalModelSet CreateModelSet()
    {
        var resourceKey = new ResourceKeyEntry(
            1,
            new QualifiedResourceName("Ed-Fi", "School"),
            "1.0.0",
            false
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0",
                "v1",
                "schema-hash",
                1,
                [1, 2, 3],
                [new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash")],
                [resourceKey]
            ),
            SqlDialect.Pgsql,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi"))],
            [],
            [],
            [],
            [],
            []
        );
    }

    private sealed class RecordingDeleteCommandExecutor(IReadOnlyList<InMemoryRelationalResultSet> resultSets)
        : IRelationalCommandExecutor
    {
        public SqlDialect Dialect => SqlDialect.Pgsql;

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var reader = new InMemoryRelationalCommandReader(resultSets);
            return await readAsync(reader, cancellationToken).ConfigureAwait(false);
        }
    }
}
