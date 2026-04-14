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
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class ProfileRuntimeAllowAllResourceAuthorizationHandler : IResourceAuthorizationHandler
{
    public Task<ResourceAuthorizationResult> Authorize(
        DocumentSecurityElements documentSecurityElements,
        OperationType operationType,
        TraceId traceId
    ) => Task.FromResult<ResourceAuthorizationResult>(new ResourceAuthorizationResult.Authorized());
}

file sealed class ProfileRuntimeNoOpUpdateCascadeHandler : IUpdateCascadeHandler
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

file sealed class ProfileRuntimeHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

internal sealed record ProfileRuntimeDocumentRow(
    long DocumentId,
    Guid DocumentUuid,
    short ResourceKeyId,
    long ContentVersion
);

internal sealed record ProfileRuntimeSchoolRow(long DocumentId, long SchoolId, string? ShortName);

internal sealed record ProfileRuntimeSchoolExtensionRow(long DocumentId, string? CampusCode);

internal sealed record ProfileRuntimeSchoolAddressRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? City
);

internal sealed record ProfileRuntimeSchoolAddressPeriodRow(
    long CollectionItemId,
    long ParentCollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? PeriodName
);

internal sealed record ProfileRuntimeSchoolExtensionAddressRow(
    long BaseCollectionItemId,
    long SchoolDocumentId,
    string? Zone
);

internal sealed record ProfileRuntimeSchoolExtensionInterventionRow(
    long CollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? InterventionCode
);

internal sealed record ProfileRuntimeSchoolExtensionInterventionVisitRow(
    long CollectionItemId,
    long ParentCollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    string? VisitCode
);

internal sealed record ProfileRuntimeSchoolExtensionAddressSponsorRefRow(
    long CollectionItemId,
    long BaseCollectionItemId,
    long SchoolDocumentId,
    int Ordinal,
    long? ProgramDocumentId,
    string? ProgramProgramName
);

internal sealed record ProfileRuntimePersistedState(
    ProfileRuntimeDocumentRow Document,
    ProfileRuntimeSchoolRow School,
    ProfileRuntimeSchoolExtensionRow? SchoolExtension,
    IReadOnlyList<ProfileRuntimeSchoolAddressRow> Addresses,
    IReadOnlyList<ProfileRuntimeSchoolAddressPeriodRow> AddressPeriods,
    IReadOnlyList<ProfileRuntimeSchoolExtensionAddressRow> ExtensionAddresses,
    IReadOnlyList<ProfileRuntimeSchoolExtensionInterventionRow> Interventions,
    IReadOnlyList<ProfileRuntimeSchoolExtensionInterventionVisitRow> InterventionVisits,
    IReadOnlyList<ProfileRuntimeSchoolExtensionAddressSponsorRefRow> SponsorReferences,
    long DocumentCount
);

