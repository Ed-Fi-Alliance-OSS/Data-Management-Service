// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <param name="DocumentId">The stored root document's surrogate id, the check's only subject.</param>
/// <param name="Check">
/// The single planned ownership check. Ownership emits exactly one check per operation, so this is a value
/// rather than the sibling families' list.
/// </param>
public sealed record OwnershipAuthorizationExecutionRequest(
    MappingSet MappingSet,
    long DocumentId,
    OwnershipAuthorizationCheckSpec Check,
    OwnershipTokenParameterization OwnershipTokenParameterization
);

public abstract record OwnershipAuthorizationExecutionResult
{
    private OwnershipAuthorizationExecutionResult() { }

    public sealed record Authorized : OwnershipAuthorizationExecutionResult;

    /// <summary>A §2.13 or §2.14 denial, a 403.</summary>
    public sealed record NotAuthorized(OwnershipAuthorizationFailure Failure)
        : OwnershipAuthorizationExecutionResult;

    public sealed record InvalidAuthorizationFailure(
        string FailureMessage,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : OwnershipAuthorizationExecutionResult;

    /// <summary>
    /// The stored target row no longer exists: it was deleted between the unlocked target lookup and this
    /// check. Read callers re-resolve the target and surface the resulting 404; locked write and delete
    /// callers never observe this because they row-lock the target before the check.
    /// </summary>
    public sealed record StaleTarget : OwnershipAuthorizationExecutionResult;
}

public interface IOwnershipAuthorizationExecutor
{
    Task<OwnershipAuthorizationExecutionResult> ExecuteAsync(
        OwnershipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Executes the single-record ownership authorization SQL. The SQL emits one <c>SELECT CASE … END;</c>
/// whose authorized arm yields <c>1</c> and whose three failure arms raise AUTH1 with an
/// <c>own1|configuredIndex|kind</c> payload, aborting the command. A clean run authorizes the record.
/// </summary>
/// <remarks>
/// One command, one statement, unlike the namespace executor which co-batches a stored and a proposed check.
/// Ownership has exactly one value source — the stored token — so there is never a second check to batch
/// with. The reader still drains every result set, which costs nothing here and keeps the shape identical
/// should a caller ever hand this executor a co-batched plan.
/// </remarks>
internal sealed class OwnershipAuthorizationExecutor(
    IRelationalCommandExecutor commandExecutor,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
) : IOwnershipAuthorizationExecutor
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<OwnershipAuthorizationExecutionResult> ExecuteAsync(
        OwnershipAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Check);

        var dialect = request.MappingSet.Key.Dialect;
        var sqlPlan = new OwnershipAuthorizationSqlCompiler(dialect).Compile(
            new OwnershipAuthorizationSqlSpec(
                request.Check,
                request.OwnershipTokenParameterization,
                OwnershipAuthorizationSqlSpecDefaults.DocumentIdParameterName
            )
        );

        try
        {
            return await _commandExecutor
                .ExecuteReaderAsync(
                    BuildCommand(sqlPlan, request),
                    ReadAuthorizedResultAsync,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
            when (OwnershipAuthorizationProviderFailureMapper.TryMapOwnershipAuthorizationFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor,
                    request.Check.RawConfiguredIndex,
                    out var mappedResult
                )
            )
        {
            // Every ownership AUTH1 outcome — denial, stale target, or unattributable payload — is decided
            // by the mapper, so this executor holds no second copy of that classification.
            return mappedResult!;
        }
    }

    /// <summary>
    /// Builds the executable command for a compiled plan. Internal so composite emission can reuse the
    /// exact standalone command — SQL and parameter binding — rather than duplicating either.
    /// </summary>
    internal static RelationalCommand BuildCommand(
        OwnershipAuthorizationSqlPlan sqlPlan,
        OwnershipAuthorizationExecutionRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(sqlPlan);
        ArgumentNullException.ThrowIfNull(request);

        Dictionary<string, object?> valuesByParameterName = new(StringComparer.Ordinal);

        OwnershipAuthorizationCommandParameterBuilder.AddParameterValues(
            valuesByParameterName,
            request.OwnershipTokenParameterization,
            request.DocumentId
        );

        return new RelationalCommand(
            sqlPlan.AuthorizationSql,
            [
                .. sqlPlan.ParametersInOrder.Select(parameter =>
                    OwnershipAuthorizationCommandParameterBuilder.BuildParameter(
                        parameter,
                        valuesByParameterName[parameter.ParameterName]
                    )
                ),
            ]
        );
    }

    private static async Task<OwnershipAuthorizationExecutionResult> ReadAuthorizedResultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        // Drain every result set so each statement executes and any AUTH1 failure surfaces as a
        // DbException. A clean run means the authorized arm matched: every other arm raises.
        var hasMoreResultSets = true;
        while (hasMoreResultSets)
        {
            hasMoreResultSets = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        return new OwnershipAuthorizationExecutionResult.Authorized();
    }
}
