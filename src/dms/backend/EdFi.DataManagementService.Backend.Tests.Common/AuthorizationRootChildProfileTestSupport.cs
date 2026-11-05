// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Tests.Common;

file sealed class AuthorizationRootChildStoredProjectionInvoker(ImmutableArray<string> hiddenMemberPaths)
    : IStoredStateProjectionInvoker
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
                    HiddenMemberPaths: hiddenMemberPaths
                ),
            ],
            VisibleStoredCollectionRows: AuthorizationRootChildProfileTestSupport
                .CreateClassPeriodAddresses(storedDocument, scopeCatalog)
                .Select(static address => new VisibleStoredCollectionRow(address, []))
                .ToImmutableArray()
        );
    }
}

internal static class AuthorizationRootChildProfileTestSupport
{
    public static void AssertNoRelationshipResponseParity(
        RelationshipAuthorizationFailure missingFailure,
        RelationshipAuthorizationFailure unrelatedFailure
    )
    {
        missingFailure.Should().BeEquivalentTo(unrelatedFailure, options => options.WithStrictOrdering());
        var traceId = new TraceId("profile-reference-parity");
        var missingProblem = FailureResponse.ForRelationshipAuthorization(traceId, missingFailure);
        var unrelatedProblem = FailureResponse.ForRelationshipAuthorization(traceId, unrelatedFailure);
        missingProblem["status"]!.GetValue<int>().Should().Be(403);
        missingProblem["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:security:authorization");
        missingProblem["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Access to the requested data could not be authorized.");
        missingProblem["errors"]!
            .AsArray()
            .Select(error => error!.GetValue<string>())
            .Should()
            .Equal(
                "No relationships have been established between the caller's education organization id claim ('900') and the resource item's 'SchoolId' value."
            );
        JsonNode.DeepEquals(missingProblem, unrelatedProblem).Should().BeTrue();
    }

    private static readonly QualifiedResourceName AuthorizationRootChildResource = new(
        "Authz",
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName
    );

    public static JsonNode CreateRootSchoolHiddenBody(AuthorizationRootChildSeed seed) =>
        new JsonObject { ["authorizationRootChildId"] = seed.AuthorizationRootChildId, ["name"] = seed.Name };

    public static BackendProfileWriteContext CreateRootSchoolHiddenProfileContext(
        MappingSet mappingSet,
        JsonNode writableRequestBody
    ) => CreateRootProfileContext(mappingSet, writableRequestBody, ["schoolReference"]);

    public static BackendProfileWriteContext CreateRootSchoolVisibleProfileContext(
        MappingSet mappingSet,
        JsonNode writableRequestBody,
        bool classPeriodsCreatable = true
    ) => CreateRootProfileContext(mappingSet, writableRequestBody, [], classPeriodsCreatable);

    public static ImmutableArray<CollectionRowAddress> CreateClassPeriodAddresses(
        JsonNode body,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var scope = scopeCatalog.Single(static scope => scope.JsonScope == "$.classPeriods[*]");
        return (body["classPeriods"]?.AsArray() ?? [])
            .Select(item => new CollectionRowAddress(
                scope.JsonScope,
                new ScopeInstanceAddress("$", []),
                scope
                    .SemanticIdentityRelativePathsInOrder.Select(path => new SemanticIdentityPart(
                        path,
                        path.TrimStart('$', '.')
                            .Split('.')
                            .Aggregate(item!, static (node, member) => node[member]!)
                            .DeepClone(),
                        IsPresent: true
                    ))
                    .ToImmutableArray()
            ))
            .ToImmutableArray();
    }

    private static BackendProfileWriteContext CreateRootProfileContext(
        MappingSet mappingSet,
        JsonNode writableRequestBody,
        ImmutableArray<string> hiddenMemberPaths,
        bool classPeriodsCreatable = true
    )
    {
        var scopeCatalog = CompiledScopeAdapterFactory.BuildFromWritePlan(
            mappingSet.WritePlansByResource[AuthorizationRootChildResource]
        );

        return new BackendProfileWriteContext(
            Request: new ProfileAppliedWriteRequest(
                WritableRequestBody: writableRequestBody,
                RootResourceCreatable: true,
                RequestScopeStates:
                [
                    new RequestScopeState(
                        Address: new ScopeInstanceAddress("$", []),
                        Visibility: ProfileVisibilityKind.VisiblePresent,
                        Creatable: true
                    ),
                ],
                VisibleRequestCollectionItems: CreateClassPeriodAddresses(writableRequestBody, scopeCatalog)
                    .Select(
                        (address, index) =>
                            new VisibleRequestCollectionItem(
                                address,
                                Creatable: classPeriodsCreatable,
                                $"$.classPeriods[{index}]"
                            )
                    )
                    .ToImmutableArray()
            ),
            ProfileName: "authorization-root-child-hidden-school-profile",
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: new AuthorizationRootChildStoredProjectionInvoker(hiddenMemberPaths)
        );
    }
}
