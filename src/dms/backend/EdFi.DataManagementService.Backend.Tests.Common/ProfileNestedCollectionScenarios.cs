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
        AllowIdentityUpdates: false,
        EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
        AuthorizationSecurableInfo: []
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
}
