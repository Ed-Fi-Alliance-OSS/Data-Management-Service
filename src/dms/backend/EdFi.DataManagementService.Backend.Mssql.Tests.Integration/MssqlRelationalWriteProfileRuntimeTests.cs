// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Plans;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

file sealed class MssqlProfileRuntimeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class MssqlProfileRuntimeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
{
    public UpdateCascadeResult Cascade(
        JsonElement originalEdFiDoc,
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

file sealed class MssqlProfileRuntimeConcurrentContentVersionBumpFreshnessChecker(
    IDmsInstanceSelection dmsInstanceSelection
) : IRelationalWriteFreshnessChecker
{
    private readonly IDmsInstanceSelection _dmsInstanceSelection =
        dmsInstanceSelection ?? throw new ArgumentNullException(nameof(dmsInstanceSelection));

    private readonly RelationalWriteFreshnessChecker _innerChecker = new();
    private bool _hasBumpedContentVersion;

    public async Task<bool> IsCurrentAsync(
        RelationalWriteExecutorRequest request,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        if (!_hasBumpedContentVersion)
        {
            _hasBumpedContentVersion = true;

            await BumpContentVersionAsync(targetContext.DocumentId, cancellationToken);
        }

        return await _innerChecker.IsCurrentAsync(request, targetContext, writeSession, cancellationToken);
    }

    private async Task BumpContentVersionAsync(long documentId, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = new(
            _dmsInstanceSelection.GetSelectedDmsInstance().ConnectionString
        );
        await connection.OpenAsync(cancellationToken);

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE [dms].[Document]
            SET [ContentVersion] = [ContentVersion] + 1
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.Add(new SqlParameter("@documentId", documentId));

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one document content-version bump for document id '{documentId}', but affected {rowsAffected} rows."
            );
        }
    }
}

internal sealed record MssqlProfileRuntimeDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record MssqlProfileRuntimeSchoolRow(long DocumentId, long SchoolId, string? ShortName);

internal sealed record MssqlProfileRuntimeSchoolExtensionRow(long DocumentId, string? CampusCode);

internal sealed record MssqlProfileRuntimeSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? City
);

internal sealed record MssqlProfileRuntimeSchoolExtensionInterventionRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? InterventionCode
);

internal sealed record MssqlProfileRuntimeSchoolExtensionInterventionVisitRow(
    long CollectionItemId,
    long ParentCollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? VisitCode
);

internal sealed record MssqlProfileRuntimeSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string? Zone
);

internal sealed record MssqlProfileRuntimePersistedState(
    MssqlProfileRuntimeDocumentRow Document,
    MssqlProfileRuntimeSchoolRow School,
    MssqlProfileRuntimeSchoolExtensionRow? SchoolExtension,
    IReadOnlyList<MssqlProfileRuntimeSchoolAddressRow> Addresses,
    IReadOnlyList<MssqlProfileRuntimeSchoolExtensionInterventionRow> Interventions,
    IReadOnlyList<MssqlProfileRuntimeSchoolExtensionInterventionVisitRow> InterventionVisits,
    IReadOnlyList<MssqlProfileRuntimeSchoolExtensionAddressRow> ExtensionAddresses,
    long DocumentCount
);

