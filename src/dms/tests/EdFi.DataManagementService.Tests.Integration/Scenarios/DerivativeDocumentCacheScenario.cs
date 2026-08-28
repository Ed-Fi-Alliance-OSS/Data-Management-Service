// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// The DocumentCache bypass, against real databases. The cache is materialized from, and keyed by, the
/// parent, so a routed read must neither serve the parent's cache nor write into the derivative's.
/// </summary>
internal static class DerivativeDocumentCacheScenario
{
    /// <summary>
    /// A derivative read returns the derivative's own relational rows, records the derivative skip, and
    /// leaves the derivative's dms.DocumentCache exactly as it found it.
    /// </summary>
    public static async Task It_bypasses_the_cache_and_leaves_the_derivative_cache_untouched(
        ApiIntegrationHarness harness,
        Func<Task<DbConnection>> openDerivativeConnection,
        string quotedCacheTableName
    )
    {
        await using DbConnection derivativeConnection = await openDerivativeConnection();
        long cachedBefore = await CountCachedDocumentsAsync(derivativeConnection, quotedCacheTableName);

        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
            .Should()
            .Be(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "the derivative's own relational rows are what a routed read returns"
            );

        harness
            .DocumentCacheReadTelemetryRecorder.Should()
            .NotBeNull("this fixture records cache read telemetry");

        harness
            .DocumentCacheReadTelemetryRecorder!.CountTelemetryRecords(
                nameof(IDocumentCacheReadTelemetry.RecordDirectFill),
                "SkippedDerivativeTarget"
            )
            .Should()
            .BeGreaterThan(0, "a routed read must record the derivative skip");

        harness
            .DocumentCacheReadTelemetryRecorder!.CountTelemetryRecords(
                nameof(IDocumentCacheReadTelemetry.RecordAttempt),
                "Attempted"
            )
            .Should()
            .Be(0, "no cache lookup may be attempted for a derivative read");

        long cachedAfter = await CountCachedDocumentsAsync(derivativeConnection, quotedCacheTableName);
        cachedAfter
            .Should()
            .Be(cachedBefore, "a routed read must not fill the derivative's own document cache");
    }

    /// <summary>
    /// The same request against the parent still uses the cache, so the bypass above is the derivative
    /// guard rather than read acceleration being off.
    /// </summary>
    public static async Task It_still_uses_the_cache_for_a_parent_read(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
            .Should()
            .Be(DerivativeRoutingSupport.PrimaryStudentUniqueId);

        harness
            .DocumentCacheReadTelemetryRecorder!.CountTelemetryRecords(
                nameof(IDocumentCacheReadTelemetry.RecordDirectFill),
                "SkippedDerivativeTarget"
            )
            .Should()
            .Be(0, "a parent read is not a derivative read");

        // Without this the control would pass for a host where read acceleration was simply off, which
        // would make the bypass above prove nothing.
        harness
            .DocumentCacheReadTelemetryRecorder!.TelemetryRecords.Count(record =>
                record.EventName == nameof(IDocumentCacheReadTelemetry.RecordAttempt)
            )
            .Should()
            .BeGreaterThan(0, "the parent read must reach the cache lookup");

        harness
            .DocumentCacheReadTelemetryRecorder!.TelemetryRecords.Should()
            .Contain(
                record =>
                    record.EventName == nameof(IDocumentCacheReadTelemetry.RecordMiss)
                    || record.EventName == nameof(IDocumentCacheReadTelemetry.RecordHit)
                    || record.EventName == nameof(IDocumentCacheReadTelemetry.RecordPageHit),
                "the lookup must reach a real cache outcome rather than being skipped earlier"
            );
    }

    /// <summary>
    /// The table name is supplied by the dialect fixture: PostgreSQL folds an unquoted identifier to
    /// lower case, and this table was created with its casing preserved.
    /// </summary>
    private static async Task<long> CountCachedDocumentsAsync(
        DbConnection connection,
        string quotedCacheTableName
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {quotedCacheTableName}";

        object? scalar = await command.ExecuteScalarAsync();

        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }
}
