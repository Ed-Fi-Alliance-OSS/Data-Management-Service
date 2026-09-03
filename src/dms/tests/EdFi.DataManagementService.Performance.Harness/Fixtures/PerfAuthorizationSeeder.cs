// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Executes the authorized-variant seeding against an already-loaded primary fixture: the
/// school, its grade-level descriptor, and one chunked set-based StudentSchoolAssociation
/// block enrolling every second student. Runs strictly after the pristine measurement phase.
/// The GENERATE_SERIES availability guard is the primary loader's responsibility, because
/// this seeder only ever runs in a database that loader has already populated.
/// </summary>
public static class PerfAuthorizationSeeder
{
    public static async Task SeedAndVerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfAuthorizationSeedDefinition seed,
        long chunkSize = PerfFixtureLoader.DefaultChunkSize
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        bool postgresql = provider == PerfProvider.Postgresql;

        long schoolResourceKeyId = await PerfSeederDatabase.ExecuteScalarAsync(
            connection,
            postgresql
                ? PgsqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(
                    PerfAuthorizationSeedDefinition.SchoolResourceName
                )
                : MssqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(
                    PerfAuthorizationSeedDefinition.SchoolResourceName
                )
        );
        long ssaResourceKeyId = await PerfSeederDatabase.ExecuteScalarAsync(
            connection,
            postgresql
                ? PgsqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(
                    PerfAuthorizationSeedDefinition.StudentSchoolAssociationResourceName
                )
                : MssqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(
                    PerfAuthorizationSeedDefinition.StudentSchoolAssociationResourceName
                )
        );

        await SeedGradeLevelDescriptorAsync(connection, postgresql, seed);

        await ExecuteWithResourceKeyAsync(
            connection,
            postgresql
                ? PgsqlPerfAuthorizationSeedSql.SchoolDocumentInsertSql(seed)
                : MssqlPerfAuthorizationSeedSql.SchoolDocumentInsertSql(seed),
            schoolResourceKeyId
        );
        await PerfSeederDatabase.ExecuteNonQueryAsync(
            connection,
            postgresql
                ? PgsqlPerfAuthorizationSeedSql.SchoolInsertSql(seed)
                : MssqlPerfAuthorizationSeedSql.SchoolInsertSql(seed)
        );

        string ssaDocumentInsertSql = postgresql
            ? PgsqlPerfAuthorizationSeedSql.SsaDocumentInsertSql(seed)
            : MssqlPerfAuthorizationSeedSql.SsaDocumentInsertSql(seed);
        string ssaInsertSql = postgresql
            ? PgsqlPerfAuthorizationSeedSql.SsaInsertSql(seed)
            : MssqlPerfAuthorizationSeedSql.SsaInsertSql(seed);

        foreach ((long from, long to) in PerfFixtureLoader.Chunks(seed.EnrolledStudentCount, chunkSize))
        {
            await PerfSeederDatabase.ExecuteRangeAsync(
                connection,
                ssaDocumentInsertSql,
                from,
                to,
                [(PerfFixtureLoaderParameters.ResourceKeyId, ssaResourceKeyId)]
            );
            await PerfSeederDatabase.ExecuteRangeAsync(connection, ssaInsertSql, from, to, []);
        }

        await PerfSeederDatabase.ExecuteNonQueryAsync(
            connection,
            postgresql
                ? PgsqlPerfAuthorizationSeedSql.ReseedSql(seed)
                : MssqlPerfAuthorizationSeedSql.ReseedSql(seed)
        );

        IReadOnlyList<string> statisticsSqls = postgresql
            ? PgsqlPerfAuthorizationSeedSql.StatisticsRefreshSqls
            : MssqlPerfAuthorizationSeedSql.StatisticsRefreshSqls;
        foreach (string statisticsSql in statisticsSqls)
        {
            await PerfSeederDatabase.ExecuteNonQueryAsync(connection, statisticsSql);
        }

