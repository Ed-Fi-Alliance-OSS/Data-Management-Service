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
/// The loader proof gate, run against a live 10,000-row load on either provider. It proves the
/// direct-SQL fixture is indistinguishable from production writes: one control student —
/// carrying the fixture's full child-collection and descriptor shape — and one control
/// descriptor are POSTed through the real HTTP pipeline after the load, and the loader rows
/// must match their row shapes across dms.Document, edfi.Student, the four child collection
/// tables, dms.Descriptor, dms.ReferentialIdentity, and the tracked-change side effects —
/// including producing exactly as many insert-time tracked-change rows per document as the
/// POST does (expected: none). Real GET-many and GET-by-id requests must then hydrate loader
/// rows with correct bodies, including non-empty child collections and resolved descriptor
/// URIs.
///
/// All assertion SQL uses double-quoted identifiers, which both providers accept.
/// </summary>
internal static class PerfFixtureSmokeScenario
{
    private const string TrackedChangesCountSql = """
        SELECT COUNT(*) FROM "tracked_changes_edfi"."Student";
        """;

    private const string TrackedDescriptorChangesCountSql = """
        SELECT COUNT(*) FROM "tracked_changes_edfi"."Descriptor";
        """;

    private const string DocumentRowSql = """
        SELECT "DocumentId", "ResourceKeyId", "CreatedByOwnershipTokenId", "ContentVersion", "IdentityVersion"
        FROM "dms"."Document"
        WHERE "DocumentUuid" = @uuid;
        """;

    private const string StudentRowSql = """
        SELECT "StudentUniqueId", "FirstName", "LastSurname", "BirthDate", "BirthCity", "ContentVersion", "BirthSexDescriptor_DescriptorId"
        FROM "edfi"."Student"
        WHERE "DocumentId" = @documentId;
        """;

    private const string ReferentialIdentityRowsSql = """
        SELECT "ReferentialId", "ResourceKeyId"
        FROM "dms"."ReferentialIdentity"
        WHERE "DocumentId" = @documentId;
        """;

    private const string DescriptorRowSql = """
        SELECT "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription", "Discriminator", "Uri", "ContentVersion"
        FROM "dms"."Descriptor"
        WHERE "DocumentId" = @documentId;
        """;

    /// <summary>
    /// Per child collection table: every column except the keys that necessarily differ
    /// between two documents (Student_DocumentId and the sequence-assigned
    /// CollectionItemId), so loader and control rows can be compared as value signatures.
    /// </summary>
    private static readonly IReadOnlyList<(string Table, string Sql)> _childRowSignatureSqls =
    [
        (
            "StudentIdentificationDocument",
            """
            SELECT "Ordinal", "IdentificationDocumentUseDescriptor_DescriptorId", "IssuerCountryDescriptor_DescriptorId", "PersonalInformationVerificationDescriptor_DescriptorId", "DocumentExpirationDate", "DocumentTitle", "IssuerDocumentIdentificationCode", "IssuerName"
            FROM "edfi"."StudentIdentificationDocument"
            WHERE "Student_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """
        ),
        (
            "StudentOtherName",
            """
            SELECT "Ordinal", "OtherNameTypeDescriptor_DescriptorId", "FirstName", "GenerationCodeSuffix", "LastSurname", "MiddleName", "PersonalTitlePrefix"
            FROM "edfi"."StudentOtherName"
            WHERE "Student_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """
        ),
        (
            "StudentPersonalIdentificationDocument",
            """
            SELECT "Ordinal", "IdentificationDocumentUseDescriptor_DescriptorId", "IssuerCountryDescriptor_DescriptorId", "PersonalInformationVerificationDescriptor_DescriptorId", "PersonalDocumentExpirationDate", "PersonalDocumentTitle", "PersonalIssuerDocumentIdentificationCode", "PersonalIssuerName"
            FROM "edfi"."StudentPersonalIdentificationDocument"
            WHERE "Student_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """
        ),
        (
            "StudentVisa",
            """
            SELECT "Ordinal", "VisaDescriptor_DescriptorId"
            FROM "edfi"."StudentVisa"
            WHERE "Student_DocumentId" = @documentId
            ORDER BY "Ordinal";
            """
        ),
    ];

    private sealed record DocumentRow(
        long DocumentId,
        long ResourceKeyId,
        bool HasOwnershipToken,
        long ContentVersion,
        long IdentityVersion
    );

