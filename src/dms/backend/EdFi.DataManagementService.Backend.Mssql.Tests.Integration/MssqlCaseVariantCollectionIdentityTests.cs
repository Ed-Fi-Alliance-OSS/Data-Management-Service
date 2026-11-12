// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server stores <c>School.addresses[*].city</c> under the case-insensitive identity collation, so a
/// PUT whose collection item differs from the stored one only in the case of <c>city</c> matches the
/// stored row on both merge paths: the row keeps its <c>CollectionItemId</c> and the stored spelling,
/// nested and aligned-extension children under it are merged rather than recreated, a profile's hidden
/// members are preserved, and an otherwise identical PUT is a guarded no-op.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
public class Given_A_Mssql_Put_With_A_Case_Variant_Collection_Semantic_Identity
{
    private const long SchoolId = 255911;
    private const string AddressesScope = "$.addresses[*]";
    private const string PeriodsScope = "$.addresses[*].periods[*]";

    private static readonly DocumentUuid SchoolDocumentUuid = new(
        Guid.Parse("eeee1452-0000-0000-0000-000000000001")
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
    public async Task It_keeps_the_stored_item_as_a_guarded_no_op_for_a_casing_only_put()
    {
        await SeedAsync(SchoolBody(Address("Austin", periods: ["First"])));
        var documentId = await ReadDocumentIdAsync();
        var before = await ReadStateAsync(documentId);

        var result = await PutAsync(SchoolBody(Address("AUSTIN", periods: ["First"])));

        result.Should().BeOfType<UpdateResult.UpdateSuccess>(result.ToString());
        var after = await ReadStateAsync(documentId);
        after.Should().BeEquivalentTo(before);
        after.Addresses.Single().City.Should().Be("Austin");
    }

    [Test]
    public async Task It_merges_nested_children_under_a_recased_parent()
    {
        await SeedAsync(SchoolBody(Address("Austin", periods: ["First"])));
        var documentId = await ReadDocumentIdAsync();
        var before = await ReadStateAsync(documentId);

        var result = await PutAsync(SchoolBody(Address("AUSTIN", periods: ["First", "Second"])));

        result.Should().BeOfType<UpdateResult.UpdateSuccess>(result.ToString());
        await AssertNestedChildrenMergedAsync(documentId, before);
    }

    [Test]
    public async Task It_updates_the_aligned_extension_under_a_recased_parent()
    {
        await SeedAsync(SchoolBody([Address("Austin")], zones: ["North"]));
        var documentId = await ReadDocumentIdAsync();
        var before = await ReadStateAsync(documentId);

        var result = await PutAsync(SchoolBody([Address("AUSTIN")], zones: ["South"]));

        result.Should().BeOfType<UpdateResult.UpdateSuccess>(result.ToString());
        await AssertAlignedExtensionUpdatedAsync(documentId, before);
    }

    [Test]
    public async Task It_preserves_a_hidden_member_of_a_recased_profiled_item()
    {
        var descriptorId = await MssqlProfileTopLevelCollectionMergeSupport.SeedAddressTypeDescriptorAsync(
            _database,
            Guid.Parse("eeee1452-0000-0000-0000-0000000000d1"),
            "Physical"
        );
        await SeedAsync(
            SchoolBody(
                Address(
                    "Austin",
                    addressTypeDescriptor: MssqlProfileTopLevelCollectionMergeSupport.PhysicalAddressTypeDescriptorUri
                )
            )
        );
        var documentId = await ReadDocumentIdAsync();
        var before = await ReadStateAsync(documentId);

        var writeBody = SchoolBody(Address("AUSTIN"));
        var result = await PutAsync(
            writeBody,
            CreateProfileContext(
                writeBody,
                requestItems: [AddressItem("AUSTIN", 0)],
                storedRows:
                [
                    new VisibleStoredCollectionRow(
                        AddressRowAddress("Austin"),
                        MssqlProfileTopLevelCollectionMergeSupport.HiddenAddressTypeDescriptorPath()
                    ),
                ]
            )
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>(result.ToString());
        var after = await ReadStateAsync(documentId);
        after.Should().BeEquivalentTo(before);
        after.Addresses.Single().City.Should().Be("Austin");
        after.Addresses.Single().AddressTypeDescriptorId.Should().Be(descriptorId);
    }

    [Test]
    public async Task It_merges_nested_children_under_a_recased_profiled_parent()
    {
        await SeedAsync(SchoolBody(Address("Austin", periods: ["First"])));
        var documentId = await ReadDocumentIdAsync();
        var before = await ReadStateAsync(documentId);

        var writeBody = SchoolBody(Address("AUSTIN", periods: ["First", "Second"]));
        var result = await PutAsync(
            writeBody,
            CreateProfileContext(
                writeBody,
                requestItems:
                [
                    AddressItem("AUSTIN", 0),
                    PeriodItem("AUSTIN", "First", 0, 0),
                    PeriodItem("AUSTIN", "Second", 0, 1),
                ],
                storedRows:
                [
                    new VisibleStoredCollectionRow(AddressRowAddress("Austin"), []),
                    new VisibleStoredCollectionRow(PeriodRowAddress("Austin", "First"), []),
                ]
            )
        );

        result.Should().BeOfType<UpdateResult.UpdateSuccess>(result.ToString());
        await AssertNestedChildrenMergedAsync(documentId, before);
    }

    private async Task AssertNestedChildrenMergedAsync(long documentId, CollectionState before)
    {
        var after = await ReadStateAsync(documentId);
        var address = after.Addresses.Single();
        address.CollectionItemId.Should().Be(before.Addresses.Single().CollectionItemId);
        address.City.Should().Be("Austin");
        after.Periods.Select(period => period.PeriodName).Should().Equal("First", "Second");
        after
            .Periods.Single(period => period.PeriodName == "First")
            .CollectionItemId.Should()
            .Be(before.Periods.Single().CollectionItemId);
        after.ContentVersion.Should().BeGreaterThan(before.ContentVersion);
    }

    private async Task AssertAlignedExtensionUpdatedAsync(long documentId, CollectionState before)
    {
        var after = await ReadStateAsync(documentId);
        var address = after.Addresses.Single();
        address.CollectionItemId.Should().Be(before.Addresses.Single().CollectionItemId);
        address.City.Should().Be("Austin");
        after.AlignedExtensions.Should().Equal(new AlignedExtensionRow(address.CollectionItemId, "South"));
    }

    private static JsonObject SchoolBody(params JsonObject[] addresses) => SchoolBody(addresses, zones: null);

    // Aligned extension values live under the root extension, positionally aligned with $.addresses.
    private static JsonObject SchoolBody(JsonObject[] addresses, string[]? zones)
    {
        var body = new JsonObject
        {
            ["schoolId"] = checked((int)SchoolId),
            ["addresses"] = new JsonArray([.. addresses]),
        };

        if (zones is not null)
        {
            body["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["addresses"] = new JsonArray([
                        .. zones.Select(zone =>
                            (JsonNode)
                                new JsonObject
                                {
                                    ["_ext"] = new JsonObject
                                    {
                                        ["sample"] = new JsonObject { ["zone"] = zone },
                                    },
                                }
                        ),
                    ]),
                },
            };
        }

