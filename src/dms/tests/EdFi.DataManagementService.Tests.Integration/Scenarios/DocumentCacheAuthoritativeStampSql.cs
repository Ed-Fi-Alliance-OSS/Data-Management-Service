// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DocumentCacheAuthoritativeStampSql
{
    // These scenarios use only the descriptor-runtime, profile-root-only and TPDM fixtures.
    // Fail on another fixture rather than silently dropping an unrecognized owner.
    public static string Owners(ApiIntegrationHarness harness) =>
        harness.Fixture.Key switch
        {
            FixtureKey.DescriptorRuntime => """
                SELECT "DocumentId", "ContentLastModifiedAt", "ContentCreatedAt" FROM "dms"."Descriptor"
                """,
            FixtureKey.ProfileRootOnlyMerge => """
                SELECT "DocumentId", "ContentLastModifiedAt", "ContentCreatedAt" FROM "dms"."Descriptor"
                UNION ALL SELECT "DocumentId", "ContentLastModifiedAt", "ContentCreatedAt" FROM "edfi"."Student"
                UNION ALL SELECT "DocumentId", "ContentLastModifiedAt", "ContentCreatedAt" FROM "edfi"."ProfileRootOnlyMergeItem"
                """,
            FixtureKey.AuthoritativeDs52Tpdm => """
                SELECT "DocumentId", "ContentLastModifiedAt", "ContentCreatedAt" FROM "tpdm"."Candidate"
                """,
            _ => throw new InvalidOperationException(
                $"Unsupported cache scenario fixture: {harness.Fixture.Key}"
            ),
        };

    // Differs from every clock-derived timestamp. The SQL Server seventh digit proves datetime2(7)
    // precision through the materializer and writer.
    public static DateTimeOffset CreationSentinel(ApiIntegrationHarness harness) =>
        harness.DbConnection is NpgsqlConnection
            ? new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(1_234_560)
            : new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(1_234_567);

    // A creation-only UPDATE allocates no content version and leaves ContentLastModifiedAt alone.
    public static async Task<DateTimeOffset> PlantCreationSentinelAsync(
        ApiIntegrationHarness harness,
        string qualifiedOwnerTable,
        Guid documentUuid
    )
    {
        DateTimeOffset sentinel = CreationSentinel(harness);
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = $"""
            UPDATE {qualifiedOwnerTable} SET "ContentCreatedAt" = @createdAt
            WHERE "DocumentId" = (SELECT "DocumentId" FROM "dms"."Document" WHERE "DocumentUuid" = @documentUuid);
            """;

        DbParameter uuid = command.CreateParameter();
        uuid.ParameterName = "@documentUuid";
        uuid.Value = documentUuid;
        command.Parameters.Add(uuid);

        DbParameter createdAt = command.CreateParameter();
        createdAt.ParameterName = "@createdAt";
        createdAt.Value = sentinel.UtcDateTime;
        if (harness.DbConnection is not NpgsqlConnection)
        {
            // SQL Server would otherwise bind datetime and round away the sub-millisecond digits.
            createdAt.DbType = DbType.DateTime2;
        }
        command.Parameters.Add(createdAt);

        (await command.ExecuteNonQueryAsync()).Should().Be(1, "the owner row must exist");
        return sentinel;
    }
}
