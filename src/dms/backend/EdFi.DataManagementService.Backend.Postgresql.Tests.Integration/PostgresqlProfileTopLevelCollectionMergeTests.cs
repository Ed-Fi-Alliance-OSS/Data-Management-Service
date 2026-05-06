// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class PostgresqlProfileTopLevelCollectionMergeNoOpHostApplicationLifetime
    : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

file sealed class PostgresqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class PostgresqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        System.Text.Json.JsonElement originalEdFiDoc,
        ProjectName originalDocumentProjectName,
        ResourceName originalDocumentResourceName,
        JsonNode modifiedEdFiDoc,
        JsonNode referencingEdFiDoc,
        long referencingDocumentId,
        short referencingDocumentPartitionKey,
        Guid referencingDocumentUuid,
        ProjectName referencingProjectName,
        ResourceName referencingResourceName
    ) =>
        new(
            OriginalEdFiDoc: referencingEdFiDoc,
            ModifiedEdFiDoc: referencingEdFiDoc,
            Id: referencingDocumentId,
            DocumentPartitionKey: referencingDocumentPartitionKey,
            DocumentUuid: referencingDocumentUuid,
            ProjectName: referencingProjectName,
            ResourceName: referencingResourceName,
            isIdentityUpdate: false
        );
}

internal sealed class PostgresqlProfileTopLevelCollectionMergeProjectionInvoker(
    ImmutableArray<VisibleStoredCollectionRow> visibleStoredRows
) : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: storedDocument,
            StoredScopeStates:
            [
                new StoredScopeState(
                    Address: new ScopeInstanceAddress("$", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            VisibleStoredCollectionRows: visibleStoredRows
        );
    }
}

internal sealed record PostgresqlProfileTopLevelCollectionAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record PostgresqlProfileTopLevelCollectionRequestItem(string City, bool Creatable);