file static class MssqlProfileRuntimeTestSupport
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-update-semantics";

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

    public const string InitialCreateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Fall" }] },
            { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }
          ],
          "_ext": {
            "sample": {
              "campusCode": "North",
              "addresses": [
                { "city": "Austin", "_ext": { "sample": { "zone": "Central" } } },
                { "city": "Dallas", "_ext": { "sample": { "zone": "East" } } }
              ],
              "interventions": [
                { "interventionCode": "INT-A", "visits": [{ "visitCode": "V1" }, { "visitCode": "V2" }] },
                { "interventionCode": "INT-B", "visits": [{ "visitCode": "V3" }] }
              ]
            }
          }
        }
        """;

    public static ServiceProvider CreateServiceProvider(Action<IServiceCollection>? configureServices = null)
    {
        ServiceCollection services = [];

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.Configure<DatabaseOptions>(options => options.IsolationLevel = IsolationLevel.ReadCommitted);
        services.AddTestReadableProfileProjector();
        services.AddScoped<RelationalDocumentStoreRepository>();
        services.AddMssqlReferenceResolver();
        configureServices?.Invoke(services);

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    public static ServiceProvider CreateStaleCompareServiceProvider() =>
        CreateServiceProvider(static services =>
        {
            services.RemoveAll<IRelationalWriteFreshnessChecker>();
            services.AddScoped<
                IRelationalWriteFreshnessChecker,
                MssqlProfileRuntimeConcurrentContentVersionBumpFreshnessChecker
            >();
        });

    public static UpsertRequest CreateCreateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string? requestBodyJsonOverride = null,
        BackendProfileWriteContext? profileContext = null
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJsonOverride ?? InitialCreateBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRuntimeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

    public static UpdateRequest CreateUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        string requestBodyJson,
        BackendProfileWriteContext? profileContext = null
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRuntimeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

    public static UpsertRequest CreatePostAsUpdateRequest(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        string traceId,
        ReferentialId referentialId,
        string requestBodyJson,
        BackendProfileWriteContext? profileContext = null
    ) =>
        new(
            ResourceInfo: SchoolResourceInfo,
            DocumentInfo: CreateSchoolDocumentInfo(referentialId),
            MappingSet: mappingSet,
            EdfiDoc: JsonNode.Parse(requestBodyJson)!,
            Headers: [],
            TraceId: new TraceId(traceId),
            DocumentUuid: documentUuid,
            DocumentSecurityElements: new([], [], [], [], []),
            UpdateCascadeHandler: new MssqlProfileRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new MssqlProfileRuntimeAllowAllResourceAuthorizationHandler(),
            ResourceAuthorizationPathways: [],
            BackendProfileWriteContext: profileContext
        );

    public static DocumentInfo CreateSchoolDocumentInfo(ReferentialId? referentialIdOverride = null)
    {
        var schoolIdentity = new DocumentIdentity([
            new DocumentIdentityElement(new JsonPath("$.schoolId"), "255901"),
        ]);

        return new DocumentInfo(
            DocumentIdentity: schoolIdentity,
            ReferentialId: referentialIdOverride
                ?? ReferentialIdCalculator.ReferentialIdFrom(SchoolResourceInfo, schoolIdentity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public static async Task<MssqlProfileRuntimePersistedState> ReadFullPersistedStateAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var schoolExtension = await ReadSchoolExtensionAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);
        var interventions = await ReadSchoolExtensionInterventionsAsync(database, document.DocumentId);
        var interventionVisits = await ReadSchoolExtensionInterventionVisitsAsync(
            database,
            document.DocumentId
        );
        var extensionAddresses = await ReadSchoolExtensionAddressesAsync(database, document.DocumentId);
        var documentCount = await ReadDocumentCountAsync(database);

        return new MssqlProfileRuntimePersistedState(
            document,
            school,
            schoolExtension,
            addresses,
            interventions,
            interventionVisits,
            extensionAddresses,
            documentCount
        );
    }

    public static async Task<long> ReadDocumentCountAsync(MssqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS [Count]
            FROM [dms].[Document];
            """
        );

        return rows.Count == 1
            ? GetInt64(rows[0], "Count")
            : throw new InvalidOperationException($"Expected exactly one count row, but found {rows.Count}.");
    }

    public static short GetInt16(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt16(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static int GetInt32(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt32(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static long GetInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        Convert.ToInt64(GetRequiredValue(row, columnName), CultureInfo.InvariantCulture);

    public static Guid GetGuid(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) is Guid value
            ? value
            : throw new InvalidOperationException($"Expected column '{columnName}' to contain a Guid value.");

    public static string GetString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        GetRequiredValue(row, columnName) as string
        ?? throw new InvalidOperationException($"Expected column '{columnName}' to contain a string value.");

    public static string? GetNullableString(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value)
            ? value as string
            : throw new InvalidOperationException(
                $"Expected persisted row to contain column '{columnName}'."
            );

    private static async Task<MssqlProfileRuntimeDocumentRow> ReadDocumentAsync(
        MssqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [DocumentUuid], [ResourceKeyId], [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new MssqlProfileRuntimeDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<MssqlProfileRuntimeSchoolRow> ReadSchoolAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [SchoolId], [ShortName]
            FROM [edfi].[School]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count == 1
            ? new MssqlProfileRuntimeSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<MssqlProfileRuntimeSchoolExtensionRow?> ReadSchoolExtensionAsync(
        MssqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [DocumentId], [CampusCode]
            FROM [sample].[SchoolExtension]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Count switch
        {
            0 => null,
            1 => new MssqlProfileRuntimeSchoolExtensionRow(
                GetInt64(rows[0], "DocumentId"),
                GetNullableString(rows[0], "CampusCode")
            ),
            _ => throw new InvalidOperationException(
                $"Expected zero or one school extension row for document id '{documentId}', but found {rows.Count}."
            ),
        };
    }

    private static async Task<IReadOnlyList<MssqlProfileRuntimeSchoolAddressRow>> ReadSchoolAddressesAsync(
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

        return rows.Select(row => new MssqlProfileRuntimeSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "City")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<MssqlProfileRuntimeSchoolExtensionInterventionRow>
    > ReadSchoolExtensionInterventionsAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [School_DocumentId], [Ordinal], [InterventionCode]
            FROM [sample].[SchoolExtensionIntervention]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlProfileRuntimeSchoolExtensionInterventionRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "InterventionCode")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<MssqlProfileRuntimeSchoolExtensionInterventionVisitRow>
    > ReadSchoolExtensionInterventionVisitsAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [ParentCollectionItemId], [School_DocumentId], [Ordinal], [VisitCode]
            FROM [sample].[SchoolExtensionInterventionVisit]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [ParentCollectionItemId], [Ordinal], [CollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlProfileRuntimeSchoolExtensionInterventionVisitRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "VisitCode")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<MssqlProfileRuntimeSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(MssqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT [BaseCollectionItemId], [School_DocumentId], [Zone]
            FROM [sample].[SchoolExtensionAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [BaseCollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );

        return rows.Select(row => new MssqlProfileRuntimeSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetNullableString(row, "Zone")
            ))
            .ToArray();
    }

    private static object GetRequiredValue(IReadOnlyDictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var value) || value is null)
        {
            throw new InvalidOperationException(
                $"Expected persisted row to contain non-null column '{columnName}'."
            );
        }

        return value;
    }
}

file sealed class MssqlProfileRuntimeFixedStoredStateProjectionInvoker(
    ImmutableArray<StoredScopeState> storedScopeStates,
    ImmutableArray<VisibleStoredCollectionRow> visibleStoredCollectionRows
) : IStoredStateProjectionInvoker
{
    private readonly ImmutableArray<StoredScopeState> _storedScopeStates = storedScopeStates;
    private readonly ImmutableArray<VisibleStoredCollectionRow> _visibleStoredCollectionRows =
        visibleStoredCollectionRows;

    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) =>
        new(
            Request: request,
            VisibleStoredBody: storedDocument.DeepClone(),
            StoredScopeStates: _storedScopeStates,
            VisibleStoredCollectionRows: _visibleStoredCollectionRows
        );
}

file sealed class MssqlProfileRuntimeUnexpectedStoredStateProjectionInvoker : IStoredStateProjectionInvoker
{
    public ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    ) =>
        throw new InvalidOperationException(
            "Stored-state projection should not run for create-new profiled requests."
        );
}

file static class MssqlProfileRuntimeContextFactory
{
    private static readonly QualifiedResourceName SchoolResource = new("Ed-Fi", "School");

    private static VisibleRequestCollectionItem CreateVisibleAddressCollectionItem(
        ScopeInstanceAddress parentAddress,
        string city,
        int requestIndex
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$.addresses[*]",
                parentAddress,
                [new SemanticIdentityPart("city", JsonValue.Create(city)!, IsPresent: true)]
            ),
            Creatable: true,
            RequestJsonPath: $"$.addresses[{requestIndex}]"
        );

    private static VisibleStoredCollectionRow CreateVisibleStoredAddressCollectionRow(
        ScopeInstanceAddress parentAddress,
        string city
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$.addresses[*]",
                parentAddress,
                [new SemanticIdentityPart("city", JsonValue.Create(city)!, IsPresent: true)]
            ),
            HiddenMemberPaths: []
        );

    private static ScopeInstanceAddress CreateAddressParentAddress(string city) =>
        new(
            "$.addresses[*]",
            [
                new AncestorCollectionInstance(
                    "$.addresses[*]",
                    [new SemanticIdentityPart("city", JsonValue.Create(city)!, IsPresent: true)]
                ),
            ]
        );

    private static VisibleRequestCollectionItem CreateVisiblePeriodCollectionItem(
        string parentCity,
        string periodName,
        int parentAddressIndex,
        int periodIndex
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$.addresses[*].periods[*]",
                CreateAddressParentAddress(parentCity),
                [new SemanticIdentityPart("periodName", JsonValue.Create(periodName)!, IsPresent: true)]
            ),
            Creatable: true,
            RequestJsonPath: $"$.addresses[{parentAddressIndex}].periods[{periodIndex}]"
        );

    private static VisibleStoredCollectionRow CreateVisibleStoredPeriodCollectionRow(
        string parentCity,
        string periodName
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$.addresses[*].periods[*]",
                CreateAddressParentAddress(parentCity),
                [new SemanticIdentityPart("periodName", JsonValue.Create(periodName)!, IsPresent: true)]
            ),
            HiddenMemberPaths: []
        );

    public static BackendProfileWriteContext CreateGuardedNoOpPutContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[SchoolResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var visibleRequestCollectionItems = ImmutableArray.Create(
            CreateVisibleAddressCollectionItem(rootAddress, "Austin", 0),
            CreateVisibleAddressCollectionItem(rootAddress, "Dallas", 1),
            CreateVisiblePeriodCollectionItem("Austin", "Fall", parentAddressIndex: 0, periodIndex: 0),
            CreateVisiblePeriodCollectionItem("Dallas", "Spring", parentAddressIndex: 1, periodIndex: 0)
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: requestBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.Hidden,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.Hidden,
                        HiddenMemberPaths: []
                    ),
                ],
                visibleStoredCollectionRows:
                [
                    CreateVisibleStoredAddressCollectionRow(rootAddress, "Austin"),
                    CreateVisibleStoredAddressCollectionRow(rootAddress, "Dallas"),
                    CreateVisibleStoredPeriodCollectionRow("Austin", "Fall"),
                    CreateVisibleStoredPeriodCollectionRow("Dallas", "Spring"),
                ]
            )
        );
    }

    public static BackendProfileWriteContext CreateGuardedNoOpPostAsUpdateContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        // Same profile shape as guarded no-op PUT
        return CreateGuardedNoOpPutContext(mappingSet, requestBodyJson);
    }

    public static BackendProfileWriteContext CreateMergeWithHiddenDataPreservationContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[SchoolResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var visibleRequestCollectionItems = ImmutableArray.Create(
            CreateVisibleAddressCollectionItem(rootAddress, "Austin", 0),
            CreateVisibleAddressCollectionItem(rootAddress, "Houston", 1),
            CreateVisiblePeriodCollectionItem("Austin", "Fall", parentAddressIndex: 0, periodIndex: 0)
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: requestBody,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.Hidden,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.Hidden,
                        HiddenMemberPaths: []
                    ),
                ],
                visibleStoredCollectionRows:
                [
                    CreateVisibleStoredAddressCollectionRow(rootAddress, "Austin"),
                    CreateVisibleStoredPeriodCollectionRow("Austin", "Fall"),
                ]
            )
        );
    }

    public static BackendProfileWriteContext CreateRootCreateRejectionContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[SchoolResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: requestBody,
            RootResourceCreatable: false,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new MssqlProfileRuntimeUnexpectedStoredStateProjectionInvoker()
        );
    }
}

