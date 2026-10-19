// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Backend.Tests.Unit.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalWriteRowHelpers_RebindCollectionSemanticIdentity
{
    // CollectionSemanticIdentityEqualityTestPlans layout: [3] City (string, flag under test),
    // [4] Program_DocumentId, [5] TypeDescriptor_DescriptorId, [6] Amount.
    private const int CityBindingIndex = 3;

    [Test]
    public void It_copies_the_stored_spelling_of_a_case_variant_collated_member()
    {
        var rebound = Rebind(cityUsesSqlServerIdentityCollation: true, requestCity: "AUSTIN");

        rebound[CityBindingIndex].Should().Be(new FlattenedWriteValue.Literal("Austin"));
    }

    [Test]
    public void It_keeps_the_request_spelling_of_an_unflagged_member()
    {
        var rebound = Rebind(cityUsesSqlServerIdentityCollation: false, requestCity: "AUSTIN");

        rebound[CityBindingIndex].Should().Be(new FlattenedWriteValue.Literal("AUSTIN"));
    }

    [Test]
    public void It_keeps_a_value_matched_only_by_the_database_collation()
    {
        // "Straße" and "Strasse" are equal under SQL_Latin1_General_CP1_CI_AS but not under
        // OrdinalIgnoreCase, so the helper never rewrites a value the comparer calls different.
        var rebound = Rebind(
            cityUsesSqlServerIdentityCollation: true,
            requestCity: "Straße",
            storedCity: "Strasse"
        );

        rebound[CityBindingIndex].Should().Be(new FlattenedWriteValue.Literal("Straße"));
    }

    [Test]
    public void It_leaves_non_identity_values_as_requested()
    {
        var rebound = Rebind(cityUsesSqlServerIdentityCollation: true, requestCity: "AUSTIN");

        rebound[2].Should().Be(new FlattenedWriteValue.Literal(7));
        rebound[6].Should().Be(new FlattenedWriteValue.Literal(2.50m));
    }

    private static ImmutableArray<FlattenedWriteValue> Rebind(
        bool cityUsesSqlServerIdentityCollation,
        string requestCity,
        string storedCity = "Austin"
    )
    {
        var plan = CollectionSemanticIdentityEqualityTestPlans.Create(cityUsesSqlServerIdentityCollation);

        return RelationalWriteRowHelpers.RebindCollectionSemanticIdentity(
            plan,
            Row(requestCity, ordinal: 7, amount: 2.50m),
            Row(storedCity, ordinal: 0, amount: 1.00m)
        );
    }

    private static FlattenedWriteValue[] Row(string city, int ordinal, decimal amount) =>
        [
            new FlattenedWriteValue.Literal(50L),
            new FlattenedWriteValue.Literal(345L),
            new FlattenedWriteValue.Literal(ordinal),
            new FlattenedWriteValue.Literal(city),
            new FlattenedWriteValue.Literal(null),
            new FlattenedWriteValue.Literal(null),
            new FlattenedWriteValue.Literal(amount),
        ];
}

internal static class RequestAncestorAlignmentTestSupport
{
    public const string AddressesScope = "$.addresses[*]";
    public const string PeriodsScope = "$.addresses[*].periods[*]";
    public const string AlignedScope = "$.addresses[*]._ext.sample";

    // AdapterFactoryTestFixtures: $.addresses[*] whose semantic identity is addressType at binding 3.
    public static ResourceWritePlan WritePlan(bool addressTypeUsesSqlServerIdentityCollation)
    {
        var writePlan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
        var collectionPlan = writePlan.TablePlansInDependencyOrder[1];
        var addressTypeBinding = collectionPlan.ColumnBindings[3];
        var flaggedCollectionPlan = collectionPlan with
        {
            ColumnBindings = collectionPlan.ColumnBindings.SetItem(
                3,
                addressTypeBinding with
                {
                    Column = addressTypeBinding.Column with
                    {
                        UsesSqlServerIdentityCollation = addressTypeUsesSqlServerIdentityCollation,
                    },
                }
            ),
        };

        return new ResourceWritePlan(
            writePlan.Model,
            [writePlan.TablePlansInDependencyOrder[0], flaggedCollectionPlan]
        );
    }

    public static ImmutableArray<SemanticIdentityPart> Identity(string relativePath, string value) =>
        [new SemanticIdentityPart(relativePath, JsonValue.Create(value), IsPresent: true)];

    public static ImmutableArray<AncestorCollectionInstance> AddressAncestors(string addressType) =>
        [new AncestorCollectionInstance(AddressesScope, Identity("addressType", addressType))];

