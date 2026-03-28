// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record DateTimeIdentitySeedData(
    long DocumentId,
    string SessionCode,
    DateTimeOffset EntryTimestamp
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Generated_Ddl_Apply_Harness_With_A_Focused_DateTime_Identity_Fixture
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/datetime-identity";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private DateTimeIdentitySeedData _seedData = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _seedData = await SeedDateTimeIdentityRowAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_should_store_a_referential_id_that_matches_the_canonical_utc_datetime_identity()
    {
        var canonicalEntryTimestamp = _seedData
            .EntryTimestamp.ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

        DocumentIdentity documentIdentity = new([
            new(new JsonPath("$.sessionCode"), _seedData.SessionCode),
            new(new JsonPath("$.entryTimestamp"), canonicalEntryTimestamp),
        ]);
        BaseResourceInfo resourceInfo = new(new("Ed-Fi"), new("TimedSession"), false);
        var expectedReferentialId = ReferentialIdCalculator
            .ReferentialIdFrom(resourceInfo, documentIdentity)
            .Value;

        var actualReferentialId = await _database.ExecuteScalarAsync<Guid>(
            """
            SELECT [ReferentialId]
            FROM [dms].[ReferentialIdentity]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", _seedData.DocumentId)
        );

        actualReferentialId.Should().Be(expectedReferentialId);
    }

    private async Task<DateTimeIdentitySeedData> SeedDateTimeIdentityRowAsync()
    {
        const string sessionCode = "MorningWindow";
        const string entryTimestampLiteral = "2025-03-05T08:30:45-05:00";

        var entryTimestamp = DateTimeOffset.ParseExact(
            entryTimestampLiteral,
            "yyyy-MM-ddTHH:mm:sszzz",
            CultureInfo.InvariantCulture
        );

        var documentId = await InsertDocumentAsync(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "Ed-Fi",
            "TimedSession"
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[TimedSession] ([DocumentId], [SessionCode], [EntryTimestamp])
            VALUES (@documentId, @sessionCode, @entryTimestamp);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@sessionCode", sessionCode),
            new SqlParameter("@entryTimestamp", SqlDbType.DateTime2) { Value = entryTimestamp.UtcDateTime }
        );

        return new(documentId, sessionCode, entryTimestamp);
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }
}
