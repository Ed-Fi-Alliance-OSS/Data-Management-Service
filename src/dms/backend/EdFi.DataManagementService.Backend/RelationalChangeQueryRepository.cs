// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.ChangeQueries;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;

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
    private readonly ChangeQueryResponseFieldMapper _fieldMapper = new();

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

    public Task<TrackedChangeQueryResult> QueryTrackedChanges(
        ITrackedChangeQueryRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request is not IRelationalTrackedChangeQueryRequest relationalRequest)
        {
            throw new NotSupportedException(
                "Tracked Change Queries require an IRelationalTrackedChangeQueryRequest."
            );
        }

        if (
            relationalRequest.Operation is ChangeQueryEndpointOperation.KeyChanges
            && relationalRequest.TrackedChangeTable.Kind
                is TrackedChangeTableKind.SharedDescriptor
                    or TrackedChangeTableKind.ConcreteAbstract
        )
        {
            return Task.FromResult(
                new TrackedChangeQueryResult(
                    [],
                    relationalRequest.PaginationParameters.TotalCount ? 0L : null
                )
            );
        }

        IReadOnlyList<ChangeQueryResponseField> fields = _fieldMapper.Map(
            relationalRequest.MappingSet,
            relationalRequest.ResourceModel,
            relationalRequest.TrackedChangeTable
        );

        var planner = new TrackedChangeQueryPlanner(_commandExecutor.Dialect);
        TrackedChangeQueryPlan plan = planner.Plan(relationalRequest, fields);

        if (plan.IsEmpty)
        {
            return Task.FromResult(new TrackedChangeQueryResult([], plan.TotalCount));
        }

        return _commandExecutor.ExecuteReaderAsync(
            plan.Command!,
            (reader, ct) =>
                TrackedChangeQueryRowReader.ReadAsync(
                    reader,
                    relationalRequest.Operation,
                    fields,
                    plan.IncludesTotalCount,
                    ct
                ),
            cancellationToken
        );
    }
}
