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
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-agnostic scenario data and assertion helpers shared by the MSSQL and
/// PostgreSQL nested-collection profile-merge integration suites. Each provider suite
/// keeps its own fixture creation, resolver registration, database type, SQL dialect,
/// readback, and parameter wiring, but consumes the input / stored-row / request-item
/// records, body builders, and address builders defined here so the two suites share a
/// single source of truth for the tested scenarios.
/// </summary>
public static class ProfileNestedCollectionScenarios
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-nested-and-root-extension-children";

    public const string ParentScope = "$.parents[*]";
    public const string ChildScope = "$.parents[*].children[*]";
    public const string RootExtScope = "$._ext.root_ext";
    public const string RootExtChildScope = "$._ext.root_ext.root_ext_children[*]";

    public static readonly QualifiedResourceName ParentResource = new("Ed-Fi", "ParentResource");

    public static readonly ResourceInfo ParentResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ParentResource"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false
    );

    public static JsonNode CreateParentResourceBody(
        int parentResourceId,
        IReadOnlyList<ParentInput>? parents = null,
        RootExtInput? rootExt = null
    )
    {
        var body = new JsonObject { ["parentResourceId"] = parentResourceId };

        if (parents is not null)
        {
            JsonArray parentNodes = [];
            foreach (var parent in parents)
            {
                JsonObject parentNode = new()
                {
                    ["parentCode"] = parent.ParentCode,
                    ["parentName"] = parent.ParentName,
                };

                if (parent.Children is not null)
                {
                    JsonArray childNodes = [];
                    foreach (var child in parent.Children)
                    {
                        childNodes.Add(
                            new JsonObject
                            {
                                ["childCode"] = child.ChildCode,
                                ["childValue"] = child.ChildValue,
                            }
                        );
                    }
                    parentNode["children"] = childNodes;
                }

                parentNodes.Add(parentNode);
            }
            body["parents"] = parentNodes;
        }

        if (rootExt is not null)
        {
            JsonObject rootExtNode = new()
            {
                ["rootExtVisibleScalar"] = rootExt.RootExtVisibleScalar,
                ["rootExtHiddenScalar"] = rootExt.RootExtHiddenScalar,
            };

            if (rootExt.Children is not null)
            {
                JsonArray rootExtChildNodes = [];
                foreach (var child in rootExt.Children)
                {
                    rootExtChildNodes.Add(
                        new JsonObject
                        {
                            ["rootExtChildCode"] = child.RootExtChildCode,
                            ["rootExtChildValue"] = child.RootExtChildValue,
                        }
                    );
                }
                rootExtNode["root_ext_children"] = rootExtChildNodes;
            }

            body["_ext"] = new JsonObject { ["root_ext"] = rootExtNode };
        }

        return body;
    }

    public static DocumentInfo CreateDocumentInfo(int parentResourceId)
    {
        var identity = new DocumentIdentity([
            new DocumentIdentityElement(
                new JsonPath("$.parentResourceId"),
                parentResourceId.ToString(CultureInfo.InvariantCulture)
            ),
        ]);

        return new DocumentInfo(
            DocumentIdentity: identity,
            ReferentialId: ReferentialIdCalculator.ReferentialIdFrom(ParentResourceInfo, identity),
            DocumentReferences: [],
            DocumentReferenceArrays: [],
            DescriptorReferences: [],
            SuperclassIdentity: null
        );
    }

    public static ImmutableArray<SemanticIdentityPart> ParentIdentity(string parentCode) =>
        [new SemanticIdentityPart("parentCode", JsonValue.Create(parentCode), IsPresent: true)];

    public static ImmutableArray<SemanticIdentityPart> ChildIdentity(string childCode) =>
        [new SemanticIdentityPart("childCode", JsonValue.Create(childCode), IsPresent: true)];

    public static ImmutableArray<SemanticIdentityPart> RootExtChildIdentity(string rootExtChildCode) =>
        [new SemanticIdentityPart("rootExtChildCode", JsonValue.Create(rootExtChildCode), IsPresent: true)];

    public static ScopeInstanceAddress ParentContainingScopeAddress(string parentCode) =>
        new(ParentScope, [new AncestorCollectionInstance(ParentScope, ParentIdentity(parentCode))]);

    public static CollectionRowAddress ParentRowAddress(string parentCode) =>
        new(ParentScope, new ScopeInstanceAddress("$", []), ParentIdentity(parentCode));

    public static CollectionRowAddress ChildRowAddress(string parentCode, string childCode) =>
        new(ChildScope, ParentContainingScopeAddress(parentCode), ChildIdentity(childCode));

    public static CollectionRowAddress RootExtChildRowAddress(string rootExtChildCode) =>
        new(
            RootExtChildScope,
            new ScopeInstanceAddress(RootExtScope, []),
            RootExtChildIdentity(rootExtChildCode)
        );

    /// <summary>
    /// Builds a profile-applied write request for the nested-collection scenario.
    /// The provider-specific projection invoker is supplied by each suite so this helper
    /// stays free of provider dependencies.
    /// </summary>
    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<RequestParentItem> requestParentItems,
        IStoredStateProjectionInvoker storedStateProjectionInvoker,
        IReadOnlyList<RequestChildItem>? requestChildItems = null,
        IReadOnlyList<RequestRootExtChildItem>? requestRootExtChildItems = null,
        RequestRootExtScope? requestRootExtScope = null,
        bool rootCreatable = true,
        string profileName = "nested-and-root-ext-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        var visibleRequestItems = ImmutableArray.CreateBuilder<VisibleRequestCollectionItem>();
        foreach (var item in requestParentItems)
        {
            visibleRequestItems.Add(
                new VisibleRequestCollectionItem(
                    ParentRowAddress(item.ParentCode),
                    item.Creatable,
                    $"$.parents[{item.ArrayIndex}]"
                )
            );
        }
        if (requestChildItems is not null)
        {
            foreach (var item in requestChildItems)
            {
                visibleRequestItems.Add(
                    new VisibleRequestCollectionItem(
                        ChildRowAddress(item.ParentCode, item.ChildCode),
                        item.Creatable,
                        $"$.parents[{item.ParentArrayIndex}].children[{item.ChildArrayIndex}]"
                    )
                );
            }
        }
        if (requestRootExtChildItems is not null)
        {
            foreach (var item in requestRootExtChildItems)
            {
                visibleRequestItems.Add(
                    new VisibleRequestCollectionItem(
                        RootExtChildRowAddress(item.RootExtChildCode),
                        item.Creatable,
                        $"$._ext.root_ext.root_ext_children[{item.ArrayIndex}]"
                    )
                );
            }
        }

        var requestScopeStates = ImmutableArray.CreateBuilder<RequestScopeState>();
        requestScopeStates.Add(
            new RequestScopeState(
                new ScopeInstanceAddress("$", []),
                ProfileVisibilityKind.VisiblePresent,
                rootCreatable
            )
        );
        if (requestRootExtScope is not null)
        {
            requestScopeStates.Add(
                new RequestScopeState(
                    new ScopeInstanceAddress(RootExtScope, []),
                    requestRootExtScope.Visibility,
                    requestRootExtScope.Creatable
                )
            );
        }

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates: requestScopeStates.ToImmutable(),
                VisibleRequestCollectionItems: visibleRequestItems.ToImmutable()
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: storedStateProjectionInvoker
        );
    }

    public sealed record ChildInput(string ChildCode, string ChildValue);

    public sealed record ParentInput(
        string ParentCode,
        string ParentName,
        IReadOnlyList<ChildInput>? Children = null
    );

    public sealed record RootExtChildInput(string RootExtChildCode, string RootExtChildValue);

    public sealed record RootExtInput(
        string RootExtVisibleScalar,
        string RootExtHiddenScalar,
        IReadOnlyList<RootExtChildInput>? Children = null
    );

    public sealed record RequestParentItem(string ParentCode, int ArrayIndex, bool Creatable = true);

    public sealed record RequestChildItem(
        string ParentCode,
        string ChildCode,
        int ParentArrayIndex,
        int ChildArrayIndex,
        bool Creatable = true
    );

    public sealed record RequestRootExtChildItem(
        string RootExtChildCode,
        int ArrayIndex,
        bool Creatable = true
    );

    public sealed record RequestRootExtScope(ProfileVisibilityKind Visibility, bool Creatable);

    public sealed record StoredParentRow(string ParentCode, ImmutableArray<string> HiddenMemberPaths);

    public sealed record StoredChildRow(
        string ParentCode,
        string ChildCode,
        ImmutableArray<string> HiddenMemberPaths
    );

    public sealed record StoredRootExtChildRow(
        string RootExtChildCode,
        ImmutableArray<string> HiddenMemberPaths
    );

    public sealed record StoredRootExtScope(
        ProfileVisibilityKind Visibility,
        ImmutableArray<string> HiddenMemberPaths
    );

    public sealed record ParentRow(
        long CollectionItemId,
        long ParentResourceDocumentId,
        int Ordinal,
        string? ParentCode,
        string? ParentName
    );

    public sealed record ChildRow(
        long CollectionItemId,
        long ParentCollectionItemId,
        long ParentResourceDocumentId,
        int Ordinal,
        string? ChildCode,
        string? ChildValue
    );

    public sealed record RootExtRow(
        long DocumentId,
        string? RootExtVisibleScalar,
        string? RootExtHiddenScalar
    );

    public sealed record RootExtChildRow(
        long CollectionItemId,
        long ParentResourceDocumentId,
        int Ordinal,
        string? RootExtChildCode,
        string? RootExtChildValue
    );

    /// <summary>
    /// Provider-neutral stored-state projection invoker used by both the MSSQL and
    /// PostgreSQL nested-collection profile-merge integration suites. The body is
    /// identical between providers; centralizing it here removes byte-for-byte
    /// duplication while leaving each provider in control of its own
    /// <c>CreateProfileContext</c> wrapper signature.
    /// </summary>
    public sealed class StoredStateProjectionInvoker(
        ImmutableArray<StoredParentRow> storedParentRows,
        ImmutableArray<StoredChildRow> storedChildRows,
        ImmutableArray<StoredRootExtChildRow> storedRootExtChildRows,
        StoredRootExtScope? storedRootExtScope
    ) : IStoredStateProjectionInvoker
    {
        public ProfileAppliedWriteContext ProjectStoredState(
            JsonNode storedDocument,
            ProfileAppliedWriteRequest request,
            IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
        )
        {
            var storedScopeStates = ImmutableArray.CreateBuilder<StoredScopeState>();
            storedScopeStates.Add(
                new StoredScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    []
                )
            );

            if (storedRootExtScope is not null)
            {
                storedScopeStates.Add(
                    new StoredScopeState(
                        new ScopeInstanceAddress(RootExtScope, []),
                        storedRootExtScope.Visibility,
                        storedRootExtScope.HiddenMemberPaths
                    )
                );
            }

            var visibleStoredRows = ImmutableArray.CreateBuilder<VisibleStoredCollectionRow>();
            foreach (var parentRow in storedParentRows)
            {
                visibleStoredRows.Add(
                    new VisibleStoredCollectionRow(
                        ParentRowAddress(parentRow.ParentCode),
                        parentRow.HiddenMemberPaths
                    )
                );
            }

            foreach (var childRow in storedChildRows)
            {
                visibleStoredRows.Add(
                    new VisibleStoredCollectionRow(
                        ChildRowAddress(childRow.ParentCode, childRow.ChildCode),
                        childRow.HiddenMemberPaths
                    )
                );
            }

            foreach (var rootExtChildRow in storedRootExtChildRows)
            {
                visibleStoredRows.Add(
                    new VisibleStoredCollectionRow(
                        RootExtChildRowAddress(rootExtChildRow.RootExtChildCode),
                        rootExtChildRow.HiddenMemberPaths
                    )
                );
            }

            return new ProfileAppliedWriteContext(
                Request: request,
                VisibleStoredBody: storedDocument,
                StoredScopeStates: storedScopeStates.ToImmutable(),
                VisibleStoredCollectionRows: visibleStoredRows.ToImmutable()
            );
        }
    }

    /// <summary>
    /// Builds the provider-neutral <see cref="StoredStateProjectionInvoker"/> from optional
    /// stored row/scope inputs. Provided so each provider's <c>CreateProfileContext</c>
    /// wrapper does not need to repeat the materialization to <see cref="ImmutableArray{T}"/>.
    /// </summary>
    public static IStoredStateProjectionInvoker BuildStoredStateProjectionInvoker(
        IReadOnlyList<StoredParentRow>? storedParentRows = null,
        IReadOnlyList<StoredChildRow>? storedChildRows = null,
        IReadOnlyList<StoredRootExtChildRow>? storedRootExtChildRows = null,
        StoredRootExtScope? storedRootExtScope = null
    ) =>
        new StoredStateProjectionInvoker(
            [.. storedParentRows ?? []],
            [.. storedChildRows ?? []],
            [.. storedRootExtChildRows ?? []],
            storedRootExtScope
        );

    /// <summary>
    /// Asserts a profiled visible-child update preserved stored nested-child identity and the
    /// deterministic sibling order: the pre-write rowset must be the seeded visible rows (in
    /// request order) followed by the hidden sibling with contiguous ordinals; the post-write
    /// rowset must be exactly the visible rows in request order carrying their pre-write
    /// CollectionItemId, parent CollectionItemId, and parent resource DocumentId with the intended
    /// updated values, then the hidden sibling row byte-for-byte unchanged in its relative
    /// position, all with contiguous 0-based ordinals. A delete-and-reinsert (new ids), wrong
    /// parent linkage, or sibling reordering fails this exact-sequence comparison.
    /// </summary>
    public static void AssertVisibleChildUpdatePreservesHiddenSiblingAndIdentities(
        IReadOnlyList<ChildRow> beforeRows,
        IReadOnlyList<ChildRow> afterRows,
        IReadOnlyList<(string ChildCode, string UpdatedValue)> visibleUpdatesInRequestOrder,
        string hiddenChildCode,
        string hiddenChildValue
    )
    {
        // Non-vacuous pre-state: the seeded visible rows in request order, then the hidden sibling,
        // with contiguous ordinals.
        List<string> expectedBeforeCodes =
        [
            .. visibleUpdatesInRequestOrder.Select(update => update.ChildCode),
            hiddenChildCode,
        ];
        beforeRows.Select(row => row.ChildCode).Should().Equal(expectedBeforeCodes);
        beforeRows.Select(row => row.Ordinal).Should().Equal(Enumerable.Range(0, beforeRows.Count));

        var beforeByCode = beforeRows.ToDictionary(row => row.ChildCode!);
        ChildRow hiddenBefore = beforeByCode[hiddenChildCode];
        hiddenBefore.ChildValue.Should().Be(hiddenChildValue);

        List<ChildRow> expectedAfter = [];
        int ordinal = 0;

        foreach ((string childCode, string updatedValue) in visibleUpdatesInRequestOrder)
        {
            expectedAfter.Add(
                beforeByCode[childCode] with
                {
                    Ordinal = ordinal++,
                    ChildValue = updatedValue,
                }
            );
        }

        expectedAfter.Add(hiddenBefore with { Ordinal = ordinal });

        afterRows.Should().Equal(expectedAfter);
    }

    /// <summary>
    /// Asserts a hidden root-extension scope survived a profiled write exactly unchanged: the
    /// pre-write root-extension row must carry the seeded scalars and its child rowset must be the
    /// seeded codes/values with contiguous ordinals (non-vacuous), and the post-write row and child
    /// rowset must equal the pre-write state byte-for-byte — identities, linkage, values, ordinals,
    /// and order included.
    /// </summary>
    public static void AssertHiddenRootExtensionScopePreservedExactly(
        RootExtRow? beforeRootExtRow,
        RootExtRow? afterRootExtRow,
        IReadOnlyList<RootExtChildRow> beforeChildRows,
        IReadOnlyList<RootExtChildRow> afterChildRows,
        IReadOnlyList<(string ChildCode, string ChildValue)> expectedSeededChildrenInOrder,
        string expectedSeededVisibleScalar,
        string expectedSeededHiddenScalar
    )
    {
        // Non-vacuous pre-state: the seeded root-extension row and its ordered child rows exist.
        beforeRootExtRow.Should().NotBeNull();
        beforeRootExtRow!.RootExtVisibleScalar.Should().Be(expectedSeededVisibleScalar);
        beforeRootExtRow.RootExtHiddenScalar.Should().Be(expectedSeededHiddenScalar);
        beforeChildRows
            .Select(row => row.RootExtChildCode)
            .Should()
            .Equal(expectedSeededChildrenInOrder.Select(child => child.ChildCode));
        beforeChildRows
            .Select(row => row.RootExtChildValue)
            .Should()
            .Equal(expectedSeededChildrenInOrder.Select(child => child.ChildValue));
        beforeChildRows.Select(row => row.Ordinal).Should().Equal(Enumerable.Range(0, beforeChildRows.Count));

        afterRootExtRow.Should().Be(beforeRootExtRow);
        afterChildRows.Should().Equal(beforeChildRows);
    }
}