    private sealed record StudentRow(
        string StudentUniqueId,
        string FirstName,
        string LastSurname,
        object BirthDate,
        bool BirthCityIsNull,
        long ContentVersion,
        long? BirthSexDescriptorId
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

    public static async Task RunAsync(ApiIntegrationHarness harness, PerfProvider provider)
    {
        PerfFixtureDefinition definition = new(PerfFixtureKind.Smoke10k);
        DbConnection connection = harness.DbConnection;

        long trackedBeforeLoad = await CountAsync(connection, TrackedChangesCountSql);
        trackedBeforeLoad.Should().Be(0, "the leased database must start clean");
        long trackedDescriptorsBeforeLoad = await CountAsync(connection, TrackedDescriptorChangesCountSql);
        trackedDescriptorsBeforeLoad.Should().Be(0, "the leased database must start clean");

        await PerfFixtureLoader.LoadAndVerifyAsync(connection, provider, definition);

        long trackedAfterLoad = await CountAsync(connection, TrackedChangesCountSql);
        long trackedDescriptorsAfterLoad = await CountAsync(connection, TrackedDescriptorChangesCountSql);

        Guid controlUuid = await PostControlStudentAsync(harness);

        long trackedAfterControl = await CountAsync(connection, TrackedChangesCountSql);
        long trackedRowsPerInsert = trackedAfterControl - trackedAfterLoad;
        trackedAfterLoad
            .Should()
            .Be(
                definition.RowCount * trackedRowsPerInsert,
                "loader inserts must produce the same tracked-change side effects per document as a production POST"
            );

        DocumentRow control = await ReadDocumentRowAsync(connection, controlUuid);
        control
            .DocumentId.Should()
            .Be(
                definition.ReseedTargetDocumentId + 1,
                "the reseed must hand the id after the descriptor block to the write path"
            );

        DocumentRow loaderFirst = await ReadDocumentRowAsync(
            connection,
            PerfFixtureDefinition.DocumentUuidFor(1)
        );
        loaderFirst.DocumentId.Should().Be(PerfFixtureDefinition.MinDocumentId);
        loaderFirst.ResourceKeyId.Should().Be(control.ResourceKeyId);
        loaderFirst.HasOwnershipToken.Should().Be(control.HasOwnershipToken);
        loaderFirst.ContentVersion.Should().BePositive();
        loaderFirst.IdentityVersion.Should().BePositive();
        control.ContentVersion.Should().BePositive();

        StudentRow loaderStudent = await ReadStudentRowAsync(connection, loaderFirst.DocumentId);
        StudentRow controlStudent = await ReadStudentRowAsync(connection, control.DocumentId);
        loaderStudent.StudentUniqueId.Should().Be(PerfFixtureDefinition.StudentUniqueIdFor(1));
        loaderStudent.FirstName.Should().Be(controlStudent.FirstName);
        loaderStudent.LastSurname.Should().Be(controlStudent.LastSurname);
        loaderStudent.BirthDate.Should().Be(controlStudent.BirthDate);
        loaderStudent.BirthCityIsNull.Should().BeTrue();
        controlStudent.BirthCityIsNull.Should().BeTrue();
        loaderStudent
            .BirthSexDescriptorId.Should()
            .Be(
                controlStudent.BirthSexDescriptorId,
                "loader and control students must bind the same birth-sex descriptor"
            );
        loaderStudent
            .BirthSexDescriptorId.Should()
            .Be(definition.DescriptorDocumentIdFor(PerfFixtureDefinition.SexDescriptorResource));
        loaderStudent
            .ContentVersion.Should()
            .Be(loaderFirst.ContentVersion, "the stamp trigger must have mirrored the document version");
        controlStudent.ContentVersion.Should().Be(control.ContentVersion);

        // Loader child collection rows must be value-identical to what the control POST's
        // child rows look like, table by table.
        foreach ((string table, string sql) in _childRowSignatureSqls)
        {
            List<string> loaderSignatures = await ReadRowSignaturesAsync(
                connection,
                sql,
                loaderFirst.DocumentId
            );
            List<string> controlSignatures = await ReadRowSignaturesAsync(
                connection,
                sql,
                control.DocumentId
            );
            loaderSignatures
                .Should()
                .NotBeEmpty($"{table} must hold loader rows — a zero-work child collection is the defect");
            loaderSignatures
                .Should()
                .Equal(controlSignatures, $"loader {table} rows must match the production POST's row shape");
        }

        // Validate the independent uuidv5 formula against the control row the production
        // write path produced, then hold loader rows to it.
        await AssertReferentialIdentityAsync(
            connection,
            control.DocumentId,
            control.ResourceKeyId,
            "control-000000001"
        );
        await AssertReferentialIdentityAsync(
            connection,
            loaderFirst.DocumentId,
            control.ResourceKeyId,
            PerfFixtureDefinition.StudentUniqueIdFor(1)
        );

        DocumentRow loaderLast = await ReadDocumentRowAsync(
            connection,
            PerfFixtureDefinition.DocumentUuidFor(definition.RowCount)
        );
        loaderLast.DocumentId.Should().Be(definition.MaxDocumentId);
        await AssertReferentialIdentityAsync(
            connection,
            loaderLast.DocumentId,
            control.ResourceKeyId,
            PerfFixtureDefinition.StudentUniqueIdFor(definition.RowCount)
        );

        await AssertControlDescriptorParityAsync(
            harness,
            connection,
            definition,
            trackedDescriptorsAfterLoad
        );

        await AssertFirstPageAsync(harness);
        await AssertDeepPageAsync(harness, definition);
        await AssertGetByIdAsync(harness);
    }

    private static async Task<Guid> PostControlStudentAsync(ApiIntegrationHarness harness)
    {
        JsonObject payload = new()
        {
            ["studentUniqueId"] = "control-000000001",
            ["firstName"] = PerfFixtureDefinition.FirstName,
            ["lastSurname"] = PerfFixtureDefinition.LastSurname,
            ["birthDate"] = PerfFixtureDefinition.BirthDateIso,
            ["birthSexDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                PerfFixtureDefinition.SexDescriptorResource
            ),
            ["otherNames"] = new JsonArray(
                new JsonObject
                {
                    ["otherNameTypeDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.OtherNameTypeDescriptorResource
                    ),
                    ["firstName"] = PerfFixtureDefinition.FirstName,
                    ["lastSurname"] = PerfFixtureDefinition.LastSurname,
                }
            ),
            ["identificationDocuments"] = new JsonArray(
                new JsonObject
                {
                    ["identificationDocumentUseDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
                    ),
                    ["personalInformationVerificationDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
                    ),
                }
            ),
            ["personalIdentificationDocuments"] = new JsonArray(
                new JsonObject
                {
                    ["identificationDocumentUseDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
                    ),
                    ["personalInformationVerificationDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
                    ),
                }
            ),
            ["visas"] = new JsonArray(
                new JsonObject
                {
                    ["visaDescriptor"] = PerfFixtureDefinition.DescriptorUriFor(
                        PerfFixtureDefinition.VisaDescriptorResource
                    ),
                }
            ),
        };
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(
            PerfFixtureDefinition.ResourceEndpoint,
            content
        );
        string body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        string location = created.Headers.Location!.ToString();
        return Guid.Parse(location.Split('/')[^1]);
    }

    /// <summary>
    /// POSTs one control VisaDescriptor through the production descriptor write path and
    /// holds the loader's VisaDescriptor rows to its shape: dms.Document identity fields,
    /// the dms.Descriptor row semantics, referential-identity row count, and tracked-change
    /// accounting.
    /// </summary>
    private static async Task AssertControlDescriptorParityAsync(
        ApiIntegrationHarness harness,
        DbConnection connection,
        PerfFixtureDefinition definition,
        long trackedDescriptorsAfterLoad
    )
    {
        JsonObject payload = new()
        {
            ["codeValue"] = "Control",
            ["shortDescription"] = "Control",
            ["namespace"] = PerfFixtureDefinition.DescriptorNamespaceFor(
                PerfFixtureDefinition.VisaDescriptorResource
            ),
        };
        using StringContent content = new(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await harness.HttpClient.PostAsync(
            "/data/ed-fi/visaDescriptors",
            content
        );
        string body = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, body);
        Guid controlDescriptorUuid = Guid.Parse(created.Headers.Location!.ToString().Split('/')[^1]);

        long trackedDescriptorsAfterControl = await CountAsync(connection, TrackedDescriptorChangesCountSql);
        long trackedDescriptorRowsPerInsert = trackedDescriptorsAfterControl - trackedDescriptorsAfterLoad;
        trackedDescriptorsAfterLoad
            .Should()
            .Be(
                PerfFixtureDefinition.DescriptorCount * trackedDescriptorRowsPerInsert,
                "loader descriptor inserts must produce the same tracked-change side effects as a production POST"
            );

        DocumentRow controlDocument = await ReadDocumentRowAsync(connection, controlDescriptorUuid);
        DescriptorRow controlDescriptor = await ReadDescriptorRowAsync(
            connection,
            controlDocument.DocumentId
        );

        long loaderDescriptorDocumentId = definition.DescriptorDocumentIdFor(
            PerfFixtureDefinition.VisaDescriptorResource
        );
        DocumentRow loaderDocument = await ReadDocumentRowAsync(
            connection,
            definition.DescriptorDocumentUuidFor(PerfFixtureDefinition.VisaDescriptorResource)
        );
        loaderDocument.DocumentId.Should().Be(loaderDescriptorDocumentId);
        loaderDocument.ResourceKeyId.Should().Be(controlDocument.ResourceKeyId);
        loaderDocument.HasOwnershipToken.Should().Be(controlDocument.HasOwnershipToken);

        DescriptorRow loaderDescriptor = await ReadDescriptorRowAsync(connection, loaderDescriptorDocumentId);
        loaderDescriptor.ResourceKeyId.Should().Be(controlDescriptor.ResourceKeyId);
        loaderDescriptor.Namespace.Should().Be(controlDescriptor.Namespace);
        loaderDescriptor.Discriminator.Should().Be(controlDescriptor.Discriminator);
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
        controlDescriptor.ContentVersion.Should().Be(controlDocument.ContentVersion);

        // Validate the independent descriptor referential-id formula against the control
        // row the production write path produced, then hold the loader row to it.
        await AssertDescriptorReferentialIdentityAsync(
            connection,
            controlDocument.DocumentId,
            controlDescriptor.ResourceKeyId,
            controlDescriptor.Uri
        );
        await AssertDescriptorReferentialIdentityAsync(
            connection,
            loaderDescriptorDocumentId,
            loaderDescriptor.ResourceKeyId,
            loaderDescriptor.Uri
        );
    }

    private static async Task AssertDescriptorReferentialIdentityAsync(
        DbConnection connection,
        long documentId,
        long expectedResourceKeyId,
        string uri
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = ReferentialIdentityRowsSql;
        AddParameter(command, "documentId", documentId);

        List<(Guid ReferentialId, long ResourceKeyId)> rows = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(
                    (reader.GetGuid(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture))
                );
            }
        }