// --------------------------------------------------------------------------
// Scenario 1: Profiled Guarded No-Op PUT
// --------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Profiled_Guarded_NoOp_PUT
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000201")
    );

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Fall" }] },
            { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }
          ]
        }
        """;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlProfileRuntimePersistedState _stateAfterCreate = null!;
    private MssqlProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();
        _stateAfterCreate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-guarded-noop-put-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteProfiledUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-guarded-noop-put-update",
                UpdateBodyJson,
                MssqlProfileRuntimeContextFactory.CreateGuardedNoOpPutContext(_mappingSet, UpdateBodyJson)
            )
        );
    }

    [Test]
    public void It_returns_update_success()
    {
        var failureMessage = _updateResult is UpdateResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "profiled guarded no-op PUT should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_does_not_change_content_version()
    {
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateAfterCreate.Document.ContentVersion);
    }

    [Test]
    public void It_preserves_school_row_unchanged()
    {
        _stateAfterUpdate.School.Should().Be(_stateAfterCreate.School);
    }

    [Test]
    public void It_preserves_extension_row_unchanged()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension.Should().Be(_stateAfterCreate.SchoolExtension);
    }

    [Test]
    public void It_preserves_address_rows_unchanged()
    {
        _stateAfterUpdate.Addresses.Should().HaveCount(2);
        _stateAfterUpdate.Addresses[0].City.Should().Be("Austin");
        _stateAfterUpdate.Addresses[1].City.Should().Be("Dallas");
    }

    [Test]
    public void It_preserves_intervention_rows_unchanged()
    {
        _stateAfterUpdate.Interventions.Should().HaveCount(2);
        _stateAfterUpdate.Interventions[0].InterventionCode.Should().Be("INT-A");
        _stateAfterUpdate.Interventions[1].InterventionCode.Should().Be("INT-B");
    }

    [Test]
    public void It_preserves_intervention_visit_rows_unchanged()
    {
        _stateAfterUpdate.InterventionVisits.Should().HaveCount(3);
    }

    [Test]
    public void It_preserves_extension_address_rows_unchanged()
    {
        _stateAfterUpdate.ExtensionAddresses.Should().HaveCount(2);
    }
}

// --------------------------------------------------------------------------
// Scenario 2: Profiled Guarded No-Op POST-as-update
// --------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Profiled_Guarded_NoOp_POST_As_Update
{
    private static readonly DocumentUuid SeedDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000202")
    );

    private static readonly DocumentUuid PostDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000202")
    );

    private const string PostBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Fall" }] },
            { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }
          ]
        }
        """;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlProfileRuntimePersistedState _stateAfterCreate = null!;
    private MssqlProfileRuntimePersistedState _stateAfterPost = null!;
    private UpsertResult _postResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();
        _stateAfterCreate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SeedDocumentUuid.Value
        );

        _postResult = await ExecuteProfiledPostAsUpdateAsync();
        _stateAfterPost = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SeedDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SeedDocumentUuid,
                "mssql-profiled-guarded-noop-post-as-update-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecuteProfiledPostAsUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        var referentialId = MssqlProfileRuntimeTestSupport.CreateSchoolDocumentInfo().ReferentialId;

        return await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                PostDocumentUuid,
                "mssql-profiled-guarded-noop-post-as-update",
                referentialId,
                PostBodyJson,
                MssqlProfileRuntimeContextFactory.CreateGuardedNoOpPostAsUpdateContext(
                    _mappingSet,
                    PostBodyJson
                )
            )
        );
    }

    [Test]
    public void It_returns_update_success()
    {
        _postResult.Should().BeOfType<UpsertResult.UpdateSuccess>();
        _postResult.As<UpsertResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SeedDocumentUuid);
    }

    [Test]
    public void It_does_not_change_content_version()
    {
        _stateAfterPost.Document.ContentVersion.Should().Be(_stateAfterCreate.Document.ContentVersion);
    }

    [Test]
    public void It_preserves_the_existing_document_uuid()
    {
        _stateAfterPost.Document.DocumentUuid.Should().Be(SeedDocumentUuid.Value);
    }

    [Test]
    public void It_does_not_create_a_second_document()
    {
        _stateAfterPost.DocumentCount.Should().Be(_stateAfterCreate.DocumentCount);
    }

    [Test]
    public void It_preserves_school_row_unchanged()
    {
        _stateAfterPost.School.Should().Be(_stateAfterCreate.School);
    }

    [Test]
    public void It_preserves_extension_row_unchanged()
    {
        _stateAfterPost.SchoolExtension.Should().NotBeNull();
        _stateAfterPost.SchoolExtension.Should().Be(_stateAfterCreate.SchoolExtension);
    }

    [Test]
    public void It_preserves_address_rows_unchanged()
    {
        _stateAfterPost.Addresses.Should().HaveCount(2);
        _stateAfterPost.Addresses[0].City.Should().Be("Austin");
        _stateAfterPost.Addresses[1].City.Should().Be("Dallas");
    }
}

