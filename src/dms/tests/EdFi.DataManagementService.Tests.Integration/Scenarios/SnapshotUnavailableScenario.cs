// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// A selected snapshot that cannot be reached answers Snapshot Not Found, wherever on the read path
/// the connection was being acquired.
/// </summary>
/// <remarks>
/// <para>
/// A read acquires a connection at several points, and each one had to be guarded separately: the
/// fingerprint read, the resource-key row read, the repository query, and document hydration. Each
/// scenario below arranges the caches so that the seam it names is the <em>first</em> uncached
/// acquisition, then asserts the end-to-end response.
/// </para>
/// <para>
/// Seam attribution is deliberately not claimed here. Every seam produces the same 404 and this
/// project has no log capture, so the response alone cannot say which one raised it - a scenario that
/// claimed otherwise would pass just as well if a single earlier seam answered all four.
/// <c>SeamConnectionGuardTests</c> is the per-seam evidence; what these prove is that the translation
/// is reached from every point on a real read, over the real HTTP pipeline, on both engines.
/// </para>
/// </remarks>
internal static class SnapshotUnavailableScenario
{
    /// <summary>
    /// Seam 1, with cold caches: the fingerprint read is the first thing a request does against the
    /// selected database, so an unreachable snapshot fails there.
    /// </summary>
    /// <remarks>
    /// The seeding step published the snapshot's connection string as a parent, which cached a verdict
    /// under the primary target kind. That is a different cache key from the same text as a snapshot,
    /// so this request genuinely starts cold - and the recovery assertion at the end proves the failed
    /// verdict was not cached either.
    /// </remarks>
    public static async Task It_returns_snapshot_not_found_when_the_fingerprint_read_cannot_connect(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string snapshotConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpResponseMessage response = await SnapshotGetAsync(harness);

            await DerivativeRoutingSupport.AssertSnapshotNotFoundAsync(
                response,
                "the fingerprint read is the first acquisition, and the snapshot is unreachable"
            );
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }

