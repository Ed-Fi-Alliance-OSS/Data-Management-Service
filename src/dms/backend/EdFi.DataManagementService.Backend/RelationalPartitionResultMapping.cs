// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Restates a shared read-path outcome as its partition-named equivalent.
/// </summary>
/// <remarks>
/// The partition operation reuses the capability lookup, authorization resolution, and parameter-budget
/// guard the GET-many path owns, and those answer in <see cref="QueryResult" />. Translating here rather
/// than re-deriving each outcome is what keeps the two operations from answering the same backend
/// condition differently, and it is the only place a query outcome becomes a partition outcome.
/// </remarks>
internal static class RelationalPartitionResultMapping
{
    /// <summary>
    /// Maps a shared read-path <paramref name="queryResult" /> to its partition equivalent.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown for an outcome the shared path cannot hand a partition request: a non-empty page, which
    /// only hydration produces, and the known-error failure, which reports invalid query terms found
    /// while selecting a hydrating page. Failing loudly keeps either from being silently restated as an
    /// empty boundary set or as an unrelated failure.
    /// </exception>
    public static PartitionResult FromQueryResult(QueryResult queryResult)
    {
        ArgumentNullException.ThrowIfNull(queryResult);

        return queryResult switch
        {
            // Every empty success the shared path produces means no accessible candidates, which is the
            // same thing an empty boundary set means.
            QueryResult.QuerySuccess { EdfiDocs.Count: 0 } => new PartitionResult.PartitionSuccess([]),

            QueryResult.QueryFailureNotImplemented notImplemented =>
                new PartitionResult.PartitionFailureNotImplemented(notImplemented.FailureMessage),

            QueryResult.QueryFailureSecurityConfiguration securityConfiguration =>
                new PartitionResult.PartitionFailureSecurityConfiguration(
                    securityConfiguration.Errors,
                    securityConfiguration.Diagnostics
                ),

            QueryResult.QueryFailureNamespaceNotAuthorized namespaceNotAuthorized =>
                new PartitionResult.PartitionFailureNamespaceNotAuthorized(
                    namespaceNotAuthorized.NamespaceFailure
                ),

            QueryResult.QueryFailureRetryable => new PartitionResult.PartitionFailureRetryable(),

            QueryResult.UnknownFailure unknownFailure => new PartitionResult.UnknownPartitionFailure(
                unknownFailure.FailureMessage
            ),

            _ => throw new InvalidOperationException(
                $"Relational partition planning cannot restate query result '{queryResult.GetType().Name}' "
                    + "as a partition outcome. The shared capability, authorization, and budget path does "
                    + "not produce it for a partition request."
            ),
        };
    }
}