// --------------------------------------------------------------------------
// Scenario 3: Profiled Stale Guarded No-Op PUT
// --------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Profiled_Stale_Guarded_NoOp_PUT
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000205")
    );

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Fall" }] },
            { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }
          ]
        }
        """;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlProfileRuntimePersistedState _stateBeforeUpdate = null!;
    private MssqlProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRuntimeTestSupport.CreateStaleCompareServiceProvider();

        await ExecuteInitialCreateAsync();
        _stateBeforeUpdate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_retries_and_returns_update_success_after_the_profiled_no_op_compare_goes_stale()
    {
        var failureMessage = _updateResult is UpdateResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "profiled stale guarded no-op PUT should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_preserves_rowsets_but_keeps_the_concurrent_content_version_bump()
    {
        var adjustedAfterState = _stateAfterUpdate with
        {
            Document = _stateAfterUpdate.Document with
            {
                ContentVersion = _stateBeforeUpdate.Document.ContentVersion,
            },
        };

        adjustedAfterState.Should().BeEquivalentTo(_stateBeforeUpdate);
        _stateAfterUpdate.Document.ContentVersion.Should().Be(_stateBeforeUpdate.Document.ContentVersion + 1);
    }

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledStaleGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-stale-guarded-noop-put-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteProfiledUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledStaleGuardedNoOpPut",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-stale-guarded-noop-put-update",
                UpdateBodyJson,
                MssqlProfileRuntimeContextFactory.CreateGuardedNoOpPutContext(_mappingSet, UpdateBodyJson)
            )
        );
    }
}

// --------------------------------------------------------------------------
// Scenario 4: Profiled Stale Guarded No-Op POST-as-update
// --------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Profiled_Stale_Guarded_NoOp_POST_As_Update
{
    private static readonly DocumentUuid SeedDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000206")
    );

    private static readonly DocumentUuid PostDocumentUuid = new(
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000206")
    );

    private const string PostBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Fall" }] },
            { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }
          ]
        }
        """;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlProfileRuntimePersistedState _stateBeforePost = null!;
    private MssqlProfileRuntimePersistedState _stateAfterPost = null!;
    private UpsertResult _postResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRuntimeTestSupport.CreateStaleCompareServiceProvider();

        await ExecuteInitialCreateAsync();
        _stateBeforePost = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SeedDocumentUuid.Value
        );

        _postResult = await ExecuteProfiledPostAsUpdateAsync();
        _stateAfterPost = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SeedDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public void It_retries_and_returns_update_success_for_a_profiled_stale_post_as_update_no_op_compare()
    {
        var failureMessage = _postResult is UpsertResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "profiled stale guarded no-op POST-as-update should succeed";

        _postResult.Should().BeOfType<UpsertResult.UpdateSuccess>(failureMessage);
        _postResult.As<UpsertResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SeedDocumentUuid);
    }

    [Test]
    public void It_preserves_the_existing_rowsets_but_keeps_the_concurrent_content_version_bump()
    {
        var adjustedAfterState = _stateAfterPost with
        {
            Document = _stateAfterPost.Document with
            {
                ContentVersion = _stateBeforePost.Document.ContentVersion,
            },
        };

        adjustedAfterState.Should().BeEquivalentTo(_stateBeforePost);
        _stateAfterPost.Document.ContentVersion.Should().Be(_stateBeforePost.Document.ContentVersion + 1);
    }

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledStaleGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SeedDocumentUuid,
                "mssql-profiled-stale-guarded-noop-post-as-update-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpsertResult> ExecuteProfiledPostAsUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledStaleGuardedNoOpPostAsUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var referentialId = MssqlProfileRuntimeTestSupport.CreateSchoolDocumentInfo().ReferentialId;

        return await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                PostDocumentUuid,
                "mssql-profiled-stale-guarded-noop-post-as-update",
                referentialId,
                PostBodyJson,
                MssqlProfileRuntimeContextFactory.CreateGuardedNoOpPostAsUpdateContext(
                    _mappingSet,
                    PostBodyJson
                )
            )
        );
    }
}

