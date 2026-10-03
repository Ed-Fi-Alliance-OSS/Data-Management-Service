// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates the <c>dms.EffectiveSchema</c> fingerprint of an education-organization projection
/// target and translates each outcome into the projection's own taxonomy.
/// </summary>
/// <remarks>
/// <para>
/// It shares the fingerprint cache with <see cref="ValidateDatabaseFingerprintMiddleware"/> and
/// follows the same retention rules, but not its responses: those are 503 for every verdict and carry
/// remediation text and validation issues, while a projection client must tell a permanent
/// incompatibility from a transient outage by status and type alone.
/// </para>
/// <list type="bullet">
/// <item>Malformed fingerprint or a hash other than this deployment's → 409
/// <c>target-schema-incompatible</c> (permanent).</item>
/// <item>No fingerprint row → 503 <c>database-not-provisioned</c> (transient).</item>
/// <item>Any other read failure, a connection failure included → 503 <c>target-unavailable</c>
/// (transient). <c>service-configuration-error</c> is reserved for a missing connection
/// configuration and is never used here.</item>
/// </list>
/// <para>
/// Nothing the database or provider produced is logged: no exception, message, hash or validation
/// issue, only the reason, the exception type name or the provider's log-safe description, the
/// target kind and the data store id.
/// </para>
/// </remarks>
internal class ValidateEducationOrganizationProjectionTargetSchemaMiddleware(
    DatabaseFingerprintProvider _fingerprintProvider,
    IEffectiveSchemaSetProvider _effectiveSchemaSetProvider,
    ILogger<ValidateEducationOrganizationProjectionTargetSchemaMiddleware> _logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        FrontendRequest request = requestInfo.FrontendRequest;
        CancellationToken cancellationToken = requestInfo.RequestCancellationToken;

        IDataStoreSelection dataStoreSelection =
            requestInfo.ScopedServiceProvider.GetRequiredService<IDataStoreSelection>();
        long dataStoreId = dataStoreSelection.GetSelectedDataStore().Id;
        EffectiveDataStoreTarget target = dataStoreSelection.GetEffectiveTarget();

        // Read synchronously so the token exists on every path; see ValidateDatabaseFingerprintMiddleware.
        ValidationCacheRead<DatabaseFingerprint?> read = _fingerprintProvider.ReadFingerprint(
            ValidationCacheKey.For(target),
            target
        );

        DatabaseFingerprint? fingerprint;

        try
        {
            // The read is shared through the cache, so the request stops waiting on cancellation
            // without cancelling the read other requests may be awaiting.
            fingerprint = await read.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DatabaseFingerprintValidationException)
        {
            // Malformed provisioning metadata is a property of the database, not of this moment. The
            // provider retains a malformed Primary verdict, exactly as for every other endpoint.
            LogSchemaIncompatible("MalformedFingerprint", target, dataStoreId, request);

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.TargetSchemaIncompatible,
                request.TraceId
            );
            return;
        }
        catch (Exception ex)
        {
            // A connection or timeout failure, or anything else that is not a verdict. A provider
            // exception's message can quote the connection string, so the exception is never handed
            // to the logger; the connection wrapper's log-safe description is used where there is one.
#pragma warning disable S6667
            _logger.LogError(
                "Projection target fingerprint read failed ({Failure}) for {TargetKind} target of data store {DataStoreId} - {TraceId}",
                ex is DatabaseConnectionUnavailableException connectionUnavailable
                    ? connectionUnavailable.FailureDescription
                    : ex.GetType().Name,
                target.Kind,
                dataStoreId,
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );
#pragma warning restore S6667

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.TargetUnavailable,
                request.TraceId
            );
            return;
        }

        if (fingerprint is null)
        {
            // A successful read of nothing, which the cache cannot see. As in the shared middleware the
            // entry is offered back; the token keeps a Primary verdict until restart and drops only a
            // derivative's, so the projection answers exactly as every other endpoint would.
            read.Token.Invalidate();

            _logger.LogWarning(
                "Projection target not provisioned (no dms.EffectiveSchema row) for {TargetKind} target of data store {DataStoreId} - {TraceId}",
                target.Kind,
                dataStoreId,
                LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
            );

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.DatabaseNotProvisioned,
                request.TraceId
            );
            return;
        }

        string expectedHash = _effectiveSchemaSetProvider
            .EffectiveSchemaSet
            .EffectiveSchema
            .EffectiveSchemaHash;

        if (!string.Equals(fingerprint.EffectiveSchemaHash, expectedHash, StringComparison.Ordinal))
        {
            // Invisible to the provider, which never compares; offered back as the shared middleware does.
            read.Token.Invalidate();

            LogSchemaIncompatible("EffectiveSchemaHashMismatch", target, dataStoreId, request);

            requestInfo.FrontendResponse = EducationOrganizationProjectionResponse.For(
                EducationOrganizationProjectionProblem.TargetSchemaIncompatible,
                request.TraceId
            );
            return;
        }

        requestInfo.DatabaseFingerprint = fingerprint;
        await next();
    }

    private void LogSchemaIncompatible(
        string reason,
        EffectiveDataStoreTarget target,
        long dataStoreId,
        FrontendRequest request
    ) =>
        _logger.LogError(
            "Projection target schema incompatible ({Reason}) for {TargetKind} target of data store {DataStoreId} - {TraceId}",
            reason,
            target.Kind,
            dataStoreId,
            LoggingSanitizer.SanitizeCorrelationId(request.TraceId.Value)
        );
}
