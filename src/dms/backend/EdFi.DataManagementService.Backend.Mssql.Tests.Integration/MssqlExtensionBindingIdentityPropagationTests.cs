// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Runtime proof that a rename of an upstream resource's identity reaches a stored
/// <em>extension</em> reference binding through the native <c>ON UPDATE CASCADE</c> foreign key,
/// and that the extension stamp trigger then bumps the owning root document's
/// <c>ContentVersion</c>. This is the extension-scope counterpart to
/// <see cref="MssqlChildBindingIdentityPropagationTests"/> (which covers child-collection
/// bindings): here the cascade lands in the sample-project root extension table
/// <c>[sample].[StudentEducationOrganizationAssociationExtension]</c>, whose
/// <c>FavoriteProgram_RefKey</c> foreign key retains <c>ON UPDATE CASCADE</c> toward the mutable
/// <c>edfi.Program</c> identity. Per design-docs/sql-server-pruning.md, SQL Server integration
/// coverage must prove that existing stamping still observes cascaded extension updates.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Provisioned_Mssql_Database_With_A_FavoriteProgram_Extension_Binding
{
    // The FavoriteProgram reference is a sample-extension property, so this exercises the
    // authoritative sample surface (which loads the sample extension project).
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    // Synthetic anchors: EducationOrganization / Student / descriptor rows are not seeded; the
    // relevant reference FKs are disabled for the synthetic INSERTs, so these values only have to
    // be internally consistent between the Program row and the extension binding that references it.
    private const long EducationOrganizationDocumentIdAnchor = -101L;
    private const long EducationOrganizationId = 255901001L;
    private const long StudentDocumentIdAnchor = -102L;
    private const string StudentUniqueId = "STU-EXT-1";
    private const long ProgramTypeDescriptorId = -103L;

    private const string OldProgramName = "Gifted and Talented";
    private const string NewProgramName = "Gifted and Talented (Renamed)";

    private const string FavoriteProgramDescriptorFkName =
        "FK_StudentEducationOrganizationAssociationExtension_FavoriteProgram_ProgramTypeDescriptor";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: true,
            _fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_should_propagate_a_Program_identity_rename_to_the_FavoriteProgram_extension_binding_and_bump_the_owning_root_ContentVersion()
    {
        // Arrange — seed:
        //   StudentEducationOrganizationAssociation (the owning root of the extension)
        //   Program (the upstream mutable identity that will be renamed)
        //   StudentEducationOrganizationAssociationExtension.FavoriteProgram_* binding referencing
        //     that Program through the retained ON UPDATE CASCADE FavoriteProgram_RefKey FK.
        var rootDocumentId = await InsertStudentEducationOrganizationAssociationAsync();
        var programDocumentId = await InsertProgramAsync(OldProgramName);
        await InsertFavoriteProgramExtensionAsync(rootDocumentId, programDocumentId, OldProgramName);

        // Record baseline AFTER seeding: the extension INSERT itself stamps the owning root, so the
        // baseline must be captured post-insert to isolate the cascade-driven bump under test.
        var initialContentVersion = await QueryDocumentContentVersionAsync(rootDocumentId);
        var bindingRowsBefore = await QueryFavoriteProgramExtensionRowCountAsync(rootDocumentId);
        var bindingAnchorBefore = await QueryFavoriteProgramExtensionAnchorAsync(rootDocumentId);
        bindingRowsBefore.Should().Be(1);

        // Small delay so a stamp comparison that also checks ContentLastModifiedAt sees a distinct
        // timestamp.
        await _database.ExecuteNonQueryAsync("WAITFOR DELAY '00:00:00.050';");

        // Act — rename the upstream Program identity column.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[Program]
            SET [ProgramName] = @newName
            WHERE [DocumentId] = @programDocumentId;
            """,
            new SqlParameter("@newName", NewProgramName),
            new SqlParameter("@programDocumentId", programDocumentId)
        );

        // Assert — the native cascade updated the projected identity column on the extension row.
        var bindingRow = await QuerySingleFavoriteProgramExtensionAsync(rootDocumentId);
        Convert
            .ToString(bindingRow["FavoriteProgram_ProgramName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewProgramName, "the native ON UPDATE CASCADE must propagate the rename into the extension");

        // Row count unchanged — the cascade is an UPDATE, not an INSERT/DELETE.
        var bindingRowsAfter = await QueryFavoriteProgramExtensionRowCountAsync(rootDocumentId);
        bindingRowsAfter
            .Should()
            .Be(
                bindingRowsBefore,
                "the cascade must update the projected identity column, not insert/delete rows"
            );

        // The FK anchor (FavoriteProgram_DocumentId) must NOT change — propagation only touches the
        // projected non-key identity columns, never the reference link itself.
        var bindingAnchorAfter = await QueryFavoriteProgramExtensionAnchorAsync(rootDocumentId);
        bindingAnchorAfter
            .Should()
            .Be(
                bindingAnchorBefore,
                "FavoriteProgram_DocumentId is the reference anchor and must not change during an identity cascade"
            );

        // The extension stamp trigger (TR_StudentEducationOrganizationAssociationExtension_Stamp)
        // must fire from the cascade UPDATE and bump the owning root's ContentVersion.
        var finalContentVersion = await QueryDocumentContentVersionAsync(rootDocumentId);
        finalContentVersion
            .Should()
            .BeGreaterThan(
                initialContentVersion,
                "the extension stamp trigger must fire from the cascade UPDATE and bump the owning root ContentVersion"
            );
    }

    private async Task<long> InsertStudentEducationOrganizationAssociationAsync()
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "StudentEducationOrganizationAssociation");
        var documentId = await InsertDocumentAsync(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            resourceKeyId
        );

        // The EducationOrganization / Student references point at synthetic anchors, so the
        // reference FKs and this table's own triggers are disabled for the seed insert; none of
        // that upstream chain is needed to exercise the Program → FavoriteProgram cascade.
        await _database.ExecuteNonQueryAsync(
            "DISABLE TRIGGER ALL ON [edfi].[StudentEducationOrganizationAssociation];"
        );
        await _database.ExecuteNonQueryAsync(
            "ALTER TABLE [edfi].[StudentEducationOrganizationAssociation] NOCHECK CONSTRAINT ALL;"
        );

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[StudentEducationOrganizationAssociation] (
                    [DocumentId],
                    [EducationOrganization_DocumentId],
                    [EducationOrganization_EducationOrganizationId],
                    [Student_DocumentId],
                    [Student_StudentUniqueId]
                )
                VALUES (
                    @documentId,
                    @educationOrganizationDocumentId,
                    @educationOrganizationId,
                    @studentDocumentId,
                    @studentUniqueId
                );
                """,
                new SqlParameter("@documentId", documentId),
                new SqlParameter("@educationOrganizationDocumentId", EducationOrganizationDocumentIdAnchor),
                new SqlParameter("@educationOrganizationId", EducationOrganizationId),
                new SqlParameter("@studentDocumentId", StudentDocumentIdAnchor),
                new SqlParameter("@studentUniqueId", StudentUniqueId)
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync(
                "ALTER TABLE [edfi].[StudentEducationOrganizationAssociation] CHECK CONSTRAINT ALL;"
            );
            await _database.ExecuteNonQueryAsync(
                "ENABLE TRIGGER ALL ON [edfi].[StudentEducationOrganizationAssociation];"
            );
        }

        return documentId;
    }

    private async Task<long> InsertProgramAsync(string programName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync("Ed-Fi", "Program");
        var documentId = await InsertDocumentAsync(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            resourceKeyId
        );

        // EducationOrganization and ProgramTypeDescriptor point at synthetic anchors, so this
        // table's reference FKs are disabled for the seed insert. Program's own triggers stay
        // disabled for the whole test: with a synthetic EducationOrganization anchor (no seeded
        // dms.ReferentialIdentity alias row) Program's identity-maintenance trigger would compute a
        // NULL ReferentialId on the rename. That machinery is orthogonal to the behavior under
        // test — the FavoriteProgram_RefKey cascade is a native FK action and the extension stamp
        // trigger lives on the extension table, so both fire independently of Program's own
        // triggers (the behavior under test). Constraints are re-enabled (untrusted) after the
        // insert; the rename only touches ProgramName, which is not one of Program's FK columns.
        await _database.ExecuteNonQueryAsync("DISABLE TRIGGER ALL ON [edfi].[Program];");
        await _database.ExecuteNonQueryAsync("ALTER TABLE [edfi].[Program] NOCHECK CONSTRAINT ALL;");

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[Program] (
                    [DocumentId],
                    [EducationOrganization_DocumentId],
                    [EducationOrganization_EducationOrganizationId],
                    [ProgramTypeDescriptor_DescriptorId],
                    [ProgramName]
                )
                VALUES (
                    @documentId,
                    @educationOrganizationDocumentId,
                    @educationOrganizationId,
                    @programTypeDescriptorId,
                    @programName
                );
                """,
                new SqlParameter("@documentId", documentId),
                new SqlParameter("@educationOrganizationDocumentId", EducationOrganizationDocumentIdAnchor),
                new SqlParameter("@educationOrganizationId", EducationOrganizationId),
                new SqlParameter("@programTypeDescriptorId", ProgramTypeDescriptorId),
                new SqlParameter("@programName", programName)
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("ALTER TABLE [edfi].[Program] CHECK CONSTRAINT ALL;");
        }

        return documentId;
    }

    private async Task InsertFavoriteProgramExtensionAsync(
        long studentEducationOrganizationAssociationDocumentId,
        long programDocumentId,
        string programName
    )
    {
        // The FavoriteProgram binding stores the four-part Program RefKey tuple
        // (EducationOrganizationId, ProgramName, ProgramTypeDescriptorId, DocumentId), matching the
        // seeded Program row so the ON UPDATE CASCADE FavoriteProgram_RefKey FK is satisfied and
        // stays enabled — that FK is the cascade path under test.
        //
        // The ProgramTypeDescriptor is a synthetic anchor (no dms.Descriptor row is seeded). A
        // Program identity rename cascades a rewrite of the FULL FavoriteProgram_* tuple into this
        // row — including the descriptor column, even though only ProgramName changed — so the
        // descriptor FK is left disabled for the whole test; re-enabling it would fail the cascade
        // UPDATE (error 547) against the unseeded descriptor. This mirrors the synthetic-anchor
        // accommodation used for the EducationOrganization/Student references above.
        await _database.ExecuteNonQueryAsync(
            $"ALTER TABLE [sample].[StudentEducationOrganizationAssociationExtension] NOCHECK CONSTRAINT [{FavoriteProgramDescriptorFkName}];"
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[StudentEducationOrganizationAssociationExtension] (
                [DocumentId],
                [FavoriteProgram_DocumentId],
                [FavoriteProgram_EducationOrganizationId],
                [FavoriteProgram_ProgramName],
                [FavoriteProgram_ProgramTypeDescriptor_DescriptorId]
            )
            VALUES (
                @documentId,
                @favoriteProgramDocumentId,
                @favoriteProgramEducationOrganizationId,
                @favoriteProgramName,
                @favoriteProgramTypeDescriptorId
            );
            """,
            new SqlParameter("@documentId", studentEducationOrganizationAssociationDocumentId),
            new SqlParameter("@favoriteProgramDocumentId", programDocumentId),
            new SqlParameter("@favoriteProgramEducationOrganizationId", EducationOrganizationId),
            new SqlParameter("@favoriteProgramName", programName),
            new SqlParameter("@favoriteProgramTypeDescriptorId", ProgramTypeDescriptorId)
        );
    }

    private async Task<long> QueryDocumentContentVersionAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<int> QueryFavoriteProgramExtensionRowCountAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [sample].[StudentEducationOrganizationAssociationExtension]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<long> QueryFavoriteProgramExtensionAnchorAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [FavoriteProgram_DocumentId]
            FROM [sample].[StudentEducationOrganizationAssociationExtension]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> QuerySingleFavoriteProgramExtensionAsync(
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                [DocumentId],
                [FavoriteProgram_DocumentId],
                [FavoriteProgram_EducationOrganizationId],
                [FavoriteProgram_ProgramName],
                [FavoriteProgram_ProgramTypeDescriptor_DescriptorId]
            FROM [sample].[StudentEducationOrganizationAssociationExtension]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Single();
    }

    private async Task<long> InsertDocumentAsync(Guid documentUuid, short resourceKeyId)
    {
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
}