internal sealed record PostgresqlProfileTopLevelCollectionStoredRow(
    string City,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record PostgresqlProfileTopLevelCollectionAddressInput(
    string City,
    string? AddressTypeDescriptor = null
);

internal static class PostgresqlProfileTopLevelCollectionMergeSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

    public const string AddressScope = "$.addresses[*]";
    public const string PhysicalAddressTypeDescriptorUri = "uri://ed-fi.org/AddressTypeDescriptor#Physical";
    public const string MailingAddressTypeDescriptorUri = "uri://ed-fi.org/AddressTypeDescriptor#Mailing";

    public static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    public static readonly ResourceInfo SchoolResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("School"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<
            IHostApplicationLifetime,
            PostgresqlProfileTopLevelCollectionMergeNoOpHostApplicationLifetime
        >();
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

    public static JsonNode CreateSchoolBody(long schoolId, params string[] cities)
    {
        var addresses = cities.Select(city => new PostgresqlProfileTopLevelCollectionAddressInput(city));
        return CreateSchoolBody(schoolId, addresses.ToArray());
    }

    public static JsonNode CreateSchoolBody(
        long schoolId,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionAddressInput> addresses
    )
    {
        JsonArray addressNodes = [];
        foreach (var address in addresses)
        {
            JsonObject addressNode = new() { ["city"] = address.City };
            if (address.AddressTypeDescriptor is not null)
            {
                addressNode["addressTypeDescriptor"] = address.AddressTypeDescriptor;
            }

            addressNodes.Add(addressNode);
        }

        return new JsonObject { ["schoolId"] = checked((int)schoolId), ["addresses"] = addressNodes };
    }

    public static DocumentInfo CreateDocumentInfo(long schoolId, JsonNode? body = null)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.schoolId"),
                schoolId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);
        var descriptorReferences = body is null ? [] : CreateAddressTypeDescriptorReferences(body).ToArray();

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: descriptorReferences,
            SuperclassIdentity: null
        );
    }

    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionRequestItem> requestItems,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionStoredRow> visibleStoredRows,
        bool rootCreatable = true,
        string profileName = "top-level-collection-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var addressIdentityPath = scopeCatalog
            .Single(descriptor => descriptor.JsonScope == AddressScope)
            .SemanticIdentityRelativePathsInOrder.Single();

        var visibleRequestItems = requestItems
            .Select(
                (item, index) =>
                    new VisibleRequestCollectionItem(
                        CreateCollectionRowAddress(addressIdentityPath, item.City),
                        item.Creatable,
                        $"$.addresses[{index}]"
                    )
            )
            .ToImmutableArray();

        var storedRows = visibleStoredRows
            .Select(row => new VisibleStoredCollectionRow(
                CreateCollectionRowAddress(addressIdentityPath, row.City),
                row.HiddenMemberPaths
            ))
            .ToImmutableArray();

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: rootCreatable
                    ),
                ],
                VisibleRequestCollectionItems: visibleRequestItems
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new PostgresqlProfileTopLevelCollectionMergeProjectionInvoker(
                storedRows
            )
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        long schoolId,
        JsonNode body,
        DocumentUuid documentUuid,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileTopLevelCollectionMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateDocumentInfo(schoolId, body),
            MappingSet: mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        long schoolId,
        JsonNode writeBody,
        DocumentUuid documentUuid,
        BackendProfileWriteContext profileContext,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileTopLevelCollectionMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );

        var updateRequest = new UpdateRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateDocumentInfo(schoolId, writeBody),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<UpsertResult> ExecuteProfiledPostAsync(
        ServiceProvider serviceProvider,
        PostgresqlGeneratedDdlTestDatabase database,
        MappingSet mappingSet,
        long schoolId,
        JsonNode writeBody,
        DocumentUuid documentUuid,
        BackendProfileWriteContext profileContext,
        string traceLabel
    )
    {
        using var scope = serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "PostgresqlProfileTopLevelCollectionMerge",
                    ConnectionString: database.ConnectionString,
                    RouteContext: []
                )
            );

        var upsertRequest = new UpsertRequest(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateDocumentInfo(schoolId, writeBody),
            MappingSet: mappingSet,
            EdfiDoc: writeBody,
            Headers: [],
            TraceId: new TraceId(traceLabel),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new PostgresqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new PostgresqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<long> ReadDocumentIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid.Value)
        );

        rows.Should().HaveCount(1);
        return GetInt64(rows[0], "DocumentId");
    }

    public static async Task<IReadOnlyList<PostgresqlProfileTopLevelCollectionAddressRow>> ReadAddressesAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "City"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new PostgresqlProfileTopLevelCollectionAddressRow(
                CollectionItemId: GetInt64(row, "CollectionItemId"),
                SchoolDocumentId: GetInt64(row, "School_DocumentId"),
                Ordinal: GetInt32(row, "Ordinal"),
                City: GetString(row, "City")
            ))
            .ToArray();
    }

    public static async Task<long?> ReadAddressTypeDescriptorIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId,
        string city
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "AddressTypeDescriptor_DescriptorId"
            FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @documentId
              AND "City" = @city;
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("city", city)
        );

        rows.Should().HaveCount(1);
        return GetNullableInt64(rows[0], "AddressTypeDescriptor_DescriptorId");
    }

    public static Task<long> ReadDocumentCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "dms"."Document";
            """
        );

    public static Task<long> ReadAddressCountAsync(PostgresqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "edfi"."SchoolAddress";
            """
        );

    public static ImmutableArray<string> HiddenCityPath() => ImmutableArray.Create("city");

    public static ImmutableArray<string> HiddenAddressTypeDescriptorPath() =>
        ImmutableArray.Create("addressTypeDescriptor");

    public static async Task<long> SeedAddressTypeDescriptorAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        string codeValue
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "AddressTypeDescriptor");
        var documentId = await database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        var uri = $"uri://ed-fi.org/AddressTypeDescriptor#{codeValue}";
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."Descriptor" (
                "DocumentId",
                "Namespace",
                "CodeValue",
                "ShortDescription",
                "Description",
                "Discriminator",
                "Uri"
            )
            VALUES (
                @documentId,
                @namespace,
                @codeValue,
                @codeValue,
                @codeValue,
                @discriminator,
                @uri
            );
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", "uri://ed-fi.org/AddressTypeDescriptor"),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("discriminator", "Ed-Fi:AddressTypeDescriptor"),
            new NpgsqlParameter("uri", uri)
        );

        var descriptorReference = CreateAddressTypeDescriptorReference(
            uri,
            "$.addresses[0].addressTypeDescriptor"
        );
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO "dms"."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO NOTHING;
            """,
            new NpgsqlParameter("referentialId", descriptorReference.ReferentialId.Value),
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );

        return documentId;
    }

    private static CollectionRowAddress CreateCollectionRowAddress(string identityPath, string city) =>
        new(
            AddressScope,
            new ScopeInstanceAddress("$", []),
            [new SemanticIdentityPart(identityPath, JsonValue.Create(city), IsPresent: true)]
        );

    private static IReadOnlyList<DescriptorReference> CreateAddressTypeDescriptorReferences(JsonNode body)
    {
        if (body["addresses"] is not JsonArray addresses)
        {
            return [];
        }

        List<DescriptorReference> references = [];
        for (var index = 0; index < addresses.Count; index++)
        {
            var descriptorUri = addresses[index]?["addressTypeDescriptor"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(descriptorUri))
            {
                continue;
            }

            references.Add(
                CreateAddressTypeDescriptorReference(
                    descriptorUri,
                    $"$.addresses[{index}].addressTypeDescriptor"
                )
            );
        }

        return references;
    }

    private static DescriptorReference CreateAddressTypeDescriptorReference(
        string uri,
        string referenceJsonPath
    )
    {
        var descriptorResourceInfo = new BaseResourceInfo(
            new ProjectName("Ed-Fi"),
            new ResourceName("AddressTypeDescriptor"),
            true
        );
        var descriptorIdentity = new DocumentIdentity([
            new DocumentIdentityElement(DocumentIdentity.DescriptorIdentityJsonPath, uri.ToLowerInvariant()),
        ]);

        return new DescriptorReference(
            ResourceInfo: descriptorResourceInfo,
            DocumentIdentity: descriptorIdentity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(
                descriptorResourceInfo,
                descriptorIdentity
            ),
            Path: new JsonPath(referenceJsonPath)
        );
    }

    private static async Task<short> GetResourceKeyIdAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        string projectName,
        string resourceName
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );

        rows.Should().HaveCount(1);
        return Convert.ToInt16(rows[0]["ResourceKeyId"], CultureInfo.InvariantCulture);
    }

    private static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        var value =
            row[columnName] ?? throw new InvalidOperationException($"Column '{columnName}' was null.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long? GetNullableInt64(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        var value = row[columnName];
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        var value =
            row[columnName] ?? throw new InvalidOperationException($"Column '{columnName}' was null.");
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row[columnName] as string
        ?? throw new InvalidOperationException($"Column '{columnName}' was not a string.");
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Profiled_TopLevelCollection_Merge
{
    private const long SchoolId = 255901;
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("eeee0001-0000-0000-0000-000000000001")
    );

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            PostgresqlProfileTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = PostgresqlProfileTopLevelCollectionMergeSupport.CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null!;
        }
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
    public async Task It_updates_visible_rows_and_preserves_hidden_rows()
    {
        var before = await SeedAndReadAddressesAsync("Austin", "Dallas", "Houston");
        var idByCity = before.ToDictionary(row => row.City, row => row.CollectionItemId);
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Houston",
            "Austin"
        );

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems:
            [
                new PostgresqlProfileTopLevelCollectionRequestItem("Houston", Creatable: true),
                new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true),
            ],
            visibleStoredRows:
            [
                new PostgresqlProfileTopLevelCollectionStoredRow(
                    "Austin",
                    PostgresqlProfileTopLevelCollectionMergeSupport.HiddenCityPath()
                ),
                new PostgresqlProfileTopLevelCollectionStoredRow("Houston", []),
            ],
            "pgsql-top-level-collection-update-preserve-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after
            .Should()
            .Equal(
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    idByCity["Houston"],
                    documentId,
                    0,
                    "Houston"
                ),
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    idByCity["Dallas"],
                    documentId,
                    1,
                    "Dallas"
                ),
                new PostgresqlProfileTopLevelCollectionAddressRow(idByCity["Austin"], documentId, 2, "Austin")
            );
    }

    [Test]
    public async Task It_preserves_hidden_descriptor_binding_on_matched_visible_row()
    {
        var physicalDescriptorId =
            await PostgresqlProfileTopLevelCollectionMergeSupport.SeedAddressTypeDescriptorAsync(
                _database,
                Guid.Parse("eeee0001-0000-0000-0000-000000000101"),
                "Physical"
            );
        var mailingDescriptorId =
            await PostgresqlProfileTopLevelCollectionMergeSupport.SeedAddressTypeDescriptorAsync(
                _database,
                Guid.Parse("eeee0001-0000-0000-0000-000000000102"),
                "Mailing"
            );
        var seedBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            [
                new PostgresqlProfileTopLevelCollectionAddressInput(
                    "Austin",
                    PostgresqlProfileTopLevelCollectionMergeSupport.PhysicalAddressTypeDescriptorUri
                ),
            ]
        );
        var seedResult = await PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            seedBody,
            SchoolDocumentUuid,
            "pgsql-top-level-collection-descriptor-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var beforeDescriptorId =
            await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressTypeDescriptorIdAsync(
                _database,
                documentId,
                "Austin"
            );
        beforeDescriptorId.Should().Be(physicalDescriptorId);

        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            [
                new PostgresqlProfileTopLevelCollectionAddressInput(
                    "Austin",
                    PostgresqlProfileTopLevelCollectionMergeSupport.MailingAddressTypeDescriptorUri
                ),
            ]
        );
        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true)],
            visibleStoredRows:
            [
                new PostgresqlProfileTopLevelCollectionStoredRow(
                    "Austin",
                    PostgresqlProfileTopLevelCollectionMergeSupport.HiddenAddressTypeDescriptorPath()
                ),
            ],
            "pgsql-top-level-collection-hidden-descriptor-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var afterDescriptorId =
            await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressTypeDescriptorIdAsync(
                _database,
                documentId,
                "Austin"
            );
        afterDescriptorId.Should().Be(physicalDescriptorId);
        afterDescriptorId.Should().NotBe(mailingDescriptorId);
    }

    [Test]
    public async Task It_deletes_omitted_visible_rows_and_preserves_hidden_rows()
    {
        var before = await SeedAndReadAddressesAsync("Austin", "Dallas", "Houston");
        var idByCity = before.ToDictionary(row => row.City, row => row.CollectionItemId);
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true)],
            visibleStoredRows:
            [
                new PostgresqlProfileTopLevelCollectionStoredRow("Austin", []),
                new PostgresqlProfileTopLevelCollectionStoredRow("Houston", []),
            ],
            "pgsql-top-level-collection-delete-visible-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after
            .Should()
            .Equal(
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    idByCity["Austin"],
                    documentId,
                    0,
                    "Austin"
                ),
                new PostgresqlProfileTopLevelCollectionAddressRow(idByCity["Dallas"], documentId, 1, "Dallas")
            );
    }

    [Test]
    public async Task It_rejects_non_creatable_new_visible_items_before_dml()
    {
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");
        var result = await ExecuteProfiledPostAsync(
            writeBody,
            requestItems: [new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: false)],
            visibleStoredRows: [],
            "pgsql-top-level-collection-non-creatable-post"
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();
        (await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentCountAsync(_database))
            .Should()
            .Be(0);
        (await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressCountAsync(_database))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_creates_new_profiled_post_with_creatable_collection_items()
    {
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Austin",
            "Dallas"
        );
        var result = await ExecuteProfiledPostAsync(
            writeBody,
            requestItems:
            [
                new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true),
                new PostgresqlProfileTopLevelCollectionRequestItem("Dallas", Creatable: true),
            ],
            visibleStoredRows: [],
            "pgsql-top-level-collection-creatable-post"
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after.Should().HaveCount(2);
        after[0].SchoolDocumentId.Should().Be(documentId);
        after[0].Ordinal.Should().Be(0);
        after[0].City.Should().Be("Austin");
        after[0].CollectionItemId.Should().BeGreaterThan(0);
        after[1].SchoolDocumentId.Should().Be(documentId);
        after[1].Ordinal.Should().Be(1);
        after[1].City.Should().Be("Dallas");
        after[1].CollectionItemId.Should().BeGreaterThan(0);
        after[1].CollectionItemId.Should().NotBe(after[0].CollectionItemId);
    }

    [Test]
    public async Task It_allows_existing_visible_updates_when_creatable_false_and_rejects_new_items()
    {
        var before = await SeedAndReadAddressesAsync("Austin", "Dallas", "Houston");
        var idByCity = before.ToDictionary(row => row.City, row => row.CollectionItemId);
        var writeAllowedBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Austin"
        );

        var allowedResult = await ExecuteProfiledPutAsync(
            writeAllowedBody,
            requestItems: [new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: false)],
            visibleStoredRows: [new PostgresqlProfileTopLevelCollectionStoredRow("Austin", [])],
            "pgsql-top-level-collection-create-denied-update-put"
        );

        allowedResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var afterAllowed = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );
        afterAllowed
            .Should()
            .Equal(
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    idByCity["Austin"],
                    documentId,
                    0,
                    "Austin"
                ),
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    idByCity["Dallas"],
                    documentId,
                    1,
                    "Dallas"
                ),
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    idByCity["Houston"],
                    documentId,
                    2,
                    "Houston"
                )
            );

        var writeRejectedBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Austin",
            "San Antonio"
        );
        var rejectedResult = await ExecuteProfiledPutAsync(
            writeRejectedBody,
            requestItems:
            [
                new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: false),
                new PostgresqlProfileTopLevelCollectionRequestItem("San Antonio", Creatable: false),
            ],
            visibleStoredRows: [new PostgresqlProfileTopLevelCollectionStoredRow("Austin", [])],
            "pgsql-top-level-collection-create-denied-insert-put"
        );

        rejectedResult.Should().BeOfType<UpdateResult.UpdateFailureProfileDataPolicy>();
        var afterRejected = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );
        afterRejected.Should().Equal(afterAllowed);
    }

    [Test]
    public async Task It_deletes_all_visible_rows_while_hidden_rows_remain()
    {
        var before = await SeedAndReadAddressesAsync("Austin", "Dallas", "Houston");
        var idByCity = before.ToDictionary(row => row.City, row => row.CollectionItemId);
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId);

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [],
            visibleStoredRows:
            [
                new PostgresqlProfileTopLevelCollectionStoredRow("Austin", []),
                new PostgresqlProfileTopLevelCollectionStoredRow("Houston", []),
            ],
            "pgsql-top-level-collection-delete-all-visible-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after
            .Should()
            .Equal(
                new PostgresqlProfileTopLevelCollectionAddressRow(idByCity["Dallas"], documentId, 0, "Dallas")
            );
    }

    [Test]
    public async Task It_inserts_when_no_rows_were_previously_visible_and_preserves_hidden_rows()
    {
        var before = await SeedAndReadAddressesAsync("Dallas");
        var hiddenCollectionItemId = before.Single().CollectionItemId;
        var writeBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new PostgresqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true)],
            visibleStoredRows: [],
            "pgsql-top-level-collection-no-previous-visible-insert-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after.Should().HaveCount(2);
        after[0]
            .Should()
            .Be(
                new PostgresqlProfileTopLevelCollectionAddressRow(
                    hiddenCollectionItemId,
                    documentId,
                    0,
                    "Dallas"
                )
            );
        after[1].SchoolDocumentId.Should().Be(documentId);
        after[1].Ordinal.Should().Be(1);
        after[1].City.Should().Be("Austin");
        after[1].CollectionItemId.Should().NotBe(hiddenCollectionItemId);
    }

    private async Task<
        IReadOnlyList<PostgresqlProfileTopLevelCollectionAddressRow>
    > SeedAndReadAddressesAsync(params string[] cities)
    {
        var seedBody = PostgresqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, cities);
        var seedResult = await PostgresqlProfileTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            seedBody,
            SchoolDocumentUuid,
            "pgsql-top-level-collection-seed"
        );
        seedResult
            .Should()
            .BeOfType<UpsertResult.InsertSuccess>($"seed returned {FormatValidationFailures(seedResult)}");

        var documentId = await PostgresqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        return await PostgresqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );
    }

    private async Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionRequestItem> requestItems,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionStoredRow> visibleStoredRows,
        string traceLabel
    )
    {
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileTopLevelCollectionMergeSupport.SchoolResource
        ];
        var profileContext = PostgresqlProfileTopLevelCollectionMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            requestItems,
            visibleStoredRows
        );

        return await PostgresqlProfileTopLevelCollectionMergeSupport.ExecuteProfiledPutAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            writeBody,
            SchoolDocumentUuid,
            profileContext,
            traceLabel
        );
    }

    private async Task<UpsertResult> ExecuteProfiledPostAsync(
        JsonNode writeBody,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionRequestItem> requestItems,
        IReadOnlyList<PostgresqlProfileTopLevelCollectionStoredRow> visibleStoredRows,
        string traceLabel
    )
    {
        var writePlan = _mappingSet.WritePlansByResource[
            PostgresqlProfileTopLevelCollectionMergeSupport.SchoolResource
        ];
        var profileContext = PostgresqlProfileTopLevelCollectionMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            requestItems,
            visibleStoredRows
        );

        return await PostgresqlProfileTopLevelCollectionMergeSupport.ExecuteProfiledPostAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            writeBody,
            SchoolDocumentUuid,
            profileContext,
            traceLabel
        );
    }

    private static string FormatValidationFailures(UpsertResult result) =>
        result is UpsertResult.UpsertFailureValidation validation
            ? string.Join(
                "; ",
                validation.ValidationFailures.Select(failure => $"{failure.Path.Value}: {failure.Message}")
            )
            : result.ToString();
}
