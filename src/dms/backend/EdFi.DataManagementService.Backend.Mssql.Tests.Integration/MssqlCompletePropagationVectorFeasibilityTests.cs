// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_The_Widest_Complete_Propagation_Vector_On_Sql_Server
{
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private int _encodedPayloadBytes;
    private long _matchingChildRows;

    [OneTimeSetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await _database.ExecuteNonQueryAsync(
            """
            CREATE TABLE [dbo].[WideVectorTarget]
            (
                [Namespace] nvarchar(255) NOT NULL,
                [SurveyIdentifier] nvarchar(60) NOT NULL,
                [ResponseIdentifier] nvarchar(60) NOT NULL,
                [SectionTitle] nvarchar(255) NOT NULL,
                [ResponseDocumentId] bigint NOT NULL,
                [ResponseSurveyDocumentId] bigint NOT NULL,
                [SectionDocumentId] bigint NOT NULL,
                [SectionSurveyDocumentId] bigint NOT NULL,
                [DocumentId] bigint NOT NULL,
                CONSTRAINT [UX_WideVectorTarget_PropagationKey] UNIQUE
                (
                    [Namespace],
                    [SurveyIdentifier],
                    [ResponseIdentifier],
                    [SectionTitle],
                    [ResponseDocumentId],
                    [ResponseSurveyDocumentId],
                    [SectionDocumentId],
                    [SectionSurveyDocumentId],
                    [DocumentId]
                )
            );

            CREATE TABLE [dbo].[WideVectorChild]
            (
                [ChildId] bigint NOT NULL PRIMARY KEY,
                [Namespace] nvarchar(255) NOT NULL,
                [SurveyIdentifier] nvarchar(60) NOT NULL,
                [ResponseIdentifier] nvarchar(60) NOT NULL,
                [SectionTitle] nvarchar(255) NOT NULL,
                [ResponseDocumentId] bigint NOT NULL,
                [ResponseSurveyDocumentId] bigint NOT NULL,
                [SectionDocumentId] bigint NOT NULL,
                [SectionSurveyDocumentId] bigint NOT NULL,
                [TargetDocumentId] bigint NOT NULL,
                CONSTRAINT [FK_WideVectorChild_Target] FOREIGN KEY
                (
                    [Namespace],
                    [SurveyIdentifier],
                    [ResponseIdentifier],
                    [SectionTitle],
                    [ResponseDocumentId],
                    [ResponseSurveyDocumentId],
                    [SectionDocumentId],
                    [SectionSurveyDocumentId],
                    [TargetDocumentId]
                )
                REFERENCES [dbo].[WideVectorTarget]
                (
                    [Namespace],
                    [SurveyIdentifier],
                    [ResponseIdentifier],
                    [SectionTitle],
                    [ResponseDocumentId],
                    [ResponseSurveyDocumentId],
                    [SectionDocumentId],
                    [SectionSurveyDocumentId],
                    [DocumentId]
                )
            );

            INSERT INTO [dbo].[WideVectorTarget]
            VALUES
            (
                REPLICATE(N'N', 255),
                REPLICATE(N'S', 60),
                REPLICATE(N'R', 60),
                REPLICATE(N'T', 255),
                1,
                2,
                3,
                4,
                5
            );

            INSERT INTO [dbo].[WideVectorChild]
            VALUES
            (
                1,
                REPLICATE(N'N', 255),
                REPLICATE(N'S', 60),
                REPLICATE(N'R', 60),
                REPLICATE(N'T', 255),
                1,
                2,
                3,
                4,
                5
            );
            """
        );

        _encodedPayloadBytes = await _database.ExecuteScalarAsync<int>(
            """
            SELECT
                DATALENGTH([Namespace])
                + DATALENGTH([SurveyIdentifier])
                + DATALENGTH([ResponseIdentifier])
                + DATALENGTH([SectionTitle])
                + 40
            FROM [dbo].[WideVectorTarget];
            """
        );
        _matchingChildRows = await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT_BIG(*)
            FROM [dbo].[WideVectorChild] AS child
            INNER JOIN [dbo].[WideVectorTarget] AS target
                ON target.[Namespace] = child.[Namespace]
               AND target.[SurveyIdentifier] = child.[SurveyIdentifier]
               AND target.[ResponseIdentifier] = child.[ResponseIdentifier]
               AND target.[SectionTitle] = child.[SectionTitle]
               AND target.[ResponseDocumentId] = child.[ResponseDocumentId]
               AND target.[ResponseSurveyDocumentId] = child.[ResponseSurveyDocumentId]
               AND target.[SectionDocumentId] = child.[SectionDocumentId]
               AND target.[SectionSurveyDocumentId] = child.[SectionSurveyDocumentId]
               AND target.[DocumentId] = child.[TargetDocumentId];
            """
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_accepts_the_maximum_declared_full_vector_payload()
    {
        _encodedPayloadBytes.Should().Be(1300);
        _matchingChildRows.Should().Be(1);
    }
}
