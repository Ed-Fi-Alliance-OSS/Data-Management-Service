// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_DescriptorRead_Test_Support
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";
    private static readonly QualifiedResourceName SchoolTypeDescriptorResource = new(
        "Ed-Fi",
        "SchoolTypeDescriptor"
    );
    private static readonly QualifiedResourceName GradeLevelDescriptorResource = new(
        "Ed-Fi",
        "GradeLevelDescriptor"
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public void It_builds_get_requests_for_external_and_stored_document_modes()
    {
        var resourceInfo = DescriptorReadIntegrationTestSupport.CreateResourceInfo(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "SchoolTypeDescriptor"
        );
        var documentUuid = new DocumentUuid(Guid.Parse("10000000-0000-0000-0000-000000000001"));

        var externalRequest = DescriptorReadIntegrationTestSupport.CreateGetRequest(
            documentUuid,
            resourceInfo,
            _mappingSet,
            new TraceId("pg-descriptor-external")
        );
        var storedRequest = DescriptorReadIntegrationTestSupport.CreateGetRequest(
            documentUuid,
            resourceInfo,
            _mappingSet,
            new TraceId("pg-descriptor-stored"),
            RelationalGetRequestReadMode.StoredDocument
        );

        externalRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.ExternalResponse);
        storedRequest.ReadMode.Should().Be(RelationalGetRequestReadMode.StoredDocument);
        externalRequest.AuthorizationStrategyEvaluators.Should().BeEmpty();
        storedRequest.AuthorizationStrategyEvaluators.Should().BeEmpty();
    }

    [Test]
    public async Task It_resolves_descriptor_resource_key_ids_from_the_mapping_set()
    {
        var expectedSchoolTypeDescriptorResourceKeyId = await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", "Ed-Fi"),
            new NpgsqlParameter("resourceName", "SchoolTypeDescriptor")
        );

        var actualSchoolTypeDescriptorResourceKeyId =
            DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
                _mappingSet,
                SchoolTypeDescriptorResource
            );

        actualSchoolTypeDescriptorResourceKeyId.Should().Be(expectedSchoolTypeDescriptorResourceKeyId);
    }

    [Test]
    public async Task It_seeds_valid_descriptor_rows_with_mixed_case_values()
    {
        var seed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("10000000-0000-0000-0000-000000000101")),
            Namespace: "uri://Ed-Fi.org/SchoolTypeDescriptor/Mixed",
            CodeValue: "MiXeDValue",
            ShortDescription: "MiXeD Short Description",
            Description: "MiXeD Description",
            EffectiveBeginDate: new DateOnly(2025, 1, 2),
            EffectiveEndDate: new DateOnly(2025, 12, 31)
        );

        var documentId = await PostgresqlDescriptorReadTestSupport.SeedDescriptorAsync(
            _database,
            _mappingSet,
            SchoolTypeDescriptorResource,
            seed
        );
        var documentRow = await PostgresqlDescriptorReadTestSupport.ReadDocumentRowAsync(
            _database,
            documentId
        );
        var descriptorRow = await PostgresqlDescriptorReadTestSupport.ReadDescriptorRowAsync(
            _database,
            documentId
        );
        var expectedResourceKeyId = DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
            _mappingSet,
            SchoolTypeDescriptorResource
        );

        GetRequiredGuid(documentRow, "DocumentUuid").Should().Be(seed.DocumentUuid.Value);
        GetRequiredInt16(documentRow, "ResourceKeyId").Should().Be(expectedResourceKeyId);
        GetRequiredInt64(documentRow, "ContentVersion").Should().BeGreaterThan(0);
        GetRequiredInt64(documentRow, "IdentityVersion").Should().BeGreaterThan(0);
        documentRow["ContentLastModifiedAt"].Should().NotBeNull();
        documentRow["IdentityLastModifiedAt"].Should().NotBeNull();
        documentRow["CreatedAt"].Should().NotBeNull();

        GetRequiredString(descriptorRow, "Namespace").Should().Be(seed.Namespace);
        GetRequiredString(descriptorRow, "CodeValue").Should().Be(seed.CodeValue);
        GetRequiredString(descriptorRow, "ShortDescription").Should().Be(seed.ShortDescription);
        GetRequiredString(descriptorRow, "Description").Should().Be(seed.Description);
        GetRequiredDateOnly(descriptorRow, "EffectiveBeginDate").Should().Be(seed.EffectiveBeginDate);
        GetRequiredDateOnly(descriptorRow, "EffectiveEndDate").Should().Be(seed.EffectiveEndDate);
        GetRequiredString(descriptorRow, "Discriminator").Should().Be("Ed-Fi:SchoolTypeDescriptor");
        GetRequiredString(descriptorRow, "Uri").Should().Be(seed.Uri);
    }

    [Test]
    public async Task It_seeds_wrong_resource_and_missing_descriptor_rows_deterministically()
    {
        var schoolTypeDescriptorResourceKeyId =
            DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
                _mappingSet,
                SchoolTypeDescriptorResource
            );
        var gradeLevelDescriptorResourceKeyId =
            DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
                _mappingSet,
                GradeLevelDescriptorResource
            );
        var missingDescriptorDocumentUuid = new DocumentUuid(
            Guid.Parse("10000000-0000-0000-0000-000000000201")
        );
        var wrongResourceSeed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("10000000-0000-0000-0000-000000000202")),
            Namespace: "uri://ed-fi.org/GradeLevelDescriptor",
            CodeValue: "Twelve",
            ShortDescription: "Twelfth grade"
        );

        var missingDescriptorDocumentId = await PostgresqlDescriptorReadTestSupport.InsertDocumentAsync(
            _database,
            missingDescriptorDocumentUuid,
            schoolTypeDescriptorResourceKeyId
        );
        var wrongResourceDocumentId = await PostgresqlDescriptorReadTestSupport.SeedDescriptorAsync(
            _database,
            _mappingSet,
            GradeLevelDescriptorResource,
            wrongResourceSeed
        );
        var missingDescriptorDocumentRow = await PostgresqlDescriptorReadTestSupport.ReadDocumentRowAsync(
            _database,
            missingDescriptorDocumentId
        );
        var wrongResourceDocumentRow = await PostgresqlDescriptorReadTestSupport.ReadDocumentRowAsync(
            _database,
            wrongResourceDocumentId
        );
        var missingDescriptorRowExists = await PostgresqlDescriptorReadTestSupport.DescriptorRowExistsAsync(
            _database,
            missingDescriptorDocumentId
        );
        var wrongResourceDescriptorRowExists =
            await PostgresqlDescriptorReadTestSupport.DescriptorRowExistsAsync(
                _database,
                wrongResourceDocumentId
            );

        GetRequiredInt16(missingDescriptorDocumentRow, "ResourceKeyId")
            .Should()
            .Be(schoolTypeDescriptorResourceKeyId);
        missingDescriptorRowExists.Should().BeFalse();

        GetRequiredInt16(wrongResourceDocumentRow, "ResourceKeyId")
            .Should()
            .Be(gradeLevelDescriptorResourceKeyId);
        gradeLevelDescriptorResourceKeyId.Should().NotBe(schoolTypeDescriptorResourceKeyId);
        wrongResourceDescriptorRowExists.Should().BeTrue();
    }

    private static DateOnly GetRequiredDateOnly(
        IReadOnlyDictionary<string, object?> row,
        string columnName
    ) =>
        row[columnName] switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            null => throw new InvalidOperationException($"Column '{columnName}' is null."),
            var value => throw new InvalidOperationException(
                $"Column '{columnName}' has unsupported date type '{value.GetType().FullName}'."
            ),
        };

    private static Guid GetRequiredGuid(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return row[columnName] switch
        {
            Guid guid => guid,
            null => throw new InvalidOperationException($"Column '{columnName}' is null."),
            var value => throw new InvalidOperationException(
                $"Column '{columnName}' has unsupported guid type '{value.GetType().FullName}'."
            ),
        };
    }

    private static short GetRequiredInt16(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        var value = row[columnName] ?? throw new InvalidOperationException($"Column '{columnName}' is null.");
        return Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    private static long GetRequiredInt64(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        var value = row[columnName] ?? throw new InvalidOperationException($"Column '{columnName}' is null.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string GetRequiredString(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        return row[columnName] switch
        {
            string value => value,
            null => throw new InvalidOperationException($"Column '{columnName}' is null."),
            var value => throw new InvalidOperationException(
                $"Column '{columnName}' has unsupported string type '{value.GetType().FullName}'."
            ),
        };
    }
}
