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

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Provider-agnostic scenario data and assertion helpers shared by the MSSQL and
/// PostgreSQL aligned-extension profile-merge integration suites. Each provider suite
/// keeps its own fixture creation, resolver registration, database type, SQL dialect,
/// readback, and parameter wiring, but consumes the input / stored-row / request-item
/// records, body builders, and address builders defined here so the two suites share a
/// single source of truth for the tested scenarios.
/// </summary>
public static class ProfileCollectionAlignedExtensionScenarios
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/profile-collection-aligned-extension";

    public const string ParentScope = "$.parents[*]";
    public const string AlignedScope = "$.parents[*]._ext.aligned";
    public const string AlignedChildScope = "$.parents[*]._ext.aligned.children[*]";
    public const string ExtensionChildScope = "$.parents[*]._ext.aligned.children[*].extensionChildren[*]";

    public static readonly QualifiedResourceName ParentResource = new("Ed-Fi", "ParentResource");

    public static readonly ResourceInfo ParentResourceInfo = new(
        ProjectName: new ProjectName("Ed-Fi"),
        ResourceName: new ResourceName("ParentResource"),
        IsDescriptor: false,
        ResourceVersion: new SemVer("1.0.0"),
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
    );

    public static JsonNode CreateParentResourceBody(int parentResourceId, params ParentInput[] parents)
    {
        JsonArray parentNodes = [];
        foreach (var parent in parents)
        {
            JsonObject parentNode = new()
            {
                ["parentCode"] = parent.ParentCode,
                ["parentName"] = parent.ParentName,
            };

            if (parent.Aligned is not null)
            {
                JsonObject alignedNode = new()
                {
                    ["alignedVisibleScalar"] = parent.Aligned.AlignedVisibleScalar,
                    ["alignedHiddenScalar"] = parent.Aligned.AlignedHiddenScalar,
                };

                if (parent.Aligned.Children is not null)
                {
                    JsonArray childNodes = [];
                    foreach (var child in parent.Aligned.Children)
                    {
                        JsonObject childNode = new() { ["childCode"] = child.ChildCode };
                        if (child.ChildValue is not null)
                        {
                            childNode["childValue"] = child.ChildValue;
                        }
                        if (child.ExtensionChildren is not null)
                        {
                            JsonArray extensionChildNodes = [];
                            foreach (var extensionChild in child.ExtensionChildren)
                            {
                                JsonObject extensionChildNode = new()
                                {
                                    ["extensionChildCode"] = extensionChild.ExtensionChildCode,
                                };
                                if (extensionChild.ExtensionChildValue is not null)
                                {
                                    extensionChildNode["extensionChildValue"] =
                                        extensionChild.ExtensionChildValue;
                                }
                                extensionChildNodes.Add(extensionChildNode);
                            }
                            childNode["extensionChildren"] = extensionChildNodes;
                        }
                        childNodes.Add(childNode);
                    }
                    alignedNode["children"] = childNodes;
                }

                parentNode["_ext"] = new JsonObject { ["aligned"] = alignedNode };
            }

            parentNodes.Add(parentNode);
        }

        return new JsonObject { ["parentResourceId"] = parentResourceId, ["parents"] = parentNodes };
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

    public static CollectionRowAddress ParentCollectionRowAddress(string parentCode) =>
        new(ParentScope, new ScopeInstanceAddress("$", []), ParentIdentity(parentCode));

    public static ScopeInstanceAddress ParentContainingScopeAddress(string parentCode) =>
        new(ParentScope, [new AncestorCollectionInstance(ParentScope, ParentIdentity(parentCode))]);

    public static ScopeInstanceAddress AlignedScopeAddress(string parentCode) =>
        new(AlignedScope, ParentContainingScopeAddress(parentCode).AncestorCollectionInstances);

    public static ImmutableArray<SemanticIdentityPart> AlignedChildIdentity(string childCode) =>
        [new SemanticIdentityPart("childCode", JsonValue.Create(childCode), IsPresent: true)];

    public static CollectionRowAddress AlignedChildCollectionRowAddress(
        string parentCode,
        string childCode
    ) => new(AlignedChildScope, AlignedScopeAddress(parentCode), AlignedChildIdentity(childCode));

    public static ImmutableArray<SemanticIdentityPart> ExtensionChildIdentity(string extensionChildCode) =>
        [
            new SemanticIdentityPart(
                "extensionChildCode",
                JsonValue.Create(extensionChildCode),
                IsPresent: true
            ),
        ];

    public static ScopeInstanceAddress AlignedChildContainingScopeAddress(string parentCode, string childCode)
    {
        var alignedScopeAncestors = AlignedScopeAddress(parentCode).AncestorCollectionInstances;
        return new ScopeInstanceAddress(
            AlignedChildScope,
            alignedScopeAncestors.Add(
                new AncestorCollectionInstance(AlignedChildScope, AlignedChildIdentity(childCode))
            )
        );
    }

    public static CollectionRowAddress ExtensionChildCollectionRowAddress(
        string parentCode,
        string childCode,
        string extensionChildCode
    ) =>
        new(
            ExtensionChildScope,
            AlignedChildContainingScopeAddress(parentCode, childCode),
            ExtensionChildIdentity(extensionChildCode)
        );

    /// <summary>
    /// Builds a profile-applied write request for the aligned-extension scenario.
    /// The provider-specific projection invoker is supplied by each suite so this helper
    /// stays free of provider dependencies.
    /// </summary>
    public static BackendProfileWriteContext CreateProfileContext(
        ResourceWritePlan writePlan,
        JsonNode requestBody,
        IReadOnlyList<RequestParentItem> requestParentItems,
        IReadOnlyList<RequestAlignedScope> requestAlignedScopes,
        IStoredStateProjectionInvoker storedStateProjectionInvoker,
        IReadOnlyList<RequestAlignedChildItem>? requestAlignedChildItems = null,
        IReadOnlyList<RequestExtensionChildItem>? requestExtensionChildItems = null,
        bool rootCreatable = true,
        string profileName = "collection-aligned-extension-profile"
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
        var visibleRequestItemsBuilder = ImmutableArray.CreateBuilder<VisibleRequestCollectionItem>();
        visibleRequestItemsBuilder.AddRange(
            requestParentItems.Select(item => new VisibleRequestCollectionItem(
                ParentCollectionRowAddress(item.ParentCode),
                item.Creatable,
                $"$.parents[{item.ArrayIndex}]"
            ))
        );
        if (requestAlignedChildItems is not null)
        {
            visibleRequestItemsBuilder.AddRange(
                requestAlignedChildItems.Select(item => new VisibleRequestCollectionItem(
                    AlignedChildCollectionRowAddress(item.ParentCode, item.ChildCode),
                    item.Creatable,
                    $"$.parents[{item.ParentArrayIndex}]._ext.aligned.children[{item.ChildArrayIndex}]"
                ))
            );
        }
        if (requestExtensionChildItems is not null)
        {
            visibleRequestItemsBuilder.AddRange(
                requestExtensionChildItems.Select(item => new VisibleRequestCollectionItem(
                    ExtensionChildCollectionRowAddress(
                        item.ParentCode,
                        item.ChildCode,
                        item.ExtensionChildCode
                    ),
                    item.Creatable,
                    $"$.parents[{item.ParentArrayIndex}]._ext.aligned.children[{item.ChildArrayIndex}].extensionChildren[{item.ExtensionChildArrayIndex}]"
                ))
            );
        }
        var visibleRequestItems = visibleRequestItemsBuilder.ToImmutable();

        var requestScopeStates = ImmutableArray.CreateBuilder<RequestScopeState>();
        requestScopeStates.Add(
            new RequestScopeState(
                new ScopeInstanceAddress("$", []),
                ProfileVisibilityKind.VisiblePresent,
                rootCreatable
            )
        );
        requestScopeStates.AddRange(
            requestAlignedScopes.Select(scope => new RequestScopeState(
                AlignedScopeAddress(scope.ParentCode),
                scope.Visibility,
                scope.Creatable
            ))
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: requestBody,
                RootResourceCreatable: rootCreatable,
                RequestScopeStates: requestScopeStates.ToImmutable(),
                VisibleRequestCollectionItems: visibleRequestItems
            ),
            ProfileName: profileName,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: storedStateProjectionInvoker
        );
    }

    public sealed record ParentInput(string ParentCode, string ParentName, AlignedInput? Aligned = null);

    public sealed record AlignedInput(
        string AlignedVisibleScalar,
        string AlignedHiddenScalar,
        IReadOnlyList<AlignedChildInput>? Children = null
    );

    public sealed record AlignedChildInput(
        string ChildCode,
        string? ChildValue,
        IReadOnlyList<ExtensionChildInput>? ExtensionChildren = null
    );

    public sealed record ExtensionChildInput(string ExtensionChildCode, string? ExtensionChildValue);

    public sealed record RequestParentItem(string ParentCode, int ArrayIndex, bool Creatable = true);

    public sealed record RequestAlignedScope(
        string ParentCode,
        ProfileVisibilityKind Visibility,
        bool Creatable
    );

    public sealed record RequestAlignedChildItem(
        string ParentCode,
        string ChildCode,
        int ParentArrayIndex,
        int ChildArrayIndex,
        bool Creatable = true
    );

    public sealed record RequestExtensionChildItem(
        string ParentCode,
        string ChildCode,
        string ExtensionChildCode,
        int ParentArrayIndex,
        int ChildArrayIndex,
        int ExtensionChildArrayIndex,
        bool Creatable = true
    );

    public sealed record StoredParentRow(string ParentCode, ImmutableArray<string> HiddenMemberPaths);

    public sealed record StoredAlignedScope(
        string ParentCode,
        ProfileVisibilityKind Visibility,
        ImmutableArray<string> HiddenMemberPaths
    );

    public sealed record StoredAlignedChildRow(
        string ParentCode,
        string ChildCode,
        ImmutableArray<string> HiddenMemberPaths
    );

    public sealed record StoredExtensionChildRow(
        string ParentCode,
        string ChildCode,
        string ExtensionChildCode,
        ImmutableArray<string> HiddenMemberPaths
    );

    public sealed record ParentRow(
        long CollectionItemId,
        long ParentResourceDocumentId,
        int Ordinal,
        string ParentCode,
        string ParentName
    );

    public sealed record AlignedRow(
        long BaseCollectionItemId,
        long ParentResourceDocumentId,
        string? AlignedVisibleScalar,
        string? AlignedHiddenScalar
    );

    public sealed record AlignedChildRow(
        long CollectionItemId,
        long BaseCollectionItemId,
        long ParentResourceDocumentId,
        int Ordinal,
        string ChildCode,
        string? ChildValue
    );

    public sealed record ExtensionChildRow(
        long CollectionItemId,
        long ParentCollectionItemId,
        long ParentResourceDocumentId,
        int Ordinal,
        string ExtensionChildCode,
        string? ExtensionChildValue
    );
}
