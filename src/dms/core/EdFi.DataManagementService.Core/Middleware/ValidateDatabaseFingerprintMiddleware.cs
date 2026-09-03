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

    /// <summary>
    /// The primary wording, unchanged. A malformed primary verdict is retained for the life of the
    /// process, so repairing the database on its own genuinely is not enough.
    /// </summary>
    private const string MalformedFingerprintDetail =
        "The target database contains malformed dms.EffectiveSchema provisioning metadata. Repair the database by re-running 'ddl provision' against an empty database. If provisioning was partial or the database was modified after provisioning, drop and recreate the database before reprovisioning. Restart the Ed-Fi API service after the database has been repaired to clear the cached fingerprint validation failure.";

    /// <summary>
    /// The same remediation without the restart, because a derivative verdict was already dropped
    /// when this response was produced. Telling the operator to restart would send them after a
    /// cached failure that no longer exists.
    /// </summary>
    private const string MalformedFingerprintDerivativeDetail =
        "The target database contains malformed dms.EffectiveSchema provisioning metadata. Repair the database by re-running 'ddl provision' against an empty database. If provisioning was partial or the database was modified after provisioning, drop and recreate the database before reprovisioning. No restart is "
        + "required: this result was not retained, and the next request will revalidate the database.";

    private const string NotProvisionedDetail =
        "The target database has not been provisioned. Run 'ddl provision' to initialize the "
        + "database schema. If this database was provisioned after the Ed-Fi API service first tried "
        + "to use it, restart the Ed-Fi API service to clear the cached provisioning state.";

    private const string NotProvisionedDerivativeDetail =
        "The target database has not been provisioned. Run 'ddl provision' to initialize the "
        + "database schema. No restart is required: this result was not retained, and the next "
        + "request will revalidate the database.";

    private const string SchemaHashMismatchTitle = "Effective Schema Hash Mismatch";

    private const string SchemaHashMismatchDetail =
        "The database was provisioned for a different effective schema than the Ed-Fi API service expects. "
        + "The database must be reprovisioned with 'ddl provision' against a fresh database "
        + "and the Ed-Fi API service restarted to clear the cached validation state.";

    private const string SchemaHashMismatchDerivativeDetail =
        "The database was provisioned for a different effective schema than the Ed-Fi API service "
        + "expects. The database must be reprovisioned with 'ddl provision' against a fresh "
        + "database. No restart is required: this result was not retained, and the next request "
        + "will revalidate the database.";

    private const string MalformedPrimaryRemediation =
        "Restart the Ed-Fi API service after repairing the database, because a malformed fingerprint "
        + "for a primary database is cached for the life of the process.";

    private const string MalformedDerivativeRemediation =
        "The cached verdict for this derivative database has already been dropped, so repairing the "
        + "database is enough and no restart is required.";

    /// <summary>
    /// Which of a paired detail message this request is told, decided by the policy class its
    /// verdict was cached under. The pairs differ only in their recovery instruction; the problem
    /// type, title, status, content type, and envelope are identical either way.
    /// </summary>
    private static string DetailFor(EffectiveDataStoreTarget target, string primary, string derivative) =>
        target.Kind == EffectiveTargetKind.Primary ? primary : derivative;

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

            string malformedDetail = DetailFor(
                target,
                MalformedFingerprintDetail,
                MalformedFingerprintDerivativeDetail
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    MalformedFingerprintTitle,
                    malformedDetail,
                    [.. ex.ValidationIssues, malformedDetail],
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );

            return;
        }
        catch (Exception ex)
        {
            // Only the exception's type, never the exception itself and never its message, data, or
            // inner exceptions. This is the catch that sees a selected-but-provider-invalid or
            // unreachable target fail inside connection acquisition, and a provider exception from
            // parsing or opening a connection string can quote its values back.
            // S6667 asks for the caught exception to be passed to the logger. That is the right
            // default and the wrong thing here, for the reason above: the exception carries the
            // untrusted value. Its type is logged instead, which is the part that helps an operator
            // without carrying anything the provider put in it.
#pragma warning disable S6667
            logger.LogError(
                "Database fingerprint read failed with {ExceptionType} for data store {DataStoreId} ({Name}). "
                    + "This is a transient error and will be retried on the next request. TraceId: {TraceId}",
                ex.GetType().Name,
                selectedInstance.Id,
                LoggingSanitizer.SanitizeForLogging(selectedInstance.Name),
                requestInfo.FrontendRequest.TraceId.Value
            );
#pragma warning restore S6667

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
                    DetailFor(target, NotProvisionedDetail, NotProvisionedDerivativeDetail),
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

            string mismatchDetail = DetailFor(
                target,
                SchemaHashMismatchDetail,
                SchemaHashMismatchDerivativeDetail
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 503,
                Body: FailureResponse.ForDatabaseFingerprintValidationError(
                    SchemaHashMismatchTitle,
                    mismatchDetail,
                    [mismatchDetail],
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