file static class ProfileRuntimeTestSupport
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

    public static ServiceProvider CreateServiceProvider()
    {
        ServiceCollection services = [];

        services.AddSingleton<IHostApplicationLifetime, ProfileRuntimeHostApplicationLifetime>();
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
            UpdateCascadeHandler: new ProfileRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRuntimeAllowAllResourceAuthorizationHandler(),
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
            UpdateCascadeHandler: new ProfileRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRuntimeAllowAllResourceAuthorizationHandler(),
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
            UpdateCascadeHandler: new ProfileRuntimeNoOpUpdateCascadeHandler(),
            ResourceAuthorizationHandler: new ProfileRuntimeAllowAllResourceAuthorizationHandler(),
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

    public static async Task<ProfileRuntimePersistedState> ReadFullPersistedStateAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var document = await ReadDocumentAsync(database, documentUuid);
        var school = await ReadSchoolAsync(database, document.DocumentId);
        var schoolExtension = await ReadSchoolExtensionAsync(database, document.DocumentId);
        var addresses = await ReadSchoolAddressesAsync(database, document.DocumentId);
        var addressPeriods = await ReadSchoolAddressPeriodsAsync(database, document.DocumentId);
        var extensionAddresses = await ReadSchoolExtensionAddressesAsync(database, document.DocumentId);
        var interventions = await ReadSchoolExtensionInterventionsAsync(database, document.DocumentId);
        var interventionVisits = await ReadSchoolExtensionInterventionVisitsAsync(
            database,
            document.DocumentId
        );
        var sponsorReferences = await ReadSchoolExtensionAddressSponsorReferencesAsync(
            database,
            document.DocumentId
        );
        var documentCount = await ReadDocumentCountAsync(database);

        return new ProfileRuntimePersistedState(
            document,
            school,
            schoolExtension,
            addresses,
            addressPeriods,
            extensionAddresses,
            interventions,
            interventionVisits,
            sponsorReferences,
            documentCount
        );
    }

    public static async Task<long> ReadDocumentCountAsync(PostgresqlGeneratedDdlTestDatabase database)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS "Count"
            FROM "dms"."Document";
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

    public static long? GetNullableInt64(IReadOnlyDictionary<string, object?> row, string columnName) =>
        row.TryGetValue(columnName, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private static async Task<ProfileRuntimeDocumentRow> ReadDocumentAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        Guid documentUuid
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion"
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Count == 1
            ? new ProfileRuntimeDocumentRow(
                GetInt64(rows[0], "DocumentId"),
                GetGuid(rows[0], "DocumentUuid"),
                GetInt16(rows[0], "ResourceKeyId"),
                GetInt64(rows[0], "ContentVersion")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one document row for '{documentUuid}', but found {rows.Count}."
            );
    }

    private static async Task<ProfileRuntimeSchoolRow> ReadSchoolAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "SchoolId", "ShortName"
            FROM "edfi"."School"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count == 1
            ? new ProfileRuntimeSchoolRow(
                GetInt64(rows[0], "DocumentId"),
                GetInt64(rows[0], "SchoolId"),
                GetNullableString(rows[0], "ShortName")
            )
            : throw new InvalidOperationException(
                $"Expected exactly one school row for document id '{documentId}', but found {rows.Count}."
            );
    }

    private static async Task<ProfileRuntimeSchoolExtensionRow?> ReadSchoolExtensionAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "DocumentId", "CampusCode"
            FROM "sample"."SchoolExtension"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Count switch
        {
            0 => null,
            1 => new ProfileRuntimeSchoolExtensionRow(
                GetInt64(rows[0], "DocumentId"),
                GetNullableString(rows[0], "CampusCode")
            ),
            _ => throw new InvalidOperationException(
                $"Expected zero or one school extension row for document id '{documentId}', but found {rows.Count}."
            ),
        };
    }

    private static async Task<IReadOnlyList<ProfileRuntimeSchoolAddressRow>> ReadSchoolAddressesAsync(
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

        return rows.Select(row => new ProfileRuntimeSchoolAddressRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "City")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<ProfileRuntimeSchoolAddressPeriodRow>
    > ReadSchoolAddressPeriodsAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "ParentCollectionItemId", "School_DocumentId", "Ordinal", "PeriodName"
            FROM "edfi"."SchoolAddressPeriod"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new ProfileRuntimeSchoolAddressPeriodRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "PeriodName")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<ProfileRuntimeSchoolExtensionAddressRow>
    > ReadSchoolExtensionAddressesAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "BaseCollectionItemId", "School_DocumentId", "Zone"
            FROM "sample"."SchoolExtensionAddress"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "BaseCollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new ProfileRuntimeSchoolExtensionAddressRow(
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetNullableString(row, "Zone")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<ProfileRuntimeSchoolExtensionInterventionRow>
    > ReadSchoolExtensionInterventionsAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "School_DocumentId", "Ordinal", "InterventionCode"
            FROM "sample"."SchoolExtensionIntervention"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new ProfileRuntimeSchoolExtensionInterventionRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "InterventionCode")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<ProfileRuntimeSchoolExtensionInterventionVisitRow>
    > ReadSchoolExtensionInterventionVisitsAsync(PostgresqlGeneratedDdlTestDatabase database, long documentId)
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "ParentCollectionItemId", "School_DocumentId", "Ordinal", "VisitCode"
            FROM "sample"."SchoolExtensionInterventionVisit"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "ParentCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new ProfileRuntimeSchoolExtensionInterventionVisitRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "ParentCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableString(row, "VisitCode")
            ))
            .ToArray();
    }

    private static async Task<
        IReadOnlyList<ProfileRuntimeSchoolExtensionAddressSponsorRefRow>
    > ReadSchoolExtensionAddressSponsorReferencesAsync(
        PostgresqlGeneratedDdlTestDatabase database,
        long documentId
    )
    {
        var rows = await database.QueryRowsAsync(
            """
            SELECT "CollectionItemId", "BaseCollectionItemId", "School_DocumentId", "Ordinal",
                   "Program_DocumentId", "Program_ProgramName"
            FROM "sample"."SchoolExtensionAddressSponsorReference"
            WHERE "School_DocumentId" = @documentId
            ORDER BY "BaseCollectionItemId", "Ordinal", "CollectionItemId";
            """,
            new NpgsqlParameter("documentId", documentId)
        );

        return rows.Select(row => new ProfileRuntimeSchoolExtensionAddressSponsorRefRow(
                GetInt64(row, "CollectionItemId"),
                GetInt64(row, "BaseCollectionItemId"),
                GetInt64(row, "School_DocumentId"),
                GetInt32(row, "Ordinal"),
                GetNullableInt64(row, "Program_DocumentId"),
                GetNullableString(row, "Program_ProgramName")
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

file sealed class ProfileRuntimeFixedStoredStateProjectionInvoker(
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

file static class ProfileRuntimeContextFactory
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

    public static BackendProfileWriteContext CreateVisibleRowUpdateWithHiddenRowPreservationContext(
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
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
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

    public static BackendProfileWriteContext CreateVisibleRowDeleteWithHiddenRowPreservationContext(
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
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
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

    public static BackendProfileWriteContext CreateVisibleButAbsentNonCollectionScopeContext(
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
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisibleAbsent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisibleAbsent,
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

    private static VisibleRequestCollectionItem CreateVisibleAddressCollectionItem(
        ScopeInstanceAddress parentAddress,
        string city,
        int requestIndex,
        bool creatable
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$.addresses[*]",
                parentAddress,
                [new SemanticIdentityPart("city", JsonValue.Create(city)!, IsPresent: true)]
            ),
            Creatable: creatable,
            RequestJsonPath: $"$.addresses[{requestIndex}]"
        );

    private static VisibleRequestCollectionItem CreateVisibleInterventionCollectionItem(
        ScopeInstanceAddress parentAddress,
        string interventionCode,
        int requestIndex,
        bool creatable
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$._ext.sample.interventions[*]",
                parentAddress,
                [
                    new SemanticIdentityPart(
                        "interventionCode",
                        JsonValue.Create(interventionCode)!,
                        IsPresent: true
                    ),
                ]
            ),
            Creatable: creatable,
            RequestJsonPath: $"$._ext.sample.interventions[{requestIndex}]"
        );

    private static VisibleRequestCollectionItem CreateVisibleVisitCollectionItem(
        ScopeInstanceAddress parentAddress,
        string visitCode,
        int requestIndex,
        int parentInterventionIndex,
        bool creatable
    ) =>
        new(
            Address: new CollectionRowAddress(
                "$._ext.sample.interventions[*].visits[*]",
                parentAddress,
                [new SemanticIdentityPart("visitCode", JsonValue.Create(visitCode)!, IsPresent: true)]
            ),
            Creatable: creatable,
            RequestJsonPath: $"$._ext.sample.interventions[{parentInterventionIndex}].visits[{requestIndex}]"
        );

    public static BackendProfileWriteContext CreateHiddenExtensionRowPreservationContext(
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
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    Address: rootAddress,
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: ["campusCode"]
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.Hidden,
                        HiddenMemberPaths: []
                    ),
                ],
                visibleStoredCollectionRows: []
            )
        );
    }

    public static BackendProfileWriteContext CreateHiddenExtensionChildCollectionPreservationContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[SchoolResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var visibleRequestCollectionItems = ImmutableArray.Create(
            CreateVisibleAddressCollectionItem(rootAddress, "Austin", 0, creatable: true),
            CreateVisibleAddressCollectionItem(rootAddress, "Dallas", 1, creatable: true)
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
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
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
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
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

    public static BackendProfileWriteContext CreateNonCreatableAddressInsertRejectionContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[SchoolResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var visibleRequestCollectionItems = ImmutableArray.Create(
            CreateVisibleAddressCollectionItem(rootAddress, "Austin", 0, creatable: false),
            CreateVisibleAddressCollectionItem(rootAddress, "Houston", 1, creatable: false)
        );

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
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
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
                visibleStoredCollectionRows: [CreateVisibleStoredAddressCollectionRow(rootAddress, "Austin")]
            )
        );
    }

    public static BackendProfileWriteContext CreateNonCreatableInterventionInsertRejectionContext(
        MappingSet mappingSet,
        string requestBodyJson
    )
    {
        var requestBody = JsonNode.Parse(requestBodyJson)!;
        var writePlan = mappingSet.WritePlansByResource[SchoolResource];
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var rootAddress = new ScopeInstanceAddress("$", []);

        var extensionAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var interventionParentAddress = new ScopeInstanceAddress(
            "$._ext.sample.interventions[*]",
            [
                new AncestorCollectionInstance(
                    "$._ext.sample.interventions[*]",
                    [
                        new SemanticIdentityPart(
                            "interventionCode",
                            JsonValue.Create("NEW-INT")!,
                            IsPresent: true
                        ),
                    ]
                ),
            ]
        );

        var visibleRequestCollectionItems = ImmutableArray.Create(
            CreateVisibleInterventionCollectionItem(extensionAddress, "NEW-INT", 0, creatable: false),
            CreateVisibleVisitCollectionItem(interventionParentAddress, "NEW-V", 0, 0, creatable: true)
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
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                    Visibility: ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );

        return new BackendProfileWriteContext(
            Request: request,
            ProfileName: "runtime-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: []
                    ),
                    new StoredScopeState(
                        Address: new ScopeInstanceAddress("$._ext.sample.addresses[*]._ext.sample", []),
                        Visibility: ProfileVisibilityKind.VisibleAbsent,
                        HiddenMemberPaths: []
                    ),
                ],
                visibleStoredCollectionRows: []
            )
        );
    }

    public static BackendProfileWriteContext CreateHiddenInlinedColumnPreservationContext(
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
            StoredStateProjectionInvoker: new ProfileRuntimeFixedStoredStateProjectionInvoker(
                storedScopeStates:
                [
                    new StoredScopeState(
                        Address: rootAddress,
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        HiddenMemberPaths: ["shortName"]
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
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Visible_Row_Update_With_Hidden_Row_Preservation
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000101")
    );

    private const string UpdateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "addresses": [{ "city": "Austin", "periods": [{ "periodName": "Fall" }] }, { "city": "Houston" }] }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterCreate = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();
        _stateAfterCreate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
            _database,
            SchoolDocumentUuid.Value
        );

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledVisibleRowUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-visible-row-update-create"
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
                    InstanceName: "ProfiledVisibleRowUpdate",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-visible-row-update",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateVisibleRowUpdateWithHiddenRowPreservationContext(
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
            : "profiled visible-row update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_has_three_address_rows_with_visible_first_in_request_order_then_hidden()
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
    public void It_preserves_address_periods_for_visible_and_hidden_rows()
    {
        // Austin's "Fall" period preserved (matched update)
        var austinPeriods = _stateAfterUpdate
            .AddressPeriods.Where(p =>
                _stateAfterUpdate.Addresses.Any(a =>
                    a.City == "Austin" && a.CollectionItemId == p.ParentCollectionItemId
                )
            )
            .ToArray();
        austinPeriods.Should().HaveCount(1);
        austinPeriods[0].PeriodName.Should().Be("Fall");

        // Dallas's "Spring" period preserved (hidden row)
        var dallasPeriods = _stateAfterUpdate
            .AddressPeriods.Where(p =>
                _stateAfterUpdate.Addresses.Any(a =>
                    a.City == "Dallas" && a.CollectionItemId == p.ParentCollectionItemId
                )
            )
            .ToArray();
        dallasPeriods.Should().HaveCount(1);
        dallasPeriods[0].PeriodName.Should().Be("Spring");

        // Houston has no periods
        var houstonPeriods = _stateAfterUpdate
            .AddressPeriods.Where(p =>
                _stateAfterUpdate.Addresses.Any(a =>
                    a.City == "Houston" && a.CollectionItemId == p.ParentCollectionItemId
                )
            )
            .ToArray();
        houstonPeriods.Should().BeEmpty();
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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Visible_Row_Delete_With_Hidden_Row_Preservation
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000102")
    );

    private const string UpdateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "addresses": [] }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledVisibleRowDelete",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-visible-row-delete-create"
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
                    InstanceName: "ProfiledVisibleRowDelete",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-visible-row-delete",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateVisibleRowDeleteWithHiddenRowPreservationContext(
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
            : "profiled visible-row delete should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_keeps_only_the_hidden_dallas_address_row()
    {
        _stateAfterUpdate.Addresses.Should().HaveCount(1);
        _stateAfterUpdate.Addresses[0].City.Should().Be("Dallas");
        _stateAfterUpdate.Addresses[0].Ordinal.Should().Be(0);
    }

    [Test]
    public void It_keeps_only_the_hidden_dallas_address_period()
    {
        _stateAfterUpdate.AddressPeriods.Should().HaveCount(1);
        _stateAfterUpdate.AddressPeriods[0].PeriodName.Should().Be("Spring");
    }

    [Test]
    public void It_keeps_only_the_hidden_dallas_extension_address()
    {
        _stateAfterUpdate.ExtensionAddresses.Should().HaveCount(1);
        _stateAfterUpdate.ExtensionAddresses[0].Zone.Should().Be("East");
    }

    [Test]
    public void It_preserves_hidden_extension_and_interventions()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension!.CampusCode.Should().Be("North");
        _stateAfterUpdate.Interventions.Should().HaveCount(2);
        _stateAfterUpdate.InterventionVisits.Should().HaveCount(3);
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Visible_But_Absent_Non_Collection_Scope
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000103")
    );

    private const string UpdateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "addresses": [{ "city": "Austin", "periods": [{ "periodName": "Fall" }] }, { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }] }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledVisibleAbsentScope",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-visible-absent-create"
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
                    InstanceName: "ProfiledVisibleAbsentScope",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-visible-absent-update",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateVisibleButAbsentNonCollectionScopeContext(
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
            : "profiled visible-absent scope update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_deletes_the_visible_absent_extension_row()
    {
        _stateAfterUpdate.SchoolExtension.Should().BeNull();
    }

    [Test]
    public void It_cascades_deletion_of_extension_children()
    {
        _stateAfterUpdate.Interventions.Should().BeEmpty();
        _stateAfterUpdate.InterventionVisits.Should().BeEmpty();
        _stateAfterUpdate.ExtensionAddresses.Should().BeEmpty();
        _stateAfterUpdate.SponsorReferences.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_the_root_school_row()
    {
        _stateAfterUpdate.School.SchoolId.Should().Be(255901);
        _stateAfterUpdate.School.ShortName.Should().Be("LHS");
    }

    [Test]
    public void It_preserves_the_address_collection_rows()
    {
        _stateAfterUpdate.Addresses.Should().HaveCount(2);
        _stateAfterUpdate.Addresses[0].City.Should().Be("Austin");
        _stateAfterUpdate.Addresses[1].City.Should().Be("Dallas");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Hidden_Inlined_Column_Preservation
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000104")
    );

    private const string UpdateBodyJson = """
        { "schoolId": 255901, "addresses": [{ "city": "Austin", "periods": [{ "periodName": "Fall" }] }, { "city": "Dallas", "periods": [{ "periodName": "Spring" }] }] }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledHiddenInlinedColumn",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-hidden-inlined-create"
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
                    InstanceName: "ProfiledHiddenInlinedColumn",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-hidden-inlined-update",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateHiddenInlinedColumnPreservationContext(
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
            : "profiled hidden-inlined-column update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_preserves_the_hidden_short_name_column()
    {
        _stateAfterUpdate.School.ShortName.Should().Be("LHS");
    }

    [Test]
    public void It_preserves_the_hidden_extension_campus_code()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension!.CampusCode.Should().Be("North");
    }

    [Test]
    public void It_preserves_the_address_rows_unchanged()
    {
        _stateAfterUpdate.Addresses.Should().HaveCount(2);
        _stateAfterUpdate.Addresses[0].City.Should().Be("Austin");
        _stateAfterUpdate.Addresses[1].City.Should().Be("Dallas");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Hidden_Extension_Row_Preservation
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000105")
    );

    private const string UpdateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "_ext": { "sample": {} } }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledHiddenExtensionRow",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-hidden-ext-row-create"
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
                    InstanceName: "ProfiledHiddenExtensionRow",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-hidden-ext-row-update",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateHiddenExtensionRowPreservationContext(
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
            : "profiled hidden-extension-row update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_preserves_the_hidden_campus_code()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension!.CampusCode.Should().Be("North");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Hidden_Extension_Child_Collection_Preservation
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000106")
    );

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "addresses": [
            { "city": "Austin" },
            { "city": "Dallas" }
          ],
          "_ext": {
            "sample": {
              "campusCode": "South"
            }
          }
        }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledHiddenExtChildColl",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-hidden-ext-child-coll-create"
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
                    InstanceName: "ProfiledHiddenExtChildColl",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-hidden-ext-child-coll-update",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateHiddenExtensionChildCollectionPreservationContext(
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
            : "profiled hidden-extension-child-collection update should succeed";

        _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>(failureMessage);
        _updateResult.As<UpdateResult.UpdateSuccess>().ExistingDocumentUuid.Should().Be(SchoolDocumentUuid);
    }

    [Test]
    public void It_updates_the_extension_campus_code()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension!.CampusCode.Should().Be("South");
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
    public void It_preserves_the_address_collection_rows()
    {
        _stateAfterUpdate.Addresses.Should().HaveCount(2);
        _stateAfterUpdate.Addresses[0].City.Should().Be("Austin");
        _stateAfterUpdate.Addresses[1].City.Should().Be("Dallas");
    }

    [Test]
    public void It_preserves_the_hidden_extension_address_rows()
    {
        _stateAfterUpdate.ExtensionAddresses.Should().HaveCount(2);

        var austinExtAddr = _stateAfterUpdate
            .ExtensionAddresses.Where(ea =>
                _stateAfterUpdate.Addresses.Any(a =>
                    a.City == "Austin" && a.CollectionItemId == ea.BaseCollectionItemId
                )
            )
            .ToArray();
        austinExtAddr.Should().HaveCount(1);
        austinExtAddr[0].Zone.Should().Be("Central");

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
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Non_Creatable_Address_Insert_Rejection
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000107")
    );

    private const string InitialCreateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "addresses": [{ "city": "Austin" }] }
        """;

    private const string PostAsUpdateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "addresses": [{ "city": "Austin" }, { "city": "Houston" }] }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterPostAsUpdate = null!;
    private UpsertResult _postAsUpdateResult = null!;
    private ReferentialId _referentialId;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _referentialId = ProfileRuntimeTestSupport.CreateSchoolDocumentInfo().ReferentialId;

        _postAsUpdateResult = await ExecuteProfiledPostAsUpdateAsync();
        _stateAfterPostAsUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledNonCreatableAddr",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-non-creatable-addr-create",
                requestBodyJsonOverride: InitialCreateBodyJson
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
                    InstanceName: "ProfiledNonCreatableAddr",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreatePostAsUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-non-creatable-addr-post-update",
                _referentialId,
                PostAsUpdateBodyJson,
                ProfileRuntimeContextFactory.CreateNonCreatableAddressInsertRejectionContext(
                    _mappingSet,
                    PostAsUpdateBodyJson
                )
            )
        );
    }

    [Test]
    public void It_rejects_the_non_creatable_address_insert()
    {
        _postAsUpdateResult.Should().BeOfType<UpsertResult.UpsertFailureValidation>();
    }

    [Test]
    public void It_has_exactly_one_document()
    {
        _stateAfterPostAsUpdate.DocumentCount.Should().Be(1);
    }

    [Test]
    public void It_preserves_original_state()
    {
        _stateAfterPostAsUpdate.Addresses.Should().HaveCount(1);
        _stateAfterPostAsUpdate.Addresses[0].City.Should().Be("Austin");
        _stateAfterPostAsUpdate.School.ShortName.Should().Be("LHS");
    }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_A_Postgresql_Profiled_Non_Creatable_Intervention_Insert_Rejection
{
    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("dddddddd-0000-0000-0000-000000000108")
    );

    private const string InitialCreateBodyJson = """
        { "schoolId": 255901, "shortName": "LHS", "_ext": { "sample": { "campusCode": "North" } } }
        """;

    private const string UpdateBodyJson = """
        {
          "schoolId": 255901,
          "shortName": "LHS",
          "_ext": {
            "sample": {
              "campusCode": "North",
              "interventions": [
                { "interventionCode": "NEW-INT", "visits": [{ "visitCode": "NEW-V" }] }
              ]
            }
          }
        }
        """;

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private MappingSet _mappingSet = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;
    private ProfileRuntimePersistedState _stateAfterUpdate = null!;
    private UpdateResult _updateResult = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            ProfileRuntimeTestSupport.FixtureRelativePath
        );
        _mappingSet = new MappingSetCompiler().Compile(_fixture.ModelSet);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _serviceProvider = ProfileRuntimeTestSupport.CreateServiceProvider();

        await ExecuteInitialCreateAsync();

        _updateResult = await ExecuteProfiledUpdateAsync();
        _stateAfterUpdate = await ProfileRuntimeTestSupport.ReadFullPersistedStateAsync(
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
                    InstanceName: "ProfiledNonCreatableIntervention",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        var result = await repository.UpsertDocument(
            ProfileRuntimeTestSupport.CreateCreateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-non-creatable-intervention-create",
                requestBodyJsonOverride: InitialCreateBodyJson
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
                    InstanceName: "ProfiledNonCreatableIntervention",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();

        return await repository.UpdateDocumentById(
            ProfileRuntimeTestSupport.CreateUpdateRequest(
                _mappingSet,
                SchoolDocumentUuid,
                "pg-profiled-runtime-non-creatable-intervention-update",
                UpdateBodyJson,
                ProfileRuntimeContextFactory.CreateNonCreatableInterventionInsertRejectionContext(
                    _mappingSet,
                    UpdateBodyJson
                )
            )
        );
    }

    [Test]
    public void It_rejects_the_non_creatable_intervention_insert()
    {
        var failureMessage = _updateResult is UpdateResult.UnknownFailure unknownFailure
            ? unknownFailure.FailureMessage
            : "profiled non-creatable intervention insert should be rejected";

        _updateResult.Should().BeOfType<UpdateResult.UpdateFailureValidation>(failureMessage);
    }

    [Test]
    public void It_creates_no_intervention_rows()
    {
        _stateAfterUpdate.Interventions.Should().BeEmpty();
        _stateAfterUpdate.InterventionVisits.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_the_extension_data()
    {
        _stateAfterUpdate.SchoolExtension.Should().NotBeNull();
        _stateAfterUpdate.SchoolExtension!.CampusCode.Should().Be("North");
    }
}