        await VerifyAsync(connection, provider, seed);
    }

    public static async Task VerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfAuthorizationSeedDefinition seed
    ) =>
        await PerfSeederDatabase.VerifyAsync(
            connection,
            provider == PerfProvider.Postgresql
                ? PgsqlPerfAuthorizationSeedSql.VerificationQueries(seed)
                : MssqlPerfAuthorizationSeedSql.VerificationQueries(seed)
        );

    /// <summary>
    /// The grade-level descriptor reuses the primary loader's descriptor statements, which
    /// mirror the production descriptor write; only the bound identity values differ.
    /// </summary>
    private static async Task SeedGradeLevelDescriptorAsync(
        DbConnection connection,
        bool postgresql,
        PerfAuthorizationSeedDefinition seed
    )
    {
        long descriptorResourceKeyId = await PerfSeederDatabase.ExecuteScalarAsync(
            connection,
            postgresql
                ? PgsqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(
                    PerfAuthorizationSeedDefinition.GradeLevelDescriptorResource
                )
                : MssqlPerfFixtureLoaderSql.DescriptorResourceKeyLookupSql(
                    PerfAuthorizationSeedDefinition.GradeLevelDescriptorResource
                )
        );

        await using (
            DbCommand documentInsert = PerfSeederDatabase.CreateCommand(
                connection,
                postgresql
                    ? PgsqlPerfFixtureLoaderSql.DescriptorDocumentInsertSql
                    : MssqlPerfFixtureLoaderSql.DescriptorDocumentInsertSql
            )
        )
        {
            PerfSeederDatabase.AddParameter(
                documentInsert,
                PerfFixtureLoaderParameters.DescriptorDocumentId,
                seed.GradeLevelDescriptorDocumentId
            );
            PerfSeederDatabase.AddObjectParameter(
                documentInsert,
                PerfFixtureLoaderParameters.DescriptorDocumentUuid,
                PerfAuthorizationSeedDefinition.GradeLevelDescriptorDocumentUuid
            );
            PerfSeederDatabase.AddParameter(
                documentInsert,
                PerfFixtureLoaderParameters.ResourceKeyId,
                descriptorResourceKeyId
            );
            await documentInsert.ExecuteNonQueryAsync();
        }

        await using (
            DbCommand descriptorInsert = PerfSeederDatabase.CreateCommand(
                connection,
                postgresql
                    ? PgsqlPerfFixtureLoaderSql.DescriptorInsertSql(
                        PerfAuthorizationSeedDefinition.GradeLevelDescriptorResource
                    )
                    : MssqlPerfFixtureLoaderSql.DescriptorInsertSql(
                        PerfAuthorizationSeedDefinition.GradeLevelDescriptorResource
                    )
            )
        )
        {
            PerfSeederDatabase.AddParameter(
                descriptorInsert,
                PerfFixtureLoaderParameters.DescriptorDocumentId,
                seed.GradeLevelDescriptorDocumentId
            );
            PerfSeederDatabase.AddParameter(
                descriptorInsert,
                PerfFixtureLoaderParameters.ResourceKeyId,
                descriptorResourceKeyId
            );
            await descriptorInsert.ExecuteNonQueryAsync();
        }

        await using DbCommand referentialInsert = PerfSeederDatabase.CreateCommand(
            connection,
            postgresql
                ? PgsqlPerfFixtureLoaderSql.DescriptorReferentialIdentityInsertSql
                : MssqlPerfFixtureLoaderSql.DescriptorReferentialIdentityInsertSql
        );
        PerfSeederDatabase.AddObjectParameter(
            referentialInsert,
            PerfFixtureLoaderParameters.DescriptorReferentialId,
            ReferentialIdentityDerivation.DescriptorReferentialId(
                PerfAuthorizationSeedDefinition.GradeLevelDescriptorResource,
                PerfAuthorizationSeedDefinition.GradeLevelDescriptorUri
            )
        );
        PerfSeederDatabase.AddParameter(
            referentialInsert,
            PerfFixtureLoaderParameters.DescriptorDocumentId,
            seed.GradeLevelDescriptorDocumentId
        );
        PerfSeederDatabase.AddParameter(
            referentialInsert,
            PerfFixtureLoaderParameters.ResourceKeyId,
            descriptorResourceKeyId
        );
        await referentialInsert.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteWithResourceKeyAsync(
        DbConnection connection,
        string sql,
        long resourceKeyId
    )
    {
        await using DbCommand command = PerfSeederDatabase.CreateCommand(connection, sql);
        PerfSeederDatabase.AddParameter(command, PerfFixtureLoaderParameters.ResourceKeyId, resourceKeyId);
        await command.ExecuteNonQueryAsync();
    }
}
