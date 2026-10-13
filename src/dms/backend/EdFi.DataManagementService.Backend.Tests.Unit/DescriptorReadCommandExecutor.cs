// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

// Existing authorization, paging and response tests arrange stable owner rows. Supply their matching
// boundary versions here; consistency regressions supply all result sets explicitly instead.
internal sealed class DescriptorReadCommandExecutor(
    IReadOnlyList<InMemoryRelationalCommandExecution> executions,
    SqlDialect dialect = SqlDialect.Pgsql,
    bool stableVersions = true
) : IRelationalCommandExecutor
{
    private readonly Queue<InMemoryRelationalCommandExecution> _executions = new(executions);
    public SqlDialect Dialect => dialect;
    public List<RelationalCommand> Commands { get; } = [];

    public async Task<TResult> ExecuteReaderAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
        CancellationToken cancellationToken = default
    )
    {
        Commands.Add(command);
        var execution = _executions.Dequeue();
        if (
            stableVersions
            && command.CommandText.Contains("-- Descriptor acquisition:", StringComparison.Ordinal)
        )
        {
            var rows = execution.ResultSets[^1];
            var versions = InMemoryRelationalResultSet.Create(
                Enumerable
                    .Range(0, rows.RowCount)
                    .Select(index =>
                        RelationalAccessTestData.CreateRow(
                            ("DocumentId", rows.GetValue(index, rows.GetOrdinal("DocumentId"))),
                            ("ContentVersion", rows.GetValue(index, rows.GetOrdinal("ContentVersion")))
                        )
                    )
                    .DistinctBy(row => row["DocumentId"])
                    .ToArray()
            );
            execution = new([.. execution.ResultSets.SkipLast(1), versions, rows, versions]);
        }
        var executor = new InMemoryRelationalCommandExecutor([execution], dialect);
        return await executor.ExecuteReaderAsync(command, readAsync, cancellationToken);
    }
}
