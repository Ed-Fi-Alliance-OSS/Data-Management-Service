// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <param name="Checks">
/// The planned checks that produced the batch. The <c>cv1</c> payload carries only an index and resolution is
/// positional, so this must be the same list, in the same order, that the compiler was given.
/// </param>
public sealed record CustomViewAuthorizationExecutionRequest(
    MappingSet MappingSet,
    long DocumentId,
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Checks
);

public abstract record CustomViewAuthorizationExecutionResult
{
    private CustomViewAuthorizationExecutionResult() { }

    public sealed record Authorized() : CustomViewAuthorizationExecutionResult;

    public sealed record NotAuthorized(CustomViewAuthorizationFailure Failure)
        : CustomViewAuthorizationExecutionResult;

    /// <summary>
    /// The batch carried a custom-view payload this family owns but could not turn into a response. A
    /// security-configuration defect rather than a denial.
    /// </summary>
    public sealed record InvalidAuthorizationFailure(
        string FailureMessage,
        SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
    ) : CustomViewAuthorizationExecutionResult;

    /// <summary>
    /// The stored target row no longer exists: it was deleted between the unlocked target lookup and the
    /// stored check. Read callers re-resolve the target and surface the resulting 404.
    /// </summary>
    public sealed record StaleTarget : CustomViewAuthorizationExecutionResult;
}

public interface ICustomViewAuthorizationExecutor
{
    Task<CustomViewAuthorizationExecutionResult> ExecuteAsync(
        CustomViewAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Executes co-batched single-record custom view-based authorization SQL. The batch emits one
/// <c>SELECT CASE ... END;</c> per check that SQL can decide; the first failing check raises AUTH1 with a
/// <c>cv1|index|kind</c> payload and aborts the batch. A clean run authorizes the record.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the namespace and relationship executors, this one can be handed a plan that emits no SQL at all: a
/// self-basis proposed check is decided in C#. That case authorizes vacuously here and the caller owns the
/// decision, which is why the compiled plan reports its emitted check indexes.
/// </para>
/// <para>
/// A failure carrying no recognized authorization payload is attributed to the custom view rather than
/// rethrown. The batch's only object created outside the generated schema is <c>auth.{StrategyName}</c>, which
/// can be dropped, replaced, or revoked between requests, and auth.md requires that to surface as the
/// <c>urn:ed-fi:api:system</c> 500 rather than as an unhandled provider error. Authorization payloads are
/// dispatched first, so a denial is never relabelled this way.
/// </para>
/// </remarks>
internal sealed class CustomViewAuthorizationExecutor(
    IRelationalCommandExecutor commandExecutor,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null
) : ICustomViewAuthorizationExecutor
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

    public async Task<CustomViewAuthorizationExecutionResult> ExecuteAsync(
        CustomViewAuthorizationExecutionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Checks.Count == 0)
        {
            return new CustomViewAuthorizationExecutionResult.Authorized();
        }

        var dialect = request.MappingSet.Key.Dialect;
        var sqlPlan = new SingleRecordCustomViewAuthorizationSqlCompiler(dialect).Compile(
            new SingleRecordCustomViewAuthorizationSqlSpec(
                request.Checks,
                CustomViewAuthorizationSqlSpecDefaults.DocumentIdParameterName
            )
        );

        if (sqlPlan.EmittedCheckIndexesInOrder.Count == 0)
        {
            // Every planned check is decided in C#; there is nothing to execute.
            return new CustomViewAuthorizationExecutionResult.Authorized();
        }

        if (sqlPlan.ProposedValueParametersInOrder.Count > 0)
        {
            throw new InvalidOperationException(
                "Custom view authorization executor cannot execute proposed-value checks without extracted runtime values."
            );
        }

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
            when (CustomViewAuthorizationProviderFailureMapper.IsStaleStoredTargetFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor,
                    request.Checks
                )
            )
        {
            return new CustomViewAuthorizationExecutionResult.StaleTarget();
        }
        catch (DbException ex)
            when (CustomViewAuthorizationProviderFailureMapper.TryMapCustomViewAuthorizationFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor,
                    request.Checks,
                    out var customViewFailure
                )
            )
        {
            return new CustomViewAuthorizationExecutionResult.NotAuthorized(customViewFailure!);
        }
        catch (DbException ex)
            when (CustomViewAuthorizationProviderFailureMapper.IsUnmappableCustomViewPayload(
                    dialect,
                    ex,
                    _providerFailureExtractor,
                    request.Checks
                )
            )
        {
            return new CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure(
                CustomViewAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata,
                AuthorizationSecurityConfigurationDiagnostics.ForCustomViewAuthorizationAuth1(request.Checks)
            );
        }
        catch (DbException ex)
            when (CustomViewAuthorizationProviderFailureMapper.IsUnrecognizedProviderFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor
                )
            )
        {
            // Attributed to the configured view, so the documented urn:ed-fi:api:system 500 is preserved
            // instead of an unhandled provider error. Mirrors how DMS-1062 guards the GET-many page query.
            throw new CustomViewAuthorizationValidationException(ex);
        }
    }

    internal static RelationalCommand BuildCommand(
        SingleRecordCustomViewAuthorizationSqlPlan sqlPlan,
        CustomViewAuthorizationExecutionRequest request
    ) =>
        new(
            sqlPlan.AuthorizationSql,
            [.. sqlPlan.ParametersInOrder.Select(parameter => BuildParameter(parameter, request.DocumentId))]
        );

    private static RelationalParameter BuildParameter(QuerySqlParameter parameter, long documentId)
    {
        if (parameter.Binding.Kind is not QuerySqlParameterBindingKind.Scalar)
        {
            throw new InvalidOperationException(
                $"Custom view authorization parameter '{parameter.ParameterName}' must bind as a scalar."
            );
        }

        return new RelationalParameter($"@{parameter.ParameterName}", documentId);
    }

    private static async Task<CustomViewAuthorizationExecutionResult> ReadAuthorizedResultAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        // Advance through every co-batched statement's result set so each check executes and a
        // later-statement AUTH1 failure surfaces as a DbException rather than being skipped.
        var hasMoreResultSets = true;
        while (hasMoreResultSets)
        {
            hasMoreResultSets = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CustomViewAuthorizationExecutionResult.Authorized();
    }
}

internal static class CustomViewAuthorizationSqlSpecDefaults
{
    public const string DocumentIdParameterName = "documentId";
}

internal static class CustomViewAuthorizationSecurityConfigurationMessages
{
    public const string InvalidAuthorizationMetadata =
        "Relational custom view-based authorization metadata is invalid: the authorization batch reported a "
        + "failure that does not correspond to any planned custom-view check.";
}
