// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Interface;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Relational implementation of <see cref="IChangeQueryRepository"/>. Reads the newest change
/// version from the dms.GetMaxChangeVersion() function, which is identical across PostgreSQL and
/// SQL Server, so a single dialect-agnostic command serves both engines.
/// </summary>
public sealed class RelationalChangeQueryRepository(IRelationalCommandExecutor commandExecutor)
    : IChangeQueryRepository
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<long> GetNewestChangeVersion(CancellationToken cancellationToken = default) =>
        _commandExecutor.ExecuteReaderAsync(
            new RelationalCommand("SELECT dms.GetMaxChangeVersion() AS \"NewestChangeVersion\""),
            static async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("dms.GetMaxChangeVersion() returned no rows.");
                }

                return reader.GetRequiredFieldValue<long>("NewestChangeVersion");
            },
            cancellationToken
        );
}
