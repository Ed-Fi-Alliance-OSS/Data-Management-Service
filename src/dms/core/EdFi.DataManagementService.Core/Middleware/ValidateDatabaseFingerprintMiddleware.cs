// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Validates that the selected database has been provisioned by reading the
/// dms.EffectiveSchema fingerprint. Short-circuits with 503 if unprovisioned.
/// </summary>
internal class ValidateDatabaseFingerprintMiddleware(
    DatabaseFingerprintProvider fingerprintProvider,
    IEffectiveSchemaSetProvider effectiveSchemaSetProvider,
    ILogger<ValidateDatabaseFingerprintMiddleware> logger
) : IPipelineStep
{
    private const string MalformedFingerprintTitle = "Database Provisioning Error";
    private const string MalformedFingerprintDetail =
        "The target database contains malformed dms.EffectiveSchema provisioning metadata. Repair the database by re-running 'ddl provision' against an empty database. If provisioning was partial or the database was modified after provisioning, drop and recreate the database before reprovisioning. Restart the Ed-Fi API service after the database has been repaired to clear the cached fingerprint validation failure.";

    private const string MalformedPrimaryRemediation =
        "Restart the Ed-Fi API service after repairing the database, because a malformed fingerprint "
        + "for a primary database is cached for the life of the process.";

    private const string MalformedDerivativeRemediation =
        "The cached verdict for this derivative database has already been dropped, so repairing the "
        + "database is enough and no restart is required.";

    private const string SchemaHashMismatchTitle = "Effective Schema Hash Mismatch";
    private const string SchemaHashMismatchDetail =
        "The database was provisioned for a different effective schema than the Ed-Fi API service expects. "
        + "The database must be reprovisioned with 'ddl provision' against a fresh database "
        + "and the Ed-Fi API service restarted to clear the cached validation state.";

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // The fingerprint describes the database this request is served from, which is the effective
        // target rather than the parent: a request routed to a snapshot or a read replica must be
        // validated against that database's own provisioning metadata. The parent is still read, for
        // the identity that names the instance in logs.
        var dataStoreSelection = requestInfo.ScopedServiceProvider.GetRequiredService<IDataStoreSelection>();
        var selectedInstance = dataStoreSelection.GetSelectedDataStore();
        var target = dataStoreSelection.GetEffectiveTarget();

        // Read synchronously so the token exists on every path, the fault paths included: a request
        // that cannot even read the fingerprint of a derivative is exactly one whose cached verdict
        // must not survive it.
        ValidationCacheRead<DatabaseFingerprint?> read = fingerprintProvider.ReadFingerprint(
            ValidationCacheKey.For(target),
            target
        );

        DatabaseFingerprint? fingerprint;

        try
        {
            fingerprint = await read.Value;
        }
        catch (DatabaseFingerprintValidationException ex)
        {
            // The remediation differs by policy class and the message must not overstate it: a
            // malformed primary verdict is retained for the process lifetime, so repairing the
            // database is not enough on its own, while a malformed derivative verdict was already
            // evicted and the next request re-reads.
            logger.LogError(
                ex,
                "Malformed dms.EffectiveSchema fingerprint for {TargetKind} target of data store {DataStoreId} ({Name}). {Remediation} TraceId: {TraceId}",
                target.Kind,
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                target.Kind == EffectiveTargetKind.Primary
                    ? MalformedPrimaryRemediation
                    : MalformedDerivativeRemediation,
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    MalformedFingerprintTitle,
                    MalformedFingerprintDetail,
                    [.. ex.ValidationIssues, MalformedFingerprintDetail],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );

            return;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Database fingerprint read failed for data store {DataStoreId} ({Name}). "
                    + "This is a transient error and will be retried on the next request. TraceId: {TraceId}",
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForServiceConfigurationError(
                    "Database fingerprint validation encountered a transient error. Check server logs for details.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );

            return;
        }

        if (fingerprint == null)
        {
            // The provider cannot see this: a missing dms.EffectiveSchema row is a successful read of
            // nothing, not a fault. Dropping the entry here is what lets a derivative provisioned
            // after this request be served without a restart.
            read.Token.Invalidate();

            logger.LogWarning(
                "Database not provisioned (no dms.EffectiveSchema row) for data store {Name} - TraceId: {TraceId}",
                LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseNotProvisioned(
                    "The target database has not been provisioned. Run 'ddl provision' to initialize the database schema. If this database was provisioned after the Ed-Fi API service first tried to use it, restart the Ed-Fi API service to clear the cached provisioning state.",
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return;
        }

        var expectedHash = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema.EffectiveSchemaHash;
        if (!string.Equals(fingerprint.EffectiveSchemaHash, expectedHash, StringComparison.Ordinal))
        {
            // Also invisible to the provider, which reads the fingerprint but never compares it to
            // what this process expects.
            read.Token.Invalidate();

            logger.LogError(
                "EffectiveSchemaHash mismatch for data store {DataStoreId} ({Name}): "
                    + "database has {DbHash}, process expects {ExpectedHash}. TraceId: {TraceId}",
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                LoggingSanitizer.SanitizeForLogging(fingerprint.EffectiveSchemaHash),
                LoggingSanitizer.SanitizeForLogging(expectedHash),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    SchemaHashMismatchTitle,
                    SchemaHashMismatchDetail,
                    [SchemaHashMismatchDetail],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return;
        }

        requestInfo.DatabaseFingerprint = fingerprint;
        await next();
    }
}
