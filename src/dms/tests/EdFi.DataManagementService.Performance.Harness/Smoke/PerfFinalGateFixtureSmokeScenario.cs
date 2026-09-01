// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Tests.Integration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The final-gate fixture proof gates, run live at smoke scale on either provider.
///
/// Primary variants: after the 10,000-row primary load, the authorization seeding and the
/// filtered overlay run in their mandatory order. One control StudentSchoolAssociation is
/// POSTed through the real HTTP pipeline for a student the seed left unenrolled, and the
/// seeded rows must match its row shape across dms.Document, the association's shared
/// columns, and the trigger-derived referential identity — validating the independent SSA
/// uuidv5 formula against a production write before holding seeded rows to it. The
/// authorization view must contain exactly the seeded students plus the control enrollment.
/// The overlay must change only the selected birth dates: no tracked-change rows, and a real
/// filtered GET must return exactly the overlaid students in DocumentId order.
///
/// Descriptor fixture: after the 2,000-row descriptor load, one control descriptor is POSTed
/// and the loader rows must match its document metadata, descriptor row semantics, and
/// referential identity derivation; a real GET-many must hydrate the interleaved namespaces
/// in DocumentId order.
///
/// All assertion SQL uses double-quoted identifiers, which both providers accept.
/// </summary>
internal static class PerfFinalGateFixtureSmokeScenario
{
    private const string TrackedStudentChangesCountSql = """
        SELECT COUNT(*) FROM "tracked_changes_edfi"."Student";
        """;

    private const string DocumentRowSql = """
        SELECT "DocumentId", "ResourceKeyId", "CreatedByOwnershipTokenId", "ContentVersion"
        FROM "dms"."Document"
        WHERE "DocumentUuid" = @uuid;
        """;

    private const string SsaRowSql = """
        SELECT "SchoolId_Unified", "School_DocumentId", "Student_DocumentId", "Student_StudentUniqueId", "EntryGradeLevelDescriptor_DescriptorId", "ContentVersion"
        FROM "edfi"."StudentSchoolAssociation"
        WHERE "DocumentId" = @documentId;
        """;

    private const string ReferentialIdentityRowsSql = """
        SELECT "ReferentialId"
        FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = @documentId;
        """;

    private const string DescriptorRowSql = """
        SELECT "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri", "ContentVersion"
        FROM "dms"."Descriptor"
        WHERE "DocumentId" = @documentId;
        """;

    private const string AuthorizedViewMembershipSql = """
        SELECT COUNT(DISTINCT "Student_DocumentId")
        FROM "auth"."EducationOrganizationIdToStudentDocumentId"
        WHERE "SourceEducationOrganizationId" = @schoolId;
        """;

    private const string AuthorizedViewContainsStudentSql = """
        SELECT COUNT(*)
        FROM "auth"."EducationOrganizationIdToStudentDocumentId"
        WHERE "SourceEducationOrganizationId" = @schoolId AND "Student_DocumentId" = @documentId;
        """;

    private sealed record DocumentRow(
        long DocumentId,
        long ResourceKeyId,
        bool HasOwnershipToken,
        long ContentVersion
    );

    private sealed record SsaRow(
        long SchoolIdUnified,
        long SchoolDocumentId,
        long StudentDocumentId,
        string StudentUniqueId,
        long EntryGradeLevelDescriptorId,
        long ContentVersion
    );

    private sealed record DescriptorRow(
        long ResourceKeyId,
        string Namespace,
        string CodeValue,
        string ShortDescription,
        string Discriminator,
        string Uri,
        long ContentVersion
    );

    public static async Task RunPrimaryVariantsAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        PerfAuthorizationSeedDefinition seed = new(definition);
        DbConnection connection = harness.DbConnection;

        await PerfFixtureLoader.LoadAndVerifyAsync(connection, provider, definition);
        await PerfAuthorizationSeeder.SeedAndVerifyAsync(connection, provider, seed);

        // The control association enrolls student ordinal 1, which the even-ordinal seed
        // left unenrolled, so the production write is a new row under the identical shape.
        const long ControlStudentOrdinal = 1;
        Guid controlUuid = await PostControlSsaAsync(
            harness,
            PerfFixtureDefinition.StudentUniqueIdFor(ControlStudentOrdinal)
        );