        return body;
    }

    private static JsonObject Address(
        string city,
        string[]? periods = null,
        string? addressTypeDescriptor = null
    )
    {
        var address = new JsonObject { ["city"] = city };

        if (addressTypeDescriptor is not null)
        {
            address["addressTypeDescriptor"] = addressTypeDescriptor;
        }

        if (periods is not null)
        {
            address["periods"] = new JsonArray([
                .. periods.Select(periodName => (JsonNode)new JsonObject { ["periodName"] = periodName }),
            ]);
        }

        return address;
    }

    private static ImmutableArray<SemanticIdentityPart> CityIdentity(string city) =>
        [new SemanticIdentityPart("city", JsonValue.Create(city), IsPresent: true)];

    private static ScopeInstanceAddress AddressContainingScopeAddress(string city) =>
        new(AddressesScope, [new AncestorCollectionInstance(AddressesScope, CityIdentity(city))]);

    private static CollectionRowAddress AddressRowAddress(string city) =>
        new(AddressesScope, new ScopeInstanceAddress("$", []), CityIdentity(city));

    private static CollectionRowAddress PeriodRowAddress(string city, string periodName) =>
        new(
            PeriodsScope,
            AddressContainingScopeAddress(city),
            [new SemanticIdentityPart("periodName", JsonValue.Create(periodName), IsPresent: true)]
        );

    private static VisibleRequestCollectionItem AddressItem(string city, int addressIndex) =>
        new(AddressRowAddress(city), Creatable: true, $"$.addresses[{addressIndex}]");

    private static VisibleRequestCollectionItem PeriodItem(
        string city,
        string periodName,
        int addressIndex,
        int periodIndex
    ) =>
        new(
            PeriodRowAddress(city, periodName),
            Creatable: true,
            $"$.addresses[{addressIndex}].periods[{periodIndex}]"
        );

    private BackendProfileWriteContext CreateProfileContext(
        JsonNode requestBody,
        IReadOnlyList<VisibleRequestCollectionItem> requestItems,
        IReadOnlyList<VisibleStoredCollectionRow> storedRows
    )
    {
        var writePlan = _mappingSet.WritePlansByResource[
            MssqlProfileTopLevelCollectionMergeSupport.SchoolResource
        ];
        ImmutableArray<RequestScopeState> requestScopeStates =
        [
            new RequestScopeState(
                new ScopeInstanceAddress("$", []),
                ProfileVisibilityKind.VisiblePresent,
                true
            ),
        ];
        ImmutableArray<StoredScopeState> storedScopeStates =
        [
            new StoredScopeState(new ScopeInstanceAddress("$", []), ProfileVisibilityKind.VisiblePresent, []),
        ];

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: true,
                RequestScopeStates: requestScopeStates,
                VisibleRequestCollectionItems: [.. requestItems]
            ),
            ProfileName: "case-variant-collection-profile",
            CompiledScopeCatalog: CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan),
            StoredStateProjectionInvoker: new StoredStateProjection(storedScopeStates, [.. storedRows])
        );
    }

    private async Task SeedAsync(JsonNode body)
    {
        var result = await MssqlProfileTopLevelCollectionMergeSupport.SeedAsync(
            _serviceProvider,
            _database,
            _mappingSet,
            SchoolId,
            body,
            SchoolDocumentUuid,
            "mssql-case-variant-collection-seed"
        );

        result.Should().BeOfType<UpsertResult.InsertSuccess>();
    }

    private async Task<UpdateResult> PutAsync(
        JsonNode body,
        BackendProfileWriteContext? profileContext = null
    )
    {
        using var scope = _serviceProvider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<IDataStoreSelection>()
            .SetSelectedDataStore(
                new DataStore(
                    Id: 1,
                    DataStoreType: "test",
                    Name: "MssqlCaseVariantCollectionIdentity",
                    ConnectionString: _database.ConnectionString,
                    RouteContext: []
                )
            );

        var updateRequest = new UpdateRequest(
            ResourceInfo: MssqlProfileTopLevelCollectionMergeSupport.SchoolResourceInfo,
            DocumentInfo: MssqlProfileTopLevelCollectionMergeSupport.CreateDocumentInfo(SchoolId, body),
            MappingSet: _mappingSet,
            EdfiDoc: body,
            Headers: [],
            TraceId: new TraceId("mssql-case-variant-collection-put"),
            DocumentUuid: SchoolDocumentUuid,
            BackendProfileWriteContext: profileContext
        );

        var repository = scope.ServiceProvider.GetRequiredService<RelationalDocumentStoreRepository>();
        return await repository.UpdateDocumentById(updateRequest);
    }

    private Task<long> ReadDocumentIdAsync() =>
        MssqlProfileTopLevelCollectionMergeSupport.ReadDocumentIdAsync(_database, SchoolDocumentUuid);

    // Rows are read by owning document and CollectionItemId, never by WHERE City = @city, which the
    // identity collation would answer case-insensitively.
    private async Task<CollectionState> ReadStateAsync(long documentId)
    {
        var addresses = await _database.QueryRowsAsync(
            """
            SELECT [CollectionItemId], [City], [AddressTypeDescriptor_DescriptorId]
            FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [Ordinal];
            """,
            new SqlParameter("@documentId", documentId)
        );
        var periods = await _database.QueryRowsAsync(
            """
            SELECT p.[CollectionItemId], p.[PeriodName]
            FROM [edfi].[SchoolAddressPeriod] p
            INNER JOIN [edfi].[SchoolAddress] a ON a.[CollectionItemId] = p.[ParentCollectionItemId]
            WHERE a.[School_DocumentId] = @documentId
            ORDER BY p.[Ordinal];
            """,
            new SqlParameter("@documentId", documentId)
        );
        var alignedExtensions = await _database.QueryRowsAsync(
            """
            SELECT [BaseCollectionItemId], [Zone]
            FROM [sample].[SchoolExtensionAddress]
            WHERE [School_DocumentId] = @documentId
            ORDER BY [BaseCollectionItemId];
            """,
            new SqlParameter("@documentId", documentId)
        );
        var contentVersion = await _database.ExecuteScalarAsync<long>(
            """
            SELECT [ContentVersion]
            FROM [dms].[Document]
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );

        return new CollectionState(
            [
                .. addresses.Select(row => new AddressRow(
                    Convert.ToInt64(row["CollectionItemId"], CultureInfo.InvariantCulture),
                    (string)row["City"]!,
                    row["AddressTypeDescriptor_DescriptorId"] is null or DBNull
                        ? null
                        : Convert.ToInt64(
                            row["AddressTypeDescriptor_DescriptorId"],
                            CultureInfo.InvariantCulture
                        )
                )),
            ],
            [
                .. periods.Select(row => new PeriodRow(
                    Convert.ToInt64(row["CollectionItemId"], CultureInfo.InvariantCulture),
                    (string)row["PeriodName"]!
                )),
            ],
            [
                .. alignedExtensions.Select(row => new AlignedExtensionRow(
                    Convert.ToInt64(row["BaseCollectionItemId"], CultureInfo.InvariantCulture),
                    row["Zone"] as string
                )),
            ],
            contentVersion
        );
    }

    private sealed record AddressRow(long CollectionItemId, string City, long? AddressTypeDescriptorId);

    private sealed record PeriodRow(long CollectionItemId, string PeriodName);

    private sealed record AlignedExtensionRow(long BaseCollectionItemId, string? Zone);

    private sealed record CollectionState(
        IReadOnlyList<AddressRow> Addresses,
        IReadOnlyList<PeriodRow> Periods,
        IReadOnlyList<AlignedExtensionRow> AlignedExtensions,
        long ContentVersion
    );

    private sealed class StoredStateProjection(
        ImmutableArray<StoredScopeState> storedScopeStates,
        ImmutableArray<VisibleStoredCollectionRow> visibleStoredRows
    ) : IStoredStateProjectionInvoker
    {
        public ProfileAppliedWriteContext ProjectStoredState(
            JsonNode storedDocument,
            ProfileAppliedWriteRequest request,
            IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
        ) => new(request, storedDocument, storedScopeStates, visibleStoredRows);
    }
}
