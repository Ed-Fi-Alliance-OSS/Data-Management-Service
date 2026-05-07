// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_DescriptorRead_Get_Request
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
    private ServiceProvider _serviceProvider = null!;
    private ResourceInfo _resourceInfo = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = CreateServiceProvider();
        _resourceInfo = DescriptorReadIntegrationTestSupport.CreateResourceInfo(
            _fixture.EffectiveSchemaSet,
            "ed-fi",
            "SchoolTypeDescriptor"
        );
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
            _database = null!;
        }
    }

    [Test]
    public async Task It_reads_descriptor_get_by_id_as_an_external_response_with_metadata_and_without_internal_fields()
    {
        var seed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000101")),
            Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
            CodeValue: "Alternative",
            ShortDescription: "Alternative",
            Description: "Alternative school type",
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
        var expectedLastModifiedAt = GetRequiredDateTimeOffset(documentRow, "ContentLastModifiedAt");

        var result = await ExecuteGetByIdAsync(seed.DocumentUuid, "pg-descriptor-get-external");

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(seed.DocumentUuid);
        success.LastModifiedDate.Should().Be(expectedLastModifiedAt.UtcDateTime);
        RelationalGetIntegrationTestHelper
            .CanonicalizeJson(success.EdfiDoc)
            .Should()
            .Be(CanonicalizeJson(CreateExpectedExternalResponse(seed, expectedLastModifiedAt)));
        success.EdfiDoc["Uri"].Should().BeNull();
        success.EdfiDoc["Discriminator"].Should().BeNull();
        success.EdfiDoc["ChangeVersion"].Should().BeNull();
    }

    [Test]
    public async Task It_reads_descriptor_get_by_id_in_stored_document_mode_without_api_metadata()
    {
        var seed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000102")),
            Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
            CodeValue: "Charter",
            ShortDescription: "Charter",
            Description: "Charter school type",
            EffectiveBeginDate: new DateOnly(2026, 2, 3),
            EffectiveEndDate: new DateOnly(2026, 8, 4)
        );

        await PostgresqlDescriptorReadTestSupport.SeedDescriptorAsync(
            _database,
            _mappingSet,
            SchoolTypeDescriptorResource,
            seed
        );

        var result = await ExecuteGetByIdAsync(
            seed.DocumentUuid,
            "pg-descriptor-get-stored",
            RelationalGetRequestReadMode.StoredDocument
        );

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        CanonicalizeJson(success.EdfiDoc).Should().Be(CanonicalizeJson(CreateExpectedStoredDocument(seed)));
        success.EdfiDoc["id"].Should().BeNull();
        success.EdfiDoc["_etag"].Should().BeNull();
        success.EdfiDoc["_lastModifiedDate"].Should().BeNull();
        success.EdfiDoc["Uri"].Should().BeNull();
        success.EdfiDoc["Discriminator"].Should().BeNull();
        success.EdfiDoc["ChangeVersion"].Should().BeNull();
    }

    [Test]
    public async Task It_returns_not_exists_for_a_document_uuid_that_belongs_to_a_different_descriptor_resource()
    {
        var seed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000103")),
            Namespace: "uri://ed-fi.org/GradeLevelDescriptor",
            CodeValue: "Eleventh grade",
            ShortDescription: "Eleventh grade"
        );

        await PostgresqlDescriptorReadTestSupport.SeedDescriptorAsync(
            _database,
            _mappingSet,
            GradeLevelDescriptorResource,
            seed
        );

        var result = await ExecuteGetByIdAsync(seed.DocumentUuid, "pg-descriptor-get-wrong-resource");

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
    }

    [Test]
    public async Task It_returns_not_implemented_when_descriptor_get_authorization_requires_filtering()
    {
        AuthorizationStrategyEvaluator[] authorizationStrategyEvaluators =
        [
            new("RelationshipsWithEdOrgsOnly", [], FilterOperator.And),
        ];

        var result = await ExecuteGetByIdAsync(
            new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000104")),
            "pg-descriptor-get-auth",
            authorizationStrategyEvaluators: authorizationStrategyEvaluators
        );

        result
            .Should()
            .BeEquivalentTo(
                new GetResult.GetFailureNotImplemented(
                    "Relational descriptor GET authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or only 'NoFurtherAuthorizationRequired' are currently supported."
                )
            );
    }

    [Test]
    public async Task It_treats_discriminator_mismatch_as_diagnostic_only()
    {
        var seed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000105")),
            Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
            CodeValue: "Magnet",
            ShortDescription: "Magnet",
            Discriminator: "Ed-Fi:GradeLevelDescriptor"
        );

        await PostgresqlDescriptorReadTestSupport.SeedDescriptorAsync(
            _database,
            _mappingSet,
            SchoolTypeDescriptorResource,
            seed
        );

        var result = await ExecuteGetByIdAsync(seed.DocumentUuid, "pg-descriptor-get-discriminator");

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be(seed.CodeValue);
        success.EdfiDoc["shortDescription"]!.GetValue<string>().Should().Be(seed.ShortDescription);
    }

    [Test]
    public async Task It_returns_an_unknown_failure_when_the_selected_descriptor_document_has_no_descriptor_row()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000106"));
        var resourceKeyId = DescriptorReadIntegrationTestSupport.GetDescriptorResourceKeyIdOrThrow(
            _mappingSet,
            SchoolTypeDescriptorResource
        );
        var documentId = await PostgresqlDescriptorReadTestSupport.InsertDocumentAsync(
            _database,
            documentUuid,
            resourceKeyId
        );

        var result = await ExecuteGetByIdAsync(documentUuid, "pg-descriptor-get-missing-row");

        var failure = result.Should().BeOfType<GetResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain($"DocumentId {documentId}");
        failure.FailureMessage.Should().Contain("dms.Descriptor.Namespace must not be null.");
    }

    [Test]
    public async Task It_omits_null_optional_fields_from_external_descriptor_responses()
    {
        var seed = new DescriptorReadSeed(
            DocumentUuid: new DocumentUuid(Guid.Parse("30000000-0000-0000-0000-000000000107")),
            Namespace: "uri://ed-fi.org/SchoolTypeDescriptor",
            CodeValue: "Private",
            ShortDescription: "Private"
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
        var expectedLastModifiedAt = GetRequiredDateTimeOffset(documentRow, "ContentLastModifiedAt");

        var result = await ExecuteGetByIdAsync(seed.DocumentUuid, "pg-descriptor-get-null-optional");

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        CanonicalizeJson(success.EdfiDoc)
            .Should()
            .Be(CanonicalizeJson(CreateExpectedExternalResponse(seed, expectedLastModifiedAt)));
        success.EdfiDoc["description"].Should().BeNull();
        success.EdfiDoc["effectiveBeginDate"].Should().BeNull();
        success.EdfiDoc["effectiveEndDate"].Should().BeNull();
        success
            .EdfiDoc.AsObject()
            .Select(static property => property.Key)
            .Should()
            .Equal("namespace", "codeValue", "shortDescription", "id", "_etag", "_lastModifiedDate");
    }

    private ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, PostgresqlRelationalQueryHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private async Task<GetResult> ExecuteGetByIdAsync(
        DocumentUuid documentUuid,
        string traceId,
        RelationalGetRequestReadMode readMode = RelationalGetRequestReadMode.ExternalResponse,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null
    )
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        SetSelectedInstance(scope.ServiceProvider);

        var request = DescriptorReadIntegrationTestSupport.CreateGetRequest(
            documentUuid,
            _resourceInfo,
            _mappingSet,
            new TraceId(traceId),
            readMode,
            authorizationStrategyEvaluators: authorizationStrategyEvaluators
        );

        return await scope
            .ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>()
            .GetDocumentById(request);
    }

    private void SetSelectedInstance(IServiceProvider serviceProvider)
    {
        serviceProvider
            .GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlDescriptorReadGet",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );
    }

    private static JsonObject CreateExpectedStoredDocument(DescriptorReadSeed seed)
    {
        JsonObject expectedDocument = new()
        {
            ["namespace"] = seed.Namespace,
            ["codeValue"] = seed.CodeValue,
            ["shortDescription"] = seed.ShortDescription,
        };

        if (seed.Description is not null)
        {
            expectedDocument["description"] = seed.Description;
        }

        if (seed.EffectiveBeginDate is not null)
        {
            expectedDocument["effectiveBeginDate"] = seed.EffectiveBeginDate.Value.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        if (seed.EffectiveEndDate is not null)
        {
            expectedDocument["effectiveEndDate"] = seed.EffectiveEndDate.Value.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        return expectedDocument;
    }

    private static JsonObject CreateExpectedExternalResponse(
        DescriptorReadSeed seed,
        DateTimeOffset expectedLastModifiedAt
    )
    {
        var expectedDocument = CreateExpectedStoredDocument(seed);
        expectedDocument["id"] = seed.DocumentUuid.Value.ToString();
        expectedDocument["_lastModifiedDate"] =
            RelationalGetIntegrationTestHelper.FormatExternalLastModifiedDate(expectedLastModifiedAt);
        expectedDocument["_etag"] = DocumentComparer.GenerateContentHash(expectedDocument);

        return expectedDocument;
    }

    private static string CanonicalizeJson(JsonNode document) =>
        RelationalGetIntegrationTestHelper.CanonicalizeJson(document);

    private static DateTimeOffset GetRequiredDateTimeOffset(
        IReadOnlyDictionary<string, object?> row,
        string columnName
    )
    {
        return row[columnName] switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            null => throw new InvalidOperationException($"Column '{columnName}' is null."),
            var value => throw new InvalidOperationException(
                $"Column '{columnName}' has unsupported datetime type '{value.GetType().FullName}'."
            ),
        };
    }
}