        DocumentRow controlDocument = await ReadDocumentRowAsync(connection, controlUuid);
        controlDocument
            .DocumentId.Should()
            .Be(
                seed.ReseedTargetDocumentId + 1,
                "the reseed must hand the id after the association block to the write path"
            );

        DocumentRow seededDocument = await ReadDocumentRowAsync(
            connection,
            PerfAuthorizationSeedDefinition.SsaDocumentUuidFor(1)
        );
        seededDocument.DocumentId.Should().Be(seed.SsaDocumentIdBase + 1);
        seededDocument.ResourceKeyId.Should().Be(controlDocument.ResourceKeyId);
        seededDocument.HasOwnershipToken.Should().Be(controlDocument.HasOwnershipToken);
        seededDocument.ContentVersion.Should().BePositive();
        controlDocument.ContentVersion.Should().BePositive();

        SsaRow seededSsa = await ReadSsaRowAsync(connection, seededDocument.DocumentId);
        SsaRow controlSsa = await ReadSsaRowAsync(connection, controlDocument.DocumentId);
        seededSsa.SchoolIdUnified.Should().Be(controlSsa.SchoolIdUnified);
        seededSsa.SchoolDocumentId.Should().Be(controlSsa.SchoolDocumentId);
        seededSsa.EntryGradeLevelDescriptorId.Should().Be(controlSsa.EntryGradeLevelDescriptorId);
        seededSsa.EntryGradeLevelDescriptorId.Should().Be(seed.GradeLevelDescriptorDocumentId);
        seededSsa
            .StudentDocumentId.Should()
            .Be(
                PerfFixtureDefinition.DocumentIdFor(PerfAuthorizationSeedDefinition.EnrolledStudentOrdinal(1))
            );
        seededSsa.StudentUniqueId.Should().Be(PerfFixtureDefinition.StudentUniqueIdFor(2));
        seededSsa
            .ContentVersion.Should()
            .Be(seededDocument.ContentVersion, "the stamp trigger must have mirrored the document version");
        controlSsa.ContentVersion.Should().Be(controlDocument.ContentVersion);

        // Validate the independent uuidv5 formula against the control row the production
        // write path produced, then hold the seeded row to it.
        await AssertSsaReferentialIdentityAsync(
            connection,
            controlDocument.DocumentId,
            PerfFixtureDefinition.StudentUniqueIdFor(ControlStudentOrdinal)
        );
        await AssertSsaReferentialIdentityAsync(
            connection,
            seededDocument.DocumentId,
            PerfFixtureDefinition.StudentUniqueIdFor(2)
        );

        long viewMembership = await CountAsync(
            connection,
            AuthorizedViewMembershipSql,
            ("schoolId", PerfAuthorizationSeedDefinition.SchoolId)
        );
        viewMembership
            .Should()
            .Be(
                seed.EnrolledStudentCount + 1,
                "the authorization view must hold the seeded students plus the control enrollment"
            );
        long controlStudentInView = await CountAsync(
            connection,
            AuthorizedViewContainsStudentSql,
            ("schoolId", PerfAuthorizationSeedDefinition.SchoolId),
            ("documentId", PerfFixtureDefinition.DocumentIdFor(ControlStudentOrdinal))
        );
        controlStudentInView.Should().Be(1);

        await PerfFilteredOverlay.ApplyAndVerifyAsync(connection, provider, definition);

        long trackedStudentChanges = await CountAsync(connection, TrackedStudentChangesCountSql);
        trackedStudentChanges
            .Should()
            .Be(0, "a birth-date overlay must not write identity tracked-change rows");

