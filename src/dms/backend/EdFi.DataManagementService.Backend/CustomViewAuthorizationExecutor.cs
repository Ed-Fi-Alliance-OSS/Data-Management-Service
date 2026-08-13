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

/// <param name="Checks">The checks this batch executes, in emission order.</param>
/// <param name="PlannedChecks">
/// Every custom-view check planned for the request, indexed as the planner assigned them. The <c>cv1</c>
/// payload carries only an index, so a failure is resolved against this list rather than against the batch —
/// which is what lets one request run several batches without their indexes colliding. Defaults to
/// <paramref name="Checks"/> for a request that runs a single batch.
/// </param>
public sealed record CustomViewAuthorizationExecutionRequest(
    MappingSet MappingSet,
    long DocumentId,
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Checks,
    IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec>? PlannedChecks = null
)
{
    public IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> PlannedChecksOrBatch =>
        PlannedChecks ?? Checks;
}

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
/// <para>
/// A failure is not the only way <c>auth.{StrategyName}</c> can break its contract, which is why the run is
/// preceded by a catalog validation whenever the caller supplies an executor for it. The membership SQL reads
/// <c>cv.DocumentId</c> from that object directly, so a plain table — or a view whose <c>DocumentId</c> is
/// merely comparable to <c>bigint</c> — answers it without raising anything, and the request would authorize or
/// deny against an object auth.md does not accept.
/// </para>
/// </remarks>
/// <param name="validationCommandExecutor">
/// The executor the pre-run catalog validation uses, or <see langword="null"/> to skip it. It must open its own
/// connection: a write session's executor runs inside the transaction holding the target lock and would consume
/// that command stream's results.
/// </param>
/// <param name="writeExceptionClassifier">
/// Keeps transient provider failures (deadlock victim, lock timeout) out of the custom-view attribution: they
/// say nothing about the view's contract, so they propagate to the caller's existing transient handling
/// instead of being wrapped as a validation failure.
/// </param>
internal sealed class CustomViewAuthorizationExecutor(
    IRelationalCommandExecutor commandExecutor,
    IRelationshipAuthorizationProviderFailureExtractor? providerFailureExtractor = null,
    IRelationalCommandExecutor? validationCommandExecutor = null,
    IRelationalWriteExceptionClassifier? writeExceptionClassifier = null
) : ICustomViewAuthorizationExecutor
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationshipAuthorizationProviderFailureExtractor _providerFailureExtractor =
        providerFailureExtractor ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;
    private readonly IRelationalCommandExecutor? _validationCommandExecutor = validationCommandExecutor;
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? new NoOpRelationalWriteExceptionClassifier();

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

        // Exactly the checks this run emits, never the whole planned list: validating a view configured after
        // this run would let its 500 preempt a denial the earlier run is about to report.
        if (_validationCommandExecutor is not null)
        {
            await CustomViewAuthorizationValidator
                .ValidateSingleRecordAsync(
                    _validationCommandExecutor,
                    dialect,
                    [
                        .. request.Checks.Where(check =>
                            sqlPlan.EmittedCheckIndexesInOrder.Contains(check.Index)
                        ),
                    ],
                    cancellationToken
                )
                .ConfigureAwait(false);
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
                    request.PlannedChecksOrBatch
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
                    request.PlannedChecksOrBatch,
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
                    request.PlannedChecksOrBatch
                )
            )
        {
            return new CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure(
                CustomViewAuthorizationSecurityConfigurationMessages.InvalidAuthorizationMetadata,
                AuthorizationSecurityConfigurationDiagnostics.ForCustomViewAuthorizationAuth1(
                    request.PlannedChecksOrBatch
                )
            );
        }
        catch (DbException ex)
            when (!_writeExceptionClassifier.IsTransientFailure(ex)
                && CustomViewAuthorizationProviderFailureMapper.IsUnrecognizedProviderFailure(
                    dialect,
                    ex,
                    _providerFailureExtractor
                )
            )
        {
            // Attributed to the configured view, so the documented urn:ed-fi:api:system 500 is preserved
            // instead of an unhandled provider error. Mirrors how DMS-1062 guards the GET-many page query.
            // Transient failures are excluded above: they prove nothing about the view and must reach the
            // caller's retryable-failure handling instead.
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
    /// <summary>
    /// A self-basis proposed check is only ever satisfied by the stored check for the same configured
    /// strategy, which is what authorizes the row whose immutable <c>DocumentId</c> the proposed value reuses.
    /// Without that pair nothing has been proven, so the plan is a configuration defect.
    /// </summary>
    public static string UnpairedSelfBasisProposedCheck(string strategyName) =>
        $"Relational custom view-based authorization metadata is invalid: strategy '{strategyName}' planned a "
        + "self-basis proposed check with no paired stored check to authorize the existing row.";

    /// <summary>
    /// Descriptor writes have no finalized root row to read a bound basis value from, so a proposed check
    /// whose basis is some other resource cannot be executed on that path.
    /// </summary>
    public static string UnsupportedProposedBasisForDescriptorWrite(string strategyName) =>
        $"Relational custom view-based authorization metadata is invalid: strategy '{strategyName}' requires a "
        + "proposed basis value from a document reference, which descriptor writes do not bind.";

    public const string InvalidAuthorizationMetadata =
        "Relational custom view-based authorization metadata is invalid: the authorization batch reported a "
        + "failure that does not correspond to any planned custom-view check.";
}