    public static ProfileAppliedWriteRequest Request(string requestAddressType) =>
        new(
            WritableRequestBody: new JsonObject(),
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
                new RequestScopeState(
                    new ScopeInstanceAddress(AlignedScope, AddressAncestors(requestAddressType)),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        AddressesScope,
                        new ScopeInstanceAddress("$", []),
                        Identity("addressType", requestAddressType)
                    ),
                    true,
                    "$.addresses[0]"
                ),
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        PeriodsScope,
                        new ScopeInstanceAddress(AddressesScope, AddressAncestors(requestAddressType)),
                        Identity("periodName", "First")
                    ),
                    true,
                    "$.addresses[0].periods[0]"
                ),
            ]
        );

    public static ProfileAppliedWriteContext StoredContext(
        ProfileAppliedWriteRequest request,
        params string[] storedAddressTypes
    ) =>
        new(
            request,
            new JsonObject(),
            [],
            [
                .. storedAddressTypes.Select(addressType => new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        AddressesScope,
                        new ScopeInstanceAddress("$", []),
                        Identity("addressType", addressType)
                    ),
                    []
                )),
            ]
        );

    public static ProfileAppliedWriteRequest Align(
        bool addressTypeUsesSqlServerIdentityCollation,
        string requestAddressType,
        params string[] storedAddressTypes
    )
    {
        var request = Request(requestAddressType);

        return RelationalWriteProfileMergeSynthesizer.AlignRequestAncestorsToStoredIdentities(
            request,
            StoredContext(request, storedAddressTypes),
            WritePlan(addressTypeUsesSqlServerIdentityCollation)
        );
    }

    public static string AncestorValue(ScopeInstanceAddress address) =>
        address
            .AncestorCollectionInstances.Single()
            .SemanticIdentityInOrder.Single()
            .Value!.GetValue<string>();
}

[TestFixture]
[Parallelizable]
public class Given_request_ancestor_alignment_for_a_parent_recased_on_a_collated_column
{
    private ProfileAppliedWriteRequest _aligned = null!;

    [SetUp]
    public void Setup() =>
        _aligned = RequestAncestorAlignmentTestSupport.Align(
            addressTypeUsesSqlServerIdentityCollation: true,
            requestAddressType: "HOME",
            "Home",
            "Work"
        );

    [Test]
    public void It_aligns_the_parent_address_of_a_nested_request_item() =>
        RequestAncestorAlignmentTestSupport
            .AncestorValue(_aligned.VisibleRequestCollectionItems[1].Address.ParentAddress)
            .Should()
            .Be("Home");

    [Test]
    public void It_aligns_the_ancestors_of_a_request_scope_state() =>
        RequestAncestorAlignmentTestSupport
            .AncestorValue(_aligned.RequestScopeStates[1].Address)
            .Should()
            .Be("Home");

    [Test]
    public void It_keeps_the_request_spelling_of_the_item_own_identity() =>
        _aligned
            .VisibleRequestCollectionItems[0]
            .Address.SemanticIdentityInOrder.Single()
            .Value!.GetValue<string>()
            .Should()
            .Be("HOME");
}

[TestFixture]
[Parallelizable]
public class Given_request_ancestor_alignment_that_must_leave_the_request_unchanged
{
    [Test]
    public void It_returns_the_same_request_for_an_unflagged_column()
    {
        var request = RequestAncestorAlignmentTestSupport.Request("HOME");

        RelationalWriteProfileMergeSynthesizer
            .AlignRequestAncestorsToStoredIdentities(
                request,
                RequestAncestorAlignmentTestSupport.StoredContext(request, "Home"),
                RequestAncestorAlignmentTestSupport.WritePlan(
                    addressTypeUsesSqlServerIdentityCollation: false
                )
            )
            .Should()
            .BeSameAs(request);
    }

    [Test]
    public void It_returns_the_same_request_for_a_create()
    {
        var request = RequestAncestorAlignmentTestSupport.Request("HOME");

        RelationalWriteProfileMergeSynthesizer
            .AlignRequestAncestorsToStoredIdentities(
                request,
                profileAppliedContext: null,
                RequestAncestorAlignmentTestSupport.WritePlan(addressTypeUsesSqlServerIdentityCollation: true)
            )
            .Should()
            .BeSameAs(request);
    }

    [TestCase("Work", TestName = "No stored row matches")]
    [TestCase("Home", "HOME", TestName = "Two stored rows match ambiguously")]
    public void It_keeps_the_request_ancestor(params string[] storedAddressTypes)
    {
        var aligned = RequestAncestorAlignmentTestSupport.Align(
            addressTypeUsesSqlServerIdentityCollation: true,
            requestAddressType: "HOME",
            storedAddressTypes
        );

        RequestAncestorAlignmentTestSupport
            .AncestorValue(aligned.VisibleRequestCollectionItems[1].Address.ParentAddress)
            .Should()
            .Be("HOME");
        RequestAncestorAlignmentTestSupport
            .AncestorValue(aligned.RequestScopeStates[1].Address)
            .Should()
            .Be("HOME");
    }
}