        await AssertFilteredPageAsync(harness, definition);
    }

    public static async Task RunDescriptorFixtureAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfDescriptorFixtureDefinition definition = new(PerfDescriptorFixtureKind.DescriptorsSmoke2k);
        DbConnection connection = harness.DbConnection;

        await PerfDescriptorFixtureLoader.LoadAndVerifyAsync(connection, provider, definition);

        Guid controlUuid = await PostControlDescriptorAsync(harness);
        DocumentRow controlDocument = await ReadDocumentRowAsync(connection, controlUuid);
        controlDocument
            .DocumentId.Should()
            .Be(
                definition.ReseedTargetDocumentId + 1,
                "the reseed must hand the id after the fixture to the write path"
            );
        DescriptorRow controlDescriptor = await ReadDescriptorRowAsync(
            connection,
            controlDocument.DocumentId
        );

        DocumentRow loaderDocument = await ReadDocumentRowAsync(
            connection,
            PerfDescriptorFixtureDefinition.DocumentUuidFor(1)
        );
        loaderDocument.DocumentId.Should().Be(PerfDescriptorFixtureDefinition.DocumentIdFor(1));
        loaderDocument.ResourceKeyId.Should().Be(controlDocument.ResourceKeyId);
        loaderDocument.HasOwnershipToken.Should().Be(controlDocument.HasOwnershipToken);

        DescriptorRow loaderDescriptor = await ReadDescriptorRowAsync(connection, loaderDocument.DocumentId);
        loaderDescriptor.ResourceKeyId.Should().Be(controlDescriptor.ResourceKeyId);
        loaderDescriptor.Discriminator.Should().Be(controlDescriptor.Discriminator);
        loaderDescriptor.Namespace.Should().Be(PerfDescriptorFixtureDefinition.AccessibleNamespace);
        loaderDescriptor.CodeValue.Should().Be(PerfDescriptorFixtureDefinition.CodeValueFor(1));
        loaderDescriptor.ShortDescription.Should().Be(loaderDescriptor.CodeValue);
        loaderDescriptor
            .Uri.Should()
            .Be(
                $"{loaderDescriptor.Namespace}#{loaderDescriptor.CodeValue}",
                "the loader must derive Uri exactly as the production write path does"
            );
        controlDescriptor.Uri.Should().Be($"{controlDescriptor.Namespace}#{controlDescriptor.CodeValue}");
        loaderDescriptor
            .ContentVersion.Should()
            .Be(
                loaderDocument.ContentVersion,
                "the descriptor stamp trigger must mirror the document version"
            );

        // Validate the descriptor referential-id formula against the control row, then hold
        // the loader row to it.
        await AssertDescriptorReferentialIdentityAsync(
            connection,
            controlDocument.DocumentId,
            controlDescriptor.Uri
        );
        await AssertDescriptorReferentialIdentityAsync(
            connection,
            loaderDocument.DocumentId,
            PerfDescriptorFixtureDefinition.UriFor(1)
        );

        await AssertDescriptorFirstPageAsync(harness);
    }

    private static async Task<Guid> PostControlSsaAsync(ApiIntegrationHarness harness, string studentUniqueId)
    {
        JsonObject payload = new()
        {
            ["entryDate"] = PerfAuthorizationSeedDefinition.EntryDateIso,
            ["entryGradeLevelDescriptor"] = PerfAuthorizationSeedDefinition.GradeLevelDescriptorUri,
            ["schoolReference"] = new JsonObject { ["schoolId"] = PerfAuthorizationSeedDefinition.SchoolId },
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
        };
        return await PostForLocationIdAsync(harness, "/data/ed-fi/studentSchoolAssociations", payload);
    }

    private static async Task<Guid> PostControlDescriptorAsync(ApiIntegrationHarness harness)
    {
        JsonObject payload = new()
        {
            ["codeValue"] = "Control",
            ["shortDescription"] = "Control",
            ["namespace"] = PerfDescriptorFixtureDefinition.AccessibleNamespace,
        };
        return await PostForLocationIdAsync(
            harness,
            PerfDescriptorFixtureDefinition.ResourceEndpoint,
            payload
        );
    }

    private static async Task<Guid> PostForLocationIdAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(endpoint, content);
        string body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        return Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);
    }

    /// <summary>
    /// The filtered page must hold exactly the first page of overlaid students in
    /// DocumentId order — the overlay's ten-percent selection served through the real
    /// query path, not just counted in the database.
    /// </summary>
    private static async Task AssertFilteredPageAsync(
        ApiIntegrationHarness harness,
        PerfFixtureDefinition definition
    )
    {
        const int PageSize = 25;
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{PerfFixtureDefinition.ResourceEndpoint}?birthDate={PerfFilteredOverlay.OverlayBirthDateIso}&limit={PageSize}&offset=0"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonArray items = JsonNode.Parse(body)!.AsArray();
        items.Should().HaveCount(PageSize);
        PerfFilteredOverlay
            .OverlaidStudentCount(definition)
            .Should()
            .BeGreaterThanOrEqualTo(PageSize, "the smoke fixture must fill a whole filtered page");

        for (int index = 0; index < PageSize; index++)
        {
            long ordinal = PerfFilteredOverlay.OverlaidStudentOrdinal(index + 1);
            Guid id = Guid.Parse(items[index]!["id"]!.GetValue<string>());
            id.Should().Be(PerfFixtureDefinition.DocumentUuidFor(ordinal), $"filtered item {index}");
            items[index]!["birthDate"]!
                .GetValue<string>()
                .Should()
                .Be(PerfFilteredOverlay.OverlayBirthDateIso);
        }
    }

    private static async Task AssertDescriptorFirstPageAsync(ApiIntegrationHarness harness)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{PerfDescriptorFixtureDefinition.ResourceEndpoint}?limit=25&offset=0"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonArray items = JsonNode.Parse(body)!.AsArray();
        items.Should().HaveCount(25);

        for (int index = 0; index < items.Count; index++)
        {
            long ordinal = index + 1;
            Guid id = Guid.Parse(items[index]!["id"]!.GetValue<string>());
            id.Should().Be(PerfDescriptorFixtureDefinition.DocumentUuidFor(ordinal), $"item {index}");
            items[index]!["codeValue"]!
                .GetValue<string>()
                .Should()
                .Be(PerfDescriptorFixtureDefinition.CodeValueFor(ordinal));
            items[index]!["namespace"]!
                .GetValue<string>()
                .Should()
                .Be(PerfDescriptorFixtureDefinition.NamespaceFor(ordinal));
        }
    }

    private static async Task AssertSsaReferentialIdentityAsync(
        DbConnection connection,
        long documentId,
        string studentUniqueId
    )
    {
        Guid expected = ReferentialIdentityDerivation.StudentSchoolAssociationReferentialId(
            PerfAuthorizationSeedDefinition.EntryDateIso,
            PerfAuthorizationSeedDefinition.SchoolId,
            studentUniqueId
        );
        Guid actual = await ReadSingleReferentialIdAsync(connection, documentId);
        actual
            .Should()
            .Be(
                expected,
                $"the association referential id for '{studentUniqueId}' must match the independent uuidv5 derivation"
            );
    }

    private static async Task AssertDescriptorReferentialIdentityAsync(
        DbConnection connection,
        long documentId,
        string uri
    )
    {
        Guid expected = ReferentialIdentityDerivation.DescriptorReferentialId(
            PerfDescriptorFixtureDefinition.ResourceName,
            uri
        );
        Guid actual = await ReadSingleReferentialIdAsync(connection, documentId);
        actual
            .Should()
            .Be(expected, $"the referential id for '{uri}' must match the independent uuidv5 derivation");
    }

    private static async Task<Guid> ReadSingleReferentialIdAsync(DbConnection connection, long documentId)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = ReferentialIdentityRowsSql;
        AddParameter(command, "documentId", documentId);

        List<Guid> rows = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetGuid(0));
            }
        }

        return rows.Should()
            .ContainSingle($"document {documentId} must carry exactly one referential identity")
            .Subject;
    }

    private static async Task<long> CountAsync(
        DbConnection connection,
        string sql,
        params (string Name, long Value)[] parameters
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, long value) in parameters)
        {
            AddParameter(command, name, value);
        }

        object? scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<DocumentRow> ReadDocumentRowAsync(DbConnection connection, Guid documentUuid)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = DocumentRowSql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "uuid";
        parameter.Value = documentUuid;
        command.Parameters.Add(parameter);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"document {documentUuid} must exist");
        return new DocumentRow(
            reader.GetInt64(0),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            !await reader.IsDBNullAsync(2),
            reader.GetInt64(3)
        );
    }

    private static async Task<SsaRow> ReadSsaRowAsync(DbConnection connection, long documentId)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = SsaRowSql;
        AddParameter(command, "documentId", documentId);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"association {documentId} must exist");
        return new SsaRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5)
        );
    }

    private static async Task<DescriptorRow> ReadDescriptorRowAsync(DbConnection connection, long documentId)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = DescriptorRowSql;
        AddParameter(command, "documentId", documentId);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"descriptor {documentId} must exist");
        return new DescriptorRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6)
        );
    }

    private static void AddParameter(DbCommand command, string name, long value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
