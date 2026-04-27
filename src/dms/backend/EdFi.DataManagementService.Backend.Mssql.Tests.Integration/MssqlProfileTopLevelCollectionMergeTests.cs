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
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler
    : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

internal sealed class MssqlProfileTopLevelCollectionMergeProjectionInvoker(
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

internal sealed record MssqlProfileTopLevelCollectionAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string City
);

internal sealed record MssqlProfileTopLevelCollectionRequestItem(string City, bool Creatable);

internal sealed record MssqlProfileTopLevelCollectionStoredRow(
    string City,
    ImmutableArray<string> HiddenMemberPaths
);

internal sealed record MssqlProfileTopLevelCollectionAddressInput(
    string City,
    string? AddressTypeDescriptor = null
);

internal static class MssqlProfileTopLevelCollectionMergeSupport
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

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static JsonNode CreateSchoolBody(long schoolId, params string[] cities)
    {
        var addresses = cities.Select(city => new MssqlProfileTopLevelCollectionAddressInput(city));
        return CreateSchoolBody(schoolId, addresses.ToArray());
    }

    public static JsonNode CreateSchoolBody(
        long schoolId,
        IReadOnlyList<MssqlProfileTopLevelCollectionAddressInput> addresses
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
        IReadOnlyList<MssqlProfileTopLevelCollectionRequestItem> requestItems,
        IReadOnlyList<MssqlProfileTopLevelCollectionStoredRow> visibleStoredRows,
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
            StoredStateProjectionInvoker: new MssqlProfileTopLevelCollectionMergeProjectionInvoker(storedRows)
        );
    }

    public static async Task<UpsertResult> SeedAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileTopLevelCollectionMerge",
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
            UpdateCascadeHandler: new MssqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: []
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<UpdateResult> ExecuteProfiledPutAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileTopLevelCollectionMerge",
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
            UpdateCascadeHandler: new MssqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    public static async Task<UpsertResult> ExecuteProfiledPostAsync(
        ServiceProvider serviceProvider,
        MssqlGeneratedDdlTestDatabase database,
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
                    InstanceName: "MssqlProfileTopLevelCollectionMerge",
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
            UpdateCascadeHandler: new MssqlProfileTopLevelCollectionMergeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileTopLevelCollectionMergeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpsertDocument(upsertRequest);
    }

    public static async Task<long> ReadDocumentIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        DocumentUuid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid.Value)
        );

        rows.Should().HaveCount(1);
        var documentId = GetInt64(rows[0], "DocumentId");
        documentId.Should().BeGreaterThan(0);
        return documentId;
    }

    public static async Task<IReadOnlyList<MssqlProfileTopLevelCollectionAddressRow>> ReadAddressesAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [School_DocumentId], [Ordinal], [City]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlProfileTopLevelCollectionAddressRow(
                CollectionItemId: GetInt64(row, "CollectionItemId"),
                SchoolDocumentId: GetInt64(row, "School_DocumentId"),
                Ordinal: GetInt32(row, "Ordinal"),
                City: GetString(row, "City")
            ))
            .ToArray();
    }

    public static async Task<long?> ReadAddressTypeDescriptorIdAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId,
        string city
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [AddressTypeDescriptor_DescriptorId]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
              AND [City] = @city;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@city", city)
        );

        rows.Should().HaveCount(1);
        return GetNullableInt64(rows[0], "AddressTypeDescriptor_DescriptorId");
    }

    public static Task<int> ReadDocumentCountAsync(MssqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [dms].[Document];
            """
        );

    public static Task<int> ReadAddressCountAsync(MssqlGeneratedDdlTestDatabase database) =>
        database.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM [edfi].[SchoolAddress];
            """
        );

    public static ImmutableArray<string> HiddenCityPath() => ImmutableArray.Create("city");

    public static ImmutableArray<string> HiddenAddressTypeDescriptorPath() =>
        ImmutableArray.Create("addressTypeDescriptor");

    public static async Task<long> SeedAddressTypeDescriptorAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid,
        string codeValue
    )
    {
        var resourceKeyId = await GetResourceKeyIdAsync(database, "Ed-Fi", "AddressTypeDescriptor");
        var documentId = await database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        var uri = $"uri://ed-fi.org/AddressTypeDescriptor#{codeValue}";
        await database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor] (
                [DocumentId],
                [Namespace],
                [CodeValue],
                [ShortDescription],
                [Description],
                [Discriminator],
                [Uri]
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
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@namespace", "uri://ed-fi.org/AddressTypeDescriptor"),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@discriminator", "Ed-Fi:AddressTypeDescriptor"),
            new SqlParameter("@uri", uri)
        );

        var descriptorReference = CreateAddressTypeDescriptorReference(
            uri,
            "$.addresses[0].addressTypeDescriptor"
        );
        await database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (
                SELECT 1 FROM [dms].[ReferentialIdentity] WHERE [ReferentialId] = @referentialId
            )
            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @documentId, @resourceKeyId);
            """,
            new SqlParameter("@referentialId", descriptorReference.ReferentialId.Value),
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId)
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
        MssqlGeneratedDdlTestDatabase database,
        string projectName,
        string resourceName
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
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
[Category("MssqlIntegration")]
public class Given_A_Mssql_Profiled_TopLevelCollection_Merge
{
    private const long SchoolId = 255901;
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("ffff0001-0000-0000-0000-000000000001")
    );

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

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
            MssqlProfileTopLevelCollectionMergeSupport.FixtureRelativePath
        );
        _mappingSet = _fixture.MappingSet;
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
        _serviceProvider = MssqlProfileTopLevelCollectionMergeSupport.CreateServiceProvider();
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
        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Houston",
            "Austin"
        );

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems:
            [
                new MssqlProfileTopLevelCollectionRequestItem("Houston", Creatable: true),
                new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true),
            ],
            visibleStoredRows:
            [
                new MssqlProfileTopLevelCollectionStoredRow(
                    "Austin",
                    MssqlProfileTopLevelCollectionMergeSupport.HiddenCityPath()
                ),
                new MssqlProfileTopLevelCollectionStoredRow("Houston", []),
            ],
            "mssql-top-level-collection-update-preserve-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after
            .Should()
            .Equal(
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Houston"], documentId, 1, "Houston"),
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Dallas"], documentId, 2, "Dallas"),
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Austin"], documentId, 3, "Austin")
            );
    }

    [Test]
    public async Task It_preserves_hidden_descriptor_binding_on_matched_visible_row()
    {
        var physicalDescriptorId =
            await MssqlProfileTopLevelCollectionMergeSupport.SeedAddressTypeDescriptorAsync(
                _database,
                Guid.Parse("ffff0001-0000-0000-0000-000000000101"),
                "Physical"
            );
        var mailingDescriptorId =
            await MssqlProfileTopLevelCollectionMergeSupport.SeedAddressTypeDescriptorAsync(
                _database,
                Guid.Parse("ffff0001-0000-0000-0000-000000000102"),
                "Mailing"
            );
        var seedBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            [
                new MssqlProfileTopLevelCollectionAddressInput(
                    "Austin",
                    MssqlProfileTopLevelCollectionMergeSupport.PhysicalAddressTypeDescriptorUri
                ),
            ]
        );
        var seedResult = await MssqlProfileTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            seedBody,
            SchoolDocumentUuid,
            "mssql-top-level-collection-descriptor-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var beforeDescriptorId =
            await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressTypeDescriptorIdAsync(
                _database,
                documentId,
                "Austin"
            );
        beforeDescriptorId.Should().Be(physicalDescriptorId);

        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            [
                new MssqlProfileTopLevelCollectionAddressInput(
                    "Austin",
                    MssqlProfileTopLevelCollectionMergeSupport.MailingAddressTypeDescriptorUri
                ),
            ]
        );
        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true)],
            visibleStoredRows:
            [
                new MssqlProfileTopLevelCollectionStoredRow(
                    "Austin",
                    MssqlProfileTopLevelCollectionMergeSupport.HiddenAddressTypeDescriptorPath()
                ),
            ],
            "mssql-top-level-collection-hidden-descriptor-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var afterDescriptorId =
            await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressTypeDescriptorIdAsync(
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
        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true)],
            visibleStoredRows:
            [
                new MssqlProfileTopLevelCollectionStoredRow("Austin", []),
                new MssqlProfileTopLevelCollectionStoredRow("Houston", []),
            ],
            "mssql-top-level-collection-delete-visible-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after
            .Should()
            .Equal(
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Austin"], documentId, 1, "Austin"),
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Dallas"], documentId, 2, "Dallas")
            );
    }

    [Test]
    public async Task It_rejects_non_creatable_new_visible_items_before_dml()
    {
        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");
        var result = await ExecuteProfiledPostAsync(
            writeBody,
            requestItems: [new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: false)],
            visibleStoredRows: [],
            "mssql-top-level-collection-non-creatable-post"
        );

        result.Should().BeOfType<UpsertResult.UpsertFailureProfileDataPolicy>();
        (await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentCountAsync(_database)).Should().Be(0);
        (await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressCountAsync(_database)).Should().Be(0);
    }

    [Test]
    public async Task It_creates_new_profiled_post_with_creatable_collection_items()
    {
        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Austin",
            "Dallas"
        );
        var result = await ExecuteProfiledPostAsync(
            writeBody,
            requestItems:
            [
                new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true),
                new MssqlProfileTopLevelCollectionRequestItem("Dallas", Creatable: true),
            ],
            visibleStoredRows: [],
            "mssql-top-level-collection-creatable-post"
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after.Should().HaveCount(2);
        after[0].SchoolDocumentId.Should().Be(documentId);
        after[0].Ordinal.Should().Be(1);
        after[0].City.Should().Be("Austin");
        after[0].CollectionItemId.Should().BeGreaterThan(0);
        after[1].SchoolDocumentId.Should().Be(documentId);
        after[1].Ordinal.Should().Be(2);
        after[1].City.Should().Be("Dallas");
        after[1].CollectionItemId.Should().BeGreaterThan(0);
        after[1].CollectionItemId.Should().NotBe(after[0].CollectionItemId);
    }

    [Test]
    public async Task It_allows_existing_visible_updates_when_creatable_false_and_rejects_new_items()
    {
        var before = await SeedAndReadAddressesAsync("Austin", "Dallas", "Houston");
        var idByCity = before.ToDictionary(row => row.City, row => row.CollectionItemId);
        var writeAllowedBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Austin"
        );

        var allowedResult = await ExecuteProfiledPutAsync(
            writeAllowedBody,
            requestItems: [new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: false)],
            visibleStoredRows: [new MssqlProfileTopLevelCollectionStoredRow("Austin", [])],
            "mssql-top-level-collection-create-denied-update-put"
        );

        allowedResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var afterAllowed = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );
        afterAllowed
            .Should()
            .Equal(
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Austin"], documentId, 1, "Austin"),
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Dallas"], documentId, 2, "Dallas"),
                new MssqlProfileTopLevelCollectionAddressRow(idByCity["Houston"], documentId, 3, "Houston")
            );

        var writeRejectedBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(
            SchoolId,
            "Austin",
            "San Antonio"
        );
        var rejectedResult = await ExecuteProfiledPutAsync(
            writeRejectedBody,
            requestItems:
            [
                new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: false),
                new MssqlProfileTopLevelCollectionRequestItem("San Antonio", Creatable: false),
            ],
            visibleStoredRows: [new MssqlProfileTopLevelCollectionStoredRow("Austin", [])],
            "mssql-top-level-collection-create-denied-insert-put"
        );

        rejectedResult.Should().BeOfType<UpdateResult.UpdateFailureProfileDataPolicy>();
        var afterRejected = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
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
        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId);

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [],
            visibleStoredRows:
            [
                new MssqlProfileTopLevelCollectionStoredRow("Austin", []),
                new MssqlProfileTopLevelCollectionStoredRow("Houston", []),
            ],
            "mssql-top-level-collection-delete-all-visible-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after
            .Should()
            .Equal(new MssqlProfileTopLevelCollectionAddressRow(idByCity["Dallas"], documentId, 1, "Dallas"));
    }

    [Test]
    public async Task It_inserts_when_no_rows_were_previously_visible_and_preserves_hidden_rows()
    {
        var before = await SeedAndReadAddressesAsync("Dallas");
        var hiddenCollectionItemId = before.Single().CollectionItemId;
        var writeBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, "Austin");

        var result = await ExecuteProfiledPutAsync(
            writeBody,
            requestItems: [new MssqlProfileTopLevelCollectionRequestItem("Austin", Creatable: true)],
            visibleStoredRows: [],
            "mssql-top-level-collection-no-previous-visible-insert-put"
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        var after = await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(
            _database,
            documentId
        );

        after.Should().HaveCount(2);
        after[0]
            .Should()
            .Be(
                new MssqlProfileTopLevelCollectionAddressRow(hiddenCollectionItemId, documentId, 1, "Dallas")
            );
        after[1].SchoolDocumentId.Should().Be(documentId);
        after[1].Ordinal.Should().Be(2);
        after[1].City.Should().Be("Austin");
        after[1].CollectionItemId.Should().NotBe(hiddenCollectionItemId);
    }

    private async Task<IReadOnlyList<MssqlProfileTopLevelCollectionAddressRow>> SeedAndReadAddressesAsync(
        params string[] cities
    )
    {
        var seedBody = MssqlProfileTopLevelCollectionMergeSupport.CreateSchoolBody(SchoolId, cities);
        var seedResult = await MssqlProfileTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            seedBody,
            SchoolDocumentUuid,
            "mssql-top-level-collection-seed"
        );
        seedResult.Should().BeOfType<UpsertResult.InsertSuccess>();

        var documentId = await MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(
            _database,
            SchoolDocumentUuid
        );
        return await MssqlProfileTopLevelCollectionMergeSupport.ReadAddressesAsync(_database, documentId);
    }

    private async Task<UpdateResult> ExecuteProfiledPutAsync(
        JsonNode writeBody,
        IReadOnlyList<MssqlProfileTopLevelCollectionRequestItem> requestItems,
        IReadOnlyList<MssqlProfileTopLevelCollectionStoredRow> visibleStoredRows,
        string traceLabel
    )
    {
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileTopLevelCollectionMergeSupport.SchoolResource
        ];
        var profileContext = MssqlProfileTopLevelCollectionMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            requestItems,
            visibleStoredRows
        );

        return await MssqlProfileTopLevelCollectionMergeSupport.ExecuteProfiledPutAsync(
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
        IReadOnlyList<MssqlProfileTopLevelCollectionRequestItem> requestItems,
        IReadOnlyList<MssqlProfileTopLevelCollectionStoredRow> visibleStoredRows,
        string traceLabel
    )
    {
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileTopLevelCollectionMergeSupport.SchoolResource
        ];
        var profileContext = MssqlProfileTopLevelCollectionMergeSupport.CreateProfileContext(
            writePlan,
            writeBody.DeepClone(),
            requestItems,
            visibleStoredRows
        );

        return await MssqlProfileTopLevelCollectionMergeSupport.ExecuteProfiledPostAsync(
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
}