// --------------------------------------------------------------------------
// Scenario 5: Profiled Merge With Hidden-Data Preservation
// --------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Profiled_Merge_With_Hidden_Data_Preservation
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000203")
    );

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin", "periods": [{ "periodName": "Fall" }] },
            { "city": "Houston" }
          ]
        }
        """;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private MssqlProfileRuntimePersistedState _stateAfterCreate = null!;
    private MssqlProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();
        _stateAfterCreate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await MssqlProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private async Task ExecuteInitialCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledMergeHiddenPreservation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-merge-hidden-preservation-create"
            )
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> ExecuteProfiledUpdateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledMergeHiddenPreservation",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            MssqlProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-merge-hidden-preservation-update",
                UpdateBodyJson,
                MssqlProfileRuntimeContextFactory.CreateMergeWithHiddenDataPreservationContext(
                    _mappingSet,
                    UpdateBodyJson
                )
            )
        );
    }

    [Test]
    public void It_returns_update_success()
    {
        var failureMessage = _updateResult is UpdateResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "profiled merge with hidden data preservation should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_has_three_address_rows_with_visible_first_then_hidden()
    {
        _stateAfterUpdate.Addresses.Should().HaveCount(3);
        _stateAfterUpdate.Addresses[0].City.Should().Be("Austin");
        _stateAfterUpdate.Addresses[0].Ordinal.Should().Be(0);
        _stateAfterUpdate.Addresses[1].City.Should().Be("Houston");
        _stateAfterUpdate.Addresses[1].Ordinal.Should().Be(1);
        _stateAfterUpdate.Addresses[2].City.Should().Be("Dallas");
        _stateAfterUpdate.Addresses[2].Ordinal.Should().Be(2);
    }

    [Test]
    public void It_preserves_hidden_extension_data()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension!.CampusCode.Should().Be("North");
    }

    [Test]
    public void It_preserves_hidden_extension_interventions()
    {
        _stateAfterUpdate.Interventions.Should().HaveCount(2);
        _stateAfterUpdate.Interventions[0].InterventionCode.Should().Be("INT-A");
        _stateAfterUpdate.Interventions[0].Ordinal.Should().Be(0);
        _stateAfterUpdate.Interventions[1].InterventionCode.Should().Be("INT-B");
        _stateAfterUpdate.Interventions[1].Ordinal.Should().Be(1);
    }

    [Test]
    public void It_preserves_hidden_extension_intervention_visits()
    {
        _stateAfterUpdate.InterventionVisits.Should().HaveCount(3);

        var intAVisits = _stateAfterUpdate
            .InterventionVisits.Where(v =>
                _stateAfterUpdate.Interventions.Any(i =>
                    i.InterventionCode == "INT-A" && i.CollectionItemId == v.ParentCollectionItemId
                )
            )
            .OrderBy(v => v.Ordinal)
            .ToArray();
        intAVisits.Should().HaveCount(2);
        intAVisits[0].VisitCode.Should().Be("V1");
        intAVisits[1].VisitCode.Should().Be("V2");

        var intBVisits = _stateAfterUpdate
            .InterventionVisits.Where(v =>
                _stateAfterUpdate.Interventions.Any(i =>
                    i.InterventionCode == "INT-B" && i.CollectionItemId == v.ParentCollectionItemId
                )
            )
            .ToArray();
        intBVisits.Should().HaveCount(1);
        intBVisits[0].VisitCode.Should().Be("V3");
    }

    [Test]
    public void It_preserves_hidden_extension_addresses()
    {
        _stateAfterUpdate.ExtensionAddresses.Should().HaveCount(2);

        // Central under Austin
        var austinExtAddr = _stateAfterUpdate
            .ExtensionAddresses.Where(ea =>
                _stateAfterUpdate.Addresses.Any(a =>
                    a.City == "Austin" && a.CollectionItemId == ea.BaseCollectionItemId
                )
            )
            .ToArray();
        austinExtAddr.Should().HaveCount(1);
        austinExtAddr[0].Zone.Should().Be("Central");

        // East under Dallas
        var dallasExtAddr = _stateAfterUpdate
            .ExtensionAddresses.Where(ea =>
                _stateAfterUpdate.Addresses.Any(a =>
                    a.City == "Dallas" && a.CollectionItemId == ea.BaseCollectionItemId
                )
            )
            .ToArray();
        dallasExtAddr.Should().HaveCount(1);
        dallasExtAddr[0].Zone.Should().Be("East");
    }

    [Test]
    public void It_increments_content_version()
    {
        _stateAfterUpdate
            .Document.ContentVersion.Should()
            .BeGreaterThan(_stateAfterCreate.Document.ContentVersion);
    }
}

// --------------------------------------------------------------------------
// Scenario 4: Root Create Rejection
// --------------------------------------------------------------------------

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_A_Mssql_Profiled_Root_Create_Rejection
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000204")
    );

    private const string CreateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin" }
          ]
        }
        """;

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private UpsertResult _createResult = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a configured connection string.");
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            MssqlProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = MssqlProfileRuntimeTestSupport.CreateServiceProvider();

        _createResult = await ExecuteProfiledCreateAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private async Task<UpsertResult> ExecuteProfiledCreateAsync()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        scope
            .ServiceProvider.GetRequiredService<IDmsInstanceSelection>()
            .SetSelectedDmsInstance(
                new DmsInstance(
                    Id: 1,
                    InstanceType: "test",
                    InstanceName: "MssqlProfiledRootCreateRejection",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            MssqlProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "mssql-profiled-root-create-rejection",
                CreateBodyJson,
                MssqlProfileRuntimeContextFactory.CreateRootCreateRejectionContext(
                    _mappingSet,
                    CreateBodyJson
                )
            )
        );
    }

    [Test]
    public void It_rejects_the_create()
    {
        _createResult.Should().BeOfType<UpsertResult.UpsertFailureNotAuthorized>();
    }

    [Test]
    public async Task It_does_not_persist_any_document()
    {
        var documentCount = await MssqlProfileRuntimeTestSupport.ReadDocumentCountAsync(_database);
        documentCount.Should().Be(0);
    }
}