        // Nothing about the failure is retained: the immediately following request revalidates and is
        // served from the snapshot. A cached failed verdict would keep answering 404 until restart.
        using HttpResponseMessage recovered = await SnapshotGetAsync(harness);

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(recovered))
            .Should()
            .Be(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "the failed verdict must not have been cached"
            );
    }

    /// <summary>
    /// Seams 2 and 3: the slow-path dms.ResourceKey row read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Priming the fingerprint verdict is not enough on its own to reach this seam.
    /// <c>ResourceKeyValidator</c> answers from a fast path that compares only the row count and the
    /// seed hash carried in the fingerprint, and in these fixtures both match, so the row reader is
    /// never called at all.
    /// </para>
    /// <para>
    /// The arrangement therefore leaves the snapshot's <c>EffectiveSchemaHash</c> alone - otherwise the
    /// fingerprint middleware answers first and this seam is never reached - and changes only its
    /// <c>ResourceKeySeedHash</c>. One request while the snapshot is still reachable populates the
    /// bounded fingerprint verdict and proves the slow path runs; the resource-key verdict is not
    /// carried over, because the middleware invalidates it on the mismatch branch. The next request
    /// therefore revalidates, and the row read is the first failing acquisition.
    /// </para>
    /// </remarks>
    public static async Task It_returns_snapshot_not_found_when_the_resource_key_read_cannot_connect(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        Func<string, Task<DbConnection>> openConnectionAsync,
        string snapshotConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(openConnectionAsync);

        await MismatchTheResourceKeySeedHashAsync(openConnectionAsync, snapshotConnectionString);

        // The priming request, which is also the proof the arrangement reaches the slow path: only a
        // resource-key verdict can answer with this problem type, and only the slow path produces one.
        using HttpResponseMessage primed = await SnapshotGetAsync(harness);
        string primedBody = await primed.Content.ReadAsStringAsync();

        primed
            .StatusCode.Should()
            .Be(
                HttpStatusCode.ServiceUnavailable,
                $"a mismatched seed hash must reach resource-key validation: {primedBody}"
            );
        primedBody
            .Should()
            .Contain(
                "urn:ed-fi:api:resource-key-seed-validation-error",
                "the fingerprint still matches, so this must be the resource-key answer rather than seam 1's"
            );

        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpResponseMessage response = await SnapshotGetAsync(harness);

            await DerivativeRoutingSupport.AssertSnapshotNotFoundAsync(
                response,
                "the fingerprint verdict is cached and the resource-key verdict is not, so the row read "
                    + "is the first acquisition"
            );
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }

    /// <summary>
    /// Seams 4 and 5: the repository query. Both validation verdicts are primed against a consistent
    /// snapshot, so neither validation middleware acquires anything and the query is the first to try.
    /// </summary>
    public static async Task It_returns_snapshot_not_found_when_the_repository_query_cannot_connect(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string snapshotConnectionString
    )
    {
        await PrimeValidationVerdictsAsync(harness);

        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpResponseMessage response = await SnapshotGetAsync(harness);

            await DerivativeRoutingSupport.AssertSnapshotNotFoundAsync(
                response,
                "both validation verdicts are cached, so the repository query is the first acquisition"
            );
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }

    /// <summary>
    /// Seams 6 and 7: document hydration, which the design singles out as the easy one to miss.
    /// </summary>
    /// <remarks>
    /// Making the database unreachable up front cannot reach this seam: the repository query would fail
    /// first. The request is instead held at hydration - which runs after the query has already
    /// executed against the snapshot - and the database is made unreachable while it waits. Hydration
    /// then acquires its own connection and is the first acquisition to fail, with every earlier one
    /// having already succeeded against the same database.
    /// </remarks>
    public static async Task It_returns_snapshot_not_found_when_hydration_cannot_connect(
        ApiIntegrationHarness harness,
        HydrationGate hydrationGate,
        IDerivativeTargetReachability reachability,
        string snapshotConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(hydrationGate);

        await PrimeValidationVerdictsAsync(harness);

        hydrationGate.Arm();
        Task<HttpResponseMessage> inFlight = SnapshotGetAsync(harness);

        // Provably past the query and inside hydration, rather than merely likely to be.
        await hydrationGate.Arrived;

        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            hydrationGate.Release();

            using HttpResponseMessage response = await inFlight;

            await DerivativeRoutingSupport.AssertSnapshotNotFoundAsync(
                response,
                "the fingerprint, resource-key, and query acquisitions all succeeded, so hydration is "
                    + "the first one to fail"
            );
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }

    /// <summary>
    /// A snapshot whose configured text no provider can parse is Snapshot Not Found, with no fallback
    /// to the primary or the replica, and nothing about the failure is retained.
    /// </summary>
    /// <remarks>
    /// Selection deliberately does no provider parsing, so this target is selectable on the strength of
    /// its text alone and the failure belongs at acquisition. That is also why the recovery half needs a
    /// published configuration rather than just a second request: revalidating would re-read the same
    /// unparseable string, so only a corrected data store proves the verdict was not cached.
    /// </remarks>
    public static async Task It_returns_snapshot_not_found_for_a_provider_invalid_snapshot_string(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        IDerivativeTargetReachability reachability,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(reachability);

        Publish(
            provider,
            dataStoreId,
            providerToken,
            primaryConnectionString,
            replicaConnectionString,
            reachability.ProviderInvalidConnectionString(primaryConnectionString)
        );

        using (HttpResponseMessage response = await SnapshotGetAsync(harness))
        {
            // A 404 is itself the no-fallback proof: had the request been served from the primary or the
            // replica it would have answered 200 with that database's Students.
            await DerivativeRoutingSupport.AssertSnapshotNotFoundAsync(
                response,
                "the snapshot's configured text cannot be parsed, so its acquisition fails"
            );
        }

        // A corrected data store, published through the same refresh path production uses.
        Publish(
            provider,
            dataStoreId,
            providerToken,
            primaryConnectionString,
            replicaConnectionString,
            snapshotConnectionString
        );

        using HttpResponseMessage corrected = await SnapshotGetAsync(harness);

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(corrected))
            .Should()
            .Be(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "no verdict was retained, so a corrected snapshot is served without a restart"
            );
    }

    /// <summary>
    /// The same unparseable text on a read replica keeps the database-availability response it produces
    /// today. It is never quietly served from the primary, and never Snapshot Not Found - that answer
    /// belongs to a snapshot, and a request that asked for no snapshot must not receive it.
    /// </summary>
    public static async Task It_keeps_the_availability_response_for_a_provider_invalid_replica_string(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        IDerivativeTargetReachability reachability,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(reachability);

        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.ReadReplica] = reachability.ProviderInvalidConnectionString(
                        primaryConnectionString
                    ),
                }
            ),
        ]);

        // No snapshot header: a replica-eligible read, which selects the unusable replica.
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        await AssertServiceConfigurationErrorAsync(
            response,
            "a read replica that cannot be parsed keeps the database-availability response"
        );
    }

    /// <summary>
    /// And on the primary, where the existing behavior must be byte-identical: the service
    /// configuration error the fingerprint middleware already answers with, for a write as well as a
    /// read.
    /// </summary>
    /// <remarks>
    /// The write is deliberately asserted as that same 503 rather than through the write-failure mapper,
    /// which this arrangement cannot reach twice over. The upsert pipeline runs its database-validation
    /// steps well before the upsert handler, so the request is answered at the fingerprint read and
    /// never reaches the write executor; and independently, the write executor's session-creation catch
    /// takes a <c>DbException</c>, which a parsing failure is not. That mapper's preservation is pinned
    /// where it is genuinely reachable, by the seam-level write-session contract test.
    /// </remarks>
    public static async Task It_keeps_the_service_configuration_error_for_a_provider_invalid_primary_string(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        IDerivativeTargetReachability reachability,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(reachability);

        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(
                dataStoreId,
                reachability.ProviderInvalidConnectionString(primaryConnectionString),
                providerToken
            ),
        ]);

        using (
            HttpResponseMessage read = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint
            )
        )
        {
            await AssertServiceConfigurationErrorAsync(
                read,
                "a primary that cannot be parsed keeps the service configuration error it answers today"
            );
        }

        using (HttpContent content = DerivativeRoutingSupport.StudentContent("provider-invalid-primary"))
        using (
            HttpResponseMessage write = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Post,
                DerivativeRoutingSupport.StudentsEndpoint,
                useSnapshotHeaderValue: null,
                content
            )
        )
        {
            await AssertServiceConfigurationErrorAsync(
                write,
                "the upsert pipeline validates the database before the handler runs, so a write is "
                    + "answered here rather than by the write-failure mapper"
            );
        }

        // The verdict was not retained, so a corrected data store is served without a restart.
        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);

        using HttpResponseMessage corrected = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(corrected))
            .Should()
            .Be(DerivativeRoutingSupport.PrimaryStudentUniqueId);
    }

    /// <summary>
    /// A snapshot that is reachable but not provisioned keeps its own 503. The connection was acquired
    /// successfully and the fingerprint row was simply absent, which is a statement about the database's
    /// contents rather than about reaching it.
    /// </summary>
    public static async Task It_keeps_the_not_provisioned_response_for_a_reachable_snapshot(
        ApiIntegrationHarness harness,
        Func<string, Task<DbConnection>> openConnectionAsync,
        string snapshotConnectionString
    )
    {
        await ExecuteAgainstSnapshotAsync(
            openConnectionAsync,
            snapshotConnectionString,
            $"DELETE FROM \"{EffectiveSchemaTableDefinition.Table.Schema.Value}\".\"{EffectiveSchemaTableDefinition.Table.Name}\""
        );

        using HttpResponseMessage response = await SnapshotGetAsync(harness);

        await AssertUntranslatedAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "urn:ed-fi:api:database-not-provisioned",
            "an unprovisioned snapshot was reached, so it is not a missing snapshot"
        );
    }

    /// <summary>
    /// And a reachable snapshot provisioned for a different effective schema keeps its own 503, for the
    /// same reason: the acquisition succeeded and the comparison is what failed.
    /// </summary>
    public static async Task It_keeps_the_schema_mismatch_response_for_a_reachable_snapshot(
        ApiIntegrationHarness harness,
        Func<string, Task<DbConnection>> openConnectionAsync,
        string snapshotConnectionString
    )
    {
        string schema = EffectiveSchemaTableDefinition.Table.Schema.Value;

        // The hash is a foreign key from dms.SchemaComponent, so the constraint comes off first. It is
        // also fixed-width and shape-validated, so the replacement is a well-formed hash of the right
        // length rather than arbitrary text - otherwise the fingerprint would be rejected as malformed
        // and this would assert the wrong 503 of the two the middleware can produce.
        await ExecuteAgainstSnapshotAsync(
            openConnectionAsync,
            snapshotConnectionString,
            $"ALTER TABLE \"{schema}\".\"SchemaComponent\" DROP CONSTRAINT \"FK_SchemaComponent_EffectiveSchemaHash\"; "
                + $"UPDATE \"{schema}\".\"{EffectiveSchemaTableDefinition.Table.Name}\" "
                + $"SET \"{EffectiveSchemaTableDefinition.EffectiveSchemaHash.Value}\" = '{new string('f', 64)}';"
        );

        using HttpResponseMessage response = await SnapshotGetAsync(harness);

        await AssertUntranslatedAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "urn:ed-fi:api:database-fingerprint-validation-error",
            "the snapshot was reached and its schema hash did not match"
        );
    }

    /// <summary>
    /// A read whose database work fails <em>after</em> a connection was acquired keeps its own answer.
    /// This is the line the guard draws: it classifies acquisition, and nothing past it.
    /// </summary>
    /// <remarks>
    /// The statement is supplied per engine because renaming a table is the one thing in these scenarios
    /// with no common syntax. It hides a table the resource query reads and that no earlier validation
    /// touches, so the fingerprint and resource-key reads still succeed against the same database and
    /// the failure is provably later than the acquisition that preceded it.
    /// </remarks>
    public static async Task It_does_not_translate_a_failure_after_a_successful_acquisition(
        ApiIntegrationHarness harness,
        Func<string, Task<DbConnection>> openConnectionAsync,
        string hideQueriedTableSql,
        string snapshotConnectionString
    )
    {
        await ExecuteAgainstSnapshotAsync(openConnectionAsync, snapshotConnectionString, hideQueriedTableSql);

        using HttpResponseMessage response = await SnapshotGetAsync(harness);
        string body = await response.Content.ReadAsStringAsync();

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.InternalServerError,
                $"a query failure is not a connection failure, so it keeps the unexpected-error answer: {body}"
            );
        body.Should().NotContain("Snapshot not found.", "the acquisition succeeded");
    }

    /// <summary>
    /// An ordinary miss on a reachable snapshot stays an ordinary miss. Both answers are 404, so this is
    /// the case where the snapshot detail could leak unnoticed onto a request that found the database
    /// perfectly well and simply had nothing to return.
    /// </summary>
    public static async Task It_does_not_translate_an_ordinary_miss_on_a_reachable_snapshot(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            $"{DerivativeRoutingSupport.StudentsEndpoint}/{Guid.NewGuid()}",
            useSnapshotHeaderValue: "true"
        );

        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
        body.Should()
            .NotContain(
                "Snapshot not found.",
                "the snapshot was reached; it is the document that does not exist"
            );
    }

    /// <summary>
    /// And a request rejected before any database work keeps its own 400 rather than being answered for
    /// the snapshot it happened to ask for.
    /// </summary>
    public static async Task It_does_not_translate_an_invalid_parameter_on_a_reachable_snapshot(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            $"{DerivativeRoutingSupport.StudentsEndpoint}?limit=not-a-number",
            useSnapshotHeaderValue: "true"
        );

        await AssertUntranslatedAsync(
            response,
            HttpStatusCode.BadRequest,
            "urn:ed-fi:api:bad-request",
            "an invalid parameter is answered on its own terms"
        );
    }

    /// <summary>
    /// An unreachable read replica keeps the database-availability response, and is never quietly served
    /// from the primary. The connectivity counterpart to the unparseable-string case: a read replica must
    /// never receive the snapshot answer however its acquisition failed.
    /// </summary>
    public static async Task It_keeps_the_availability_response_when_the_replica_is_unreachable(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string replicaConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(reachability);

        await reachability.MakeUnreachableAsync(replicaConnectionString);

        try
        {
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DerivativeRoutingSupport.StudentsEndpoint
            );

            await AssertServiceConfigurationErrorAsync(
                response,
                "an unreachable read replica keeps the database-availability response"
            );
        }
        finally
        {
            await reachability.MakeReachableAsync(replicaConnectionString);
        }
    }

    /// <summary>
    /// An authorization denial against a reachable snapshot keeps its 403.
    /// </summary>
    public static async Task It_does_not_translate_an_authorization_denial_on_a_reachable_snapshot(
        ApiIntegrationHarness harness
    )
    {
        using HttpResponseMessage response = await SnapshotGetAsync(harness);

        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().NotContain("Snapshot not found.");
    }

    /// <summary>
    /// With cache acceleration enabled, a snapshot-selected read bypasses the cache adapter outright -
    /// the acceleration coordinator declines every non-primary target - so the relational seam is what
    /// answers, and an unreachable snapshot still returns the exact Snapshot Not Found with no fallback
    /// to the primary's cached rows.
    /// </summary>
    public static async Task It_bypasses_the_cache_and_still_answers_snapshot_not_found(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string snapshotConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(reachability);

        await reachability.MakeUnreachableAsync(snapshotConnectionString);

        try
        {
            using HttpResponseMessage response = await SnapshotGetAsync(harness);

            await DerivativeRoutingSupport.AssertSnapshotNotFoundAsync(
                response,
                "cache acceleration does not apply to a snapshot, so the relational seam answers"
            );
        }
        finally
        {
            await reachability.MakeReachableAsync(snapshotConnectionString);
        }
    }

    /// <summary>
    /// The other side of the same arrangement: a primary-selected read whose cache acquisition fails is
    /// a cache miss, not a failed request. It falls through to the relational read and is served.
    /// </summary>
    public static async Task It_falls_back_relationally_when_a_primary_cache_acquisition_fails(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString
    )
    {
        ArgumentNullException.ThrowIfNull(provider);

        // Parent only, so the read selects the primary rather than a derivative.
        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);

        using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint
        );

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(response))
            .Should()
            .Be(
                DerivativeRoutingSupport.PrimaryStudentUniqueId,
                "a failed cache acquisition is a miss that falls through to the relational read"
            );
    }

    /// <summary>
    /// An answer that must keep its own status and problem type rather than becoming Snapshot Not Found.
    /// </summary>
    private static async Task AssertUntranslatedAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedProblemType,
        string because
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expectedStatus, $"{because}: {body}");
        body.Should().Contain(expectedProblemType, because);
        body.Should().NotContain("Snapshot not found.", because);
    }

    private static async Task ExecuteAgainstSnapshotAsync(
        Func<string, Task<DbConnection>> openConnectionAsync,
        string snapshotConnectionString,
        string commandText
    )
    {
        ArgumentNullException.ThrowIfNull(openConnectionAsync);

        await using DbConnection connection = await openConnectionAsync(snapshotConnectionString);
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The database-availability answer a non-snapshot acquisition failure keeps: a 503 carrying the
    /// service-configuration problem type. Asserted as neither Snapshot Not Found nor a success, so
    /// that the snapshot response cannot leak onto a kind that must not receive it.
    /// </summary>
    private static async Task AssertServiceConfigurationErrorAsync(
        HttpResponseMessage response,
        string because
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, $"{because}: {body}");
        body.Should().Contain("urn:ed-fi:api:service-configuration-error", because);
        body.Should().NotContain("Snapshot not found.", because);
    }

    private static void Publish(
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string replicaConnectionString,
        string snapshotConnectionString
    ) =>
        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.ReadReplica] = replicaConnectionString,
                    [DataStoreDerivativeType.Snapshot] = snapshotConnectionString,
                }
            ),
        ]);

    private static Task<HttpResponseMessage> SnapshotGetAsync(ApiIntegrationHarness harness) =>
        DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DerivativeRoutingSupport.StudentsEndpoint,
            useSnapshotHeaderValue: "true"
        );

    /// <summary>
    /// One successful snapshot read, which populates the fingerprint verdict and the resource-key
    /// verdict for the snapshot target. Its success is asserted, because a scenario that primed nothing
    /// would still see the seam it names fail - just not first, which is the property under test.
    /// </summary>
    private static async Task PrimeValidationVerdictsAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage primed = await SnapshotGetAsync(harness);

        (await DerivativeRoutingSupport.ReadServingDatabaseAsync(primed))
            .Should()
            .Be(
                DerivativeRoutingSupport.SnapshotStudentUniqueId,
                "the verdicts must be primed against a snapshot that validates"
            );
    }

    /// <summary>
    /// Replaces the snapshot's resource-key seed hash with one the process cannot expect, leaving its
    /// effective-schema hash untouched so the fingerprint still validates.
    /// </summary>
    /// <remarks>
    /// Written through a parameter rather than an engine-specific literal, and with the identifiers
    /// quoted, so the one statement runs unchanged on PostgreSQL - which folds unquoted identifiers to
    /// lower case - and on SQL Server.
    /// </remarks>
    private static async Task MismatchTheResourceKeySeedHashAsync(
        Func<string, Task<DbConnection>> openConnectionAsync,
        string snapshotConnectionString
    )
    {
        await using DbConnection connection = await openConnectionAsync(snapshotConnectionString);
        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            $"UPDATE \"{EffectiveSchemaTableDefinition.Table.Schema.Value}\".\"{EffectiveSchemaTableDefinition.Table.Name}\" "
            + $"SET \"{EffectiveSchemaTableDefinition.ResourceKeySeedHash.Value}\" = @seedHash";

        DbParameter seedHash = command.CreateParameter();
        seedHash.ParameterName = "seedHash";

        // A hash of the required length, so it passes the fingerprint's own shape validation and fails
        // only the comparison. A malformed length would be answered by seam 1 instead.
        seedHash.Value = Enumerable.Repeat((byte)0xFF, 32).ToArray();
        command.Parameters.Add(seedHash);

        await command.ExecuteNonQueryAsync();
    }
}