        (Guid referentialId, long resourceKeyId) = rows.Should()
            .ContainSingle($"descriptor document {documentId} must carry exactly one referential identity")
            .Subject;
        resourceKeyId.Should().Be(expectedResourceKeyId);
        referentialId
            .Should()
            .Be(
                ReferentialIdentityDerivation.DescriptorReferentialId(
                    PerfFixtureDefinition.VisaDescriptorResource,
                    uri
                ),
                $"the referential id for '{uri}' must match the independent uuidv5 derivation"
            );
    }

    private static async Task AssertFirstPageAsync(ApiIntegrationHarness harness)
    {
        JsonArray items = await GetPageAsync(harness, "?limit=25&offset=0");
        items.Should().HaveCount(25);
        Guid firstId = Guid.Parse(items[0]!["id"]!.GetValue<string>());
        firstId.Should().Be(PerfFixtureDefinition.DocumentUuidFor(1));
        items[0]!["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.StudentUniqueIdFor(1));
        items[0]!["firstName"]!.GetValue<string>().Should().Be(PerfFixtureDefinition.FirstName);
        AssertHydratedChildShape(items[0]!);
    }

    private static async Task AssertDeepPageAsync(
        ApiIntegrationHarness harness,
        PerfFixtureDefinition definition
    )
    {
        JsonArray items = await GetPageAsync(harness, $"?limit=25&offset={definition.RowCount - 25}");
        items.Should().HaveCount(25);
        Guid lastId = Guid.Parse(items[^1]!["id"]!.GetValue<string>());
        lastId.Should().Be(PerfFixtureDefinition.DocumentUuidFor(definition.RowCount));
        AssertHydratedChildShape(items[^1]!);
    }

    private static async Task AssertGetByIdAsync(ApiIntegrationHarness harness)
    {
        Guid documentUuid = PerfFixtureDefinition.DocumentUuidFor(9_999);
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            $"{PerfFixtureDefinition.ResourceEndpoint}/{documentUuid}"
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode document = JsonNode.Parse(body)!;
        document["studentUniqueId"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.StudentUniqueIdFor(9_999));
        AssertHydratedChildShape(document);
    }

    /// <summary>
    /// A hydrated student body must carry every child collection non-empty with its
    /// descriptor URIs resolved — the response can only contain these URIs when the
    /// hydration batch's collection and descriptor-resolution statements did real work.
    /// </summary>
    private static void AssertHydratedChildShape(JsonNode student)
    {
        student["birthSexDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.DescriptorUriFor(PerfFixtureDefinition.SexDescriptorResource));

        JsonArray otherNames = student["otherNames"]!.AsArray();
        otherNames.Should().HaveCount(PerfFixtureDefinition.ChildCollectionRowsPerStudent);
        otherNames[0]!["otherNameTypeDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(
                PerfFixtureDefinition.DescriptorUriFor(PerfFixtureDefinition.OtherNameTypeDescriptorResource)
            );
        otherNames[0]!["firstName"]!.GetValue<string>().Should().Be(PerfFixtureDefinition.FirstName);
        otherNames[0]!["lastSurname"]!.GetValue<string>().Should().Be(PerfFixtureDefinition.LastSurname);

        JsonArray identificationDocuments = student["identificationDocuments"]!.AsArray();
        identificationDocuments.Should().HaveCount(PerfFixtureDefinition.ChildCollectionRowsPerStudent);
        identificationDocuments[0]!["identificationDocumentUseDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(
                PerfFixtureDefinition.DescriptorUriFor(
                    PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
                )
            );

        JsonArray personalIdentificationDocuments = student["personalIdentificationDocuments"]!.AsArray();
        personalIdentificationDocuments
            .Should()
            .HaveCount(PerfFixtureDefinition.ChildCollectionRowsPerStudent);
        personalIdentificationDocuments[0]!["personalInformationVerificationDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(
                PerfFixtureDefinition.DescriptorUriFor(
                    PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
                )
            );

        JsonArray visas = student["visas"]!.AsArray();
        visas.Should().HaveCount(PerfFixtureDefinition.ChildCollectionRowsPerStudent);
        visas[0]!["visaDescriptor"]!
            .GetValue<string>()
            .Should()
            .Be(PerfFixtureDefinition.DescriptorUriFor(PerfFixtureDefinition.VisaDescriptorResource));
    }

    private static async Task<JsonArray> GetPageAsync(ApiIntegrationHarness harness, string queryString)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            PerfFixtureDefinition.ResourceEndpoint + queryString
        );
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsArray();
    }

    private static async Task AssertReferentialIdentityAsync(
        DbConnection connection,
        long documentId,
        long expectedResourceKeyId,
        string studentUniqueId
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = ReferentialIdentityRowsSql;
        AddParameter(command, "documentId", documentId);

        List<(Guid ReferentialId, long ResourceKeyId)> rows = [];
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(
                    (reader.GetGuid(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture))
                );
            }
        }

        (Guid referentialId, long resourceKeyId) = rows.Should()
            .ContainSingle($"document {documentId} must carry exactly one referential identity")
            .Subject;
        resourceKeyId.Should().Be(expectedResourceKeyId);
        referentialId
            .Should()
            .Be(
                ReferentialIdentityDerivation.StudentReferentialId(studentUniqueId),
                $"the referential id for '{studentUniqueId}' must match the independent uuidv5 derivation"
            );
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql, long? documentId = null)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (documentId is not null)
        {
            AddParameter(command, "documentId", documentId.Value);
        }

        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
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
            reader.GetInt64(3),
            reader.GetInt64(4)
        );
    }

    private static async Task<StudentRow> ReadStudentRowAsync(DbConnection connection, long documentId)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = StudentRowSql;
        AddParameter(command, "documentId", documentId);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"student {documentId} must exist");
        return new StudentRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetValue(3),
            await reader.IsDBNullAsync(4),
            reader.GetInt64(5),
            await reader.IsDBNullAsync(6) ? null : reader.GetInt64(6)
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

    /// <summary>
    /// Reads every row of a child-collection query into one value signature per row, with
    /// nulls made explicit, so loader and control rows compare as whole shapes.
    /// </summary>
    private static async Task<List<string>> ReadRowSignaturesAsync(
        DbConnection connection,
        string sql,
        long documentId
    )
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "documentId", documentId);

        List<string> signatures = [];
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            StringBuilder signature = new();
            for (int field = 0; field < reader.FieldCount; field++)
            {
                if (field > 0)
                {
                    signature.Append('|');
                }

                object value = reader.GetValue(field);
                signature.Append(
                    value is DBNull ? "<null>" : Convert.ToString(value, CultureInfo.InvariantCulture)
                );
            }

            signatures.Add(signature.ToString());
        }

        return signatures;
    }

    private static void AddParameter(DbCommand command, string name, long value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
