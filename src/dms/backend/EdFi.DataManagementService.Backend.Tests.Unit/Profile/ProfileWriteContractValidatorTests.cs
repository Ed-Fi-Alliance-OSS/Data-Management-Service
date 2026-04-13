// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_ValidRequestContract_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog: root scope "$" with no collection ancestors
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        // Request with one scope state whose address matches the catalog
        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems: []
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_UnknownJsonScope_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog has only the root scope, but the request references an unknown scope
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        // Address references a scope not in the catalog
        var unknownAddress = new ScopeInstanceAddress("$.nonExistentScope", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(unknownAddress, ProfileVisibilityKind.VisiblePresent, Creatable: false),
            ],
            VisibleRequestCollectionItems: []
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_category_CoreBackendContractMismatch()
    {
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
    }

    [Test]
    public void It_emits_an_UnknownJsonScopeCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<UnknownJsonScopeCoreBackendContractMismatchFailure>();
    }

    [Test]
    public void It_captures_the_unknown_json_scope()
    {
        var failure = (UnknownJsonScopeCoreBackendContractMismatchFailure)_result[0];
        failure.JsonScope.Should().Be("$.nonExistentScope");
    }
}

[TestFixture]
public class Given_ValidCollectionItemAddress_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog: root + collection scopes
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
        };

        // Valid collection row address — parent has no ancestor collection instances (matches empty CollectionAncestorsInOrder)
        var parentAddress = new ScopeInstanceAddress("$", []);
        var identityParts = ImmutableArray.Create(
            new SemanticIdentityPart("addressType", JsonValue.Create("Physical")!, true)
        );
        var collectionRowAddress = new CollectionRowAddress("$.addresses[*]", parentAddress, identityParts);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    collectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_UnknownCollectionJsonScope_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        // Collection item references a scope not in the catalog
        var parentAddress = new ScopeInstanceAddress("$", []);
        var collectionRowAddress = new CollectionRowAddress("$.unknownCollection[*]", parentAddress, []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    collectionRowAddress,
                    Creatable: false,
                    RequestJsonPath: "$.unknownCollection[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_category_CoreBackendContractMismatch()
    {
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
    }

    [Test]
    public void It_emits_an_UnknownJsonScopeCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<UnknownJsonScopeCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_AncestorChainMismatch_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog: nested collection requiring one ancestor
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
            new(
                JsonScope: "$.addresses[*].periods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$.addresses[*]",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: ["periodType"],
                CanonicalScopeRelativeMemberPaths: ["periodType"]
            ),
        };

        // Address references the nested collection but provides wrong ancestor chain (empty instead of one ancestor)
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                // Root: valid (no ancestors needed)
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                // addresses[*] as a NonCollection scope (addresses[*] is a collection scope but testing via RequestScopeState)
                // Use root which is valid but add a scope with wrong ancestor for the nested collection
                new RequestScopeState(
                    new ScopeInstanceAddress(
                        "$.addresses[*].periods[*]",
                        AncestorCollectionInstances: [] // wrong: expects 1 ancestor "$.addresses[*]"
                    ),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_category_CoreBackendContractMismatch()
    {
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
    }

    [Test]
    public void It_emits_an_AncestorChainMismatchCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<AncestorChainMismatchCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_ValidWriteContext_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId", "nameOfInstitution"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                // Hidden member path is in the catalog
                new StoredScopeState(rootAddress, ProfileVisibilityKind.Hidden, ["nameOfInstitution"]),
            ],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_HiddenMemberPathNotInCatalog_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                // Hidden member path is NOT in the catalog
                new StoredScopeState(rootAddress, ProfileVisibilityKind.Hidden, ["unknownMemberPath"]),
            ],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_category_CoreBackendContractMismatch()
    {
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
    }

    [Test]
    public void It_emits_a_CanonicalMemberPathMismatchCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<CanonicalMemberPathMismatchCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_CollectionRow_with_unknown_ParentAddress_JsonScope_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
        };

        // ParentAddress references a scope that doesn't exist in the catalog
        var collectionRowAddress = new CollectionRowAddress(
            JsonScope: "$.addresses[*]",
            ParentAddress: new ScopeInstanceAddress("$.unknownScope", []),
            SemanticIdentityInOrder: [new SemanticIdentityPart("addressType", JsonValue.Create("Home"), true)]
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    collectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "POST",
            operation: "upsert"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_a_ParentScopeMismatchCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<ParentScopeMismatchCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_CollectionRow_with_wrong_semantic_identity_part_count_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
        };

        // Emitted address has TWO semantic identity parts but the compiled scope expects ONE
        var collectionRowAddress = new CollectionRowAddress(
            JsonScope: "$.addresses[*]",
            ParentAddress: new ScopeInstanceAddress("$", []),
            SemanticIdentityInOrder:
            [
                new SemanticIdentityPart("addressType", JsonValue.Create("Home"), true),
                new SemanticIdentityPart("extraField", JsonValue.Create("X"), true),
            ]
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    collectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "POST",
            operation: "upsert"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_a_SemanticIdentityMismatchCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<SemanticIdentityMismatchCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_CollectionRow_with_wrong_semantic_identity_path_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
        };

        // Emitted address has correct count but wrong path name
        var collectionRowAddress = new CollectionRowAddress(
            JsonScope: "$.addresses[*]",
            ParentAddress: new ScopeInstanceAddress("$", []),
            SemanticIdentityInOrder: [new SemanticIdentityPart("wrongPath", JsonValue.Create("Home"), true)]
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    collectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "POST",
            operation: "upsert"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_a_SemanticIdentityMismatchCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<SemanticIdentityMismatchCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_valid_CollectionRow_with_correct_semantic_identity_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
        };

        var collectionRowAddress = new CollectionRowAddress(
            JsonScope: "$.addresses[*]",
            ParentAddress: new ScopeInstanceAddress("$", []),
            SemanticIdentityInOrder: [new SemanticIdentityPart("addressType", JsonValue.Create("Home"), true)]
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    collectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "POST",
            operation: "upsert"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_valid_nested_CollectionRowAddress_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog: root + addresses collection + nested periods collection
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
            new(
                JsonScope: "$.addresses[*].periods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$.addresses[*]",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: ["periodType"],
                CanonicalScopeRelativeMemberPaths: ["periodType"]
            ),
        };

        // Parent address for the nested collection row is an addresses[*] instance.
        // Per the address derivation contract, the parent carries the parent collection
        // instance itself in AncestorCollectionInstances so the validator can align with
        // the child scope's CollectionAncestorsInOrder.
        var addressIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("addressType", JsonValue.Create("Physical")!, true)
        );
        var parentAncestorInstance = new AncestorCollectionInstance("$.addresses[*]", addressIdentity);
        var parentAddress = new ScopeInstanceAddress("$.addresses[*]", [parentAncestorInstance]);

        var periodIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("periodType", JsonValue.Create("Current")!, true)
        );
        var nestedCollectionRowAddress = new CollectionRowAddress(
            "$.addresses[*].periods[*]",
            parentAddress,
            periodIdentity
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    nestedCollectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0].periods[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_nested_CollectionRowAddress_with_wrong_ancestor_chain_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$.addresses[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["addressType"],
                CanonicalScopeRelativeMemberPaths: ["addressType"]
            ),
            new(
                JsonScope: "$.addresses[*].periods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$.addresses[*]",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: ["periodType"],
                CanonicalScopeRelativeMemberPaths: ["periodType"]
            ),
        };

        // Wrong: parent address has empty ancestor chain instead of including the
        // addresses[*] collection instance, so ancestor chain won't match the compiled
        // CollectionAncestorsInOrder for periods[*].
        var parentAddress = new ScopeInstanceAddress("$.addresses[*]", []);

        var periodIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("periodType", JsonValue.Create("Current")!, true)
        );
        var nestedCollectionRowAddress = new CollectionRowAddress(
            "$.addresses[*].periods[*]",
            parentAddress,
            periodIdentity
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: [],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    nestedCollectionRowAddress,
                    Creatable: true,
                    RequestJsonPath: "$.addresses[0].periods[0]"
                ),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_emits_category_CoreBackendContractMismatch()
    {
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
    }

    [Test]
    public void It_emits_an_AncestorChainMismatchCoreBackendContractMismatchFailure()
    {
        _result[0].Should().BeOfType<AncestorChainMismatchCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_NestedNonCollectionScope_Missing_RequestScopeState_For_One_Collection_Instance_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog:
        //   $ (root)
        //   $.classPeriods[*] (collection, identified by classPeriodName)
        //   $.classPeriods[*].schoolReference (non-collection, nested under the collection)
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*].schoolReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);

        var itemOneIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("First")!, true)
        );
        var itemTwoIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("Second")!, true)
        );

        var itemOneAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemOneIdentity);
        var itemTwoAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemTwoIdentity);

        // Nested scope's RequestScopeState is emitted for item "First" only - item "Second"
        // is missing, which previously escaped to an InvalidOperationException at merge time.
        var nestedScopeAddressForItemOne = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemOneIdentity))
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    nestedScopeAddressForItemOne,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    itemOneAddress,
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
                new VisibleRequestCollectionItem(
                    itemTwoAddress,
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[1]"
                ),
            ]
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            // Root StoredScopeState satisfies root-completeness so the sole expected failure
            // is the missing request-side nested instance.
            StoredScopeStates: [new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, [])],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "ClassPeriod",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_emits_one_failure_for_the_missing_instance()
    {
        _result.Should().ContainSingle();
        _result[0].Should().BeAssignableTo<CoreBackendContractMismatchFailure>();
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
        _result[0].Message.Should().Contain("$.classPeriods[*].schoolReference");
        _result[0].Message.Should().Contain("Second");
    }
}

[TestFixture]
public class Given_NestedNonCollectionScope_With_All_Instances_Present_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*].schoolReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var itemOneIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("First")!, true)
        );
        var itemTwoIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("Second")!, true)
        );

        var itemOneAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemOneIdentity);
        var itemTwoAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemTwoIdentity);

        var nestedScopeForOne = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemOneIdentity))
        );
        var nestedScopeForTwo = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemTwoIdentity))
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    nestedScopeForOne,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    nestedScopeForTwo,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    itemOneAddress,
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
                new VisibleRequestCollectionItem(
                    itemTwoAddress,
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[1]"
                ),
            ]
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            // Root StoredScopeState satisfies root-completeness; stored completeness for the
            // nested scopes is not exercised here (no VisibleStoredCollectionRows).
            StoredScopeStates: [new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, [])],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "ClassPeriod",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_NestedNonCollectionScope_Hidden_For_One_Instance_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Scope is VisiblePresent for item "First" and Hidden for item "Second". The
        // Hidden instance does not need a RequestScopeState because the merge preserves
        // it via StoredScopeState; the other instance does.
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*].schoolReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var itemOneIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("First")!, true)
        );
        var itemTwoIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("Second")!, true)
        );

        var itemOneAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemOneIdentity);
        var itemTwoAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemTwoIdentity);

        var nestedScopeForOne = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemOneIdentity))
        );
        var nestedScopeForTwo = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemTwoIdentity))
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    nestedScopeForOne,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                // No RequestScopeState for itemTwo's schoolReference - legal because the
                // stored state marks that specific instance Hidden.
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    itemOneAddress,
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
                new VisibleRequestCollectionItem(
                    itemTwoAddress,
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[1]"
                ),
            ]
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(nestedScopeForTwo, ProfileVisibilityKind.Hidden, []),
            ],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "ClassPeriod",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_TopLevelNonCollectionScope_Missing_StoredScopeState_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // A top-level non-collection scope ($._ext.sample) exists in the catalog and the
        // request emits a RequestScopeState for it, but C6 dropped the corresponding
        // StoredScopeState. Without it the merge would silently default HiddenMemberPaths
        // to empty and might overwrite hidden columns.
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
            new(
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["extValue", "hiddenExt"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var extAddress = new ScopeInstanceAddress("$._ext.sample", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(extAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            // Root has stored state; extension scope stored state is deliberately missing.
            StoredScopeStates: [new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, [])],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_emits_one_contract_mismatch_failure_for_the_missing_stored_scope_state()
    {
        _result.Should().ContainSingle();
        _result[0].Should().BeAssignableTo<CoreBackendContractMismatchFailure>();
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
        _result[0].Message.Should().Contain("$._ext.sample").And.Contain("StoredScopeState");
    }
}

[TestFixture]
public class Given_NestedNonCollectionScope_Missing_StoredScopeState_For_One_Stored_Instance_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Two visible stored collection rows ("First", "Second") exist and the nested
        // non-collection scope $.classPeriods[*].schoolReference has its StoredScopeState
        // present for "First" but missing for "Second".
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*].schoolReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var itemOneIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("First")!, true)
        );
        var itemTwoIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("Second")!, true)
        );

        var itemOneAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemOneIdentity);
        var itemTwoAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemTwoIdentity);

        var nestedScopeForOne = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemOneIdentity))
        );
        var nestedScopeForTwo = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemTwoIdentity))
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    nestedScopeForOne,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    nestedScopeForTwo,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(itemOneAddress, true, "$.classPeriods[0]"),
                new VisibleRequestCollectionItem(itemTwoAddress, true, "$.classPeriods[1]"),
            ]
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(nestedScopeForOne, ProfileVisibilityKind.VisiblePresent, []),
                // StoredScopeState for nestedScopeForTwo deliberately omitted.
            ],
            VisibleStoredCollectionRows:
            [
                new VisibleStoredCollectionRow(itemOneAddress, []),
                new VisibleStoredCollectionRow(itemTwoAddress, []),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "ClassPeriod",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_emits_one_failure_for_the_missing_stored_instance()
    {
        _result.Should().ContainSingle();
        _result[0].Should().BeAssignableTo<CoreBackendContractMismatchFailure>();
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
        _result[0].Message.Should().Contain("$.classPeriods[*].schoolReference");
        _result[0].Message.Should().Contain("Second");
        _result[0].Message.Should().Contain("StoredScopeState");
    }
}

[TestFixture]
public class Given_NestedNonCollectionScope_With_All_Stored_Instances_Present_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Both stored instances have matching StoredScopeStates so no completeness failure.
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*].schoolReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var itemOneIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("First")!, true)
        );
        var itemTwoIdentity = ImmutableArray.Create(
            new SemanticIdentityPart("classPeriodName", JsonValue.Create("Second")!, true)
        );

        var itemOneAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemOneIdentity);
        var itemTwoAddress = new CollectionRowAddress("$.classPeriods[*]", rootAddress, itemTwoIdentity);

        var nestedScopeForOne = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemOneIdentity))
        );
        var nestedScopeForTwo = new ScopeInstanceAddress(
            "$.classPeriods[*].schoolReference",
            ImmutableArray.Create(new AncestorCollectionInstance("$.classPeriods[*]", itemTwoIdentity))
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    nestedScopeForOne,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    nestedScopeForTwo,
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(itemOneAddress, true, "$.classPeriods[0]"),
                new VisibleRequestCollectionItem(itemTwoAddress, true, "$.classPeriods[1]"),
            ]
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(nestedScopeForOne, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(nestedScopeForTwo, ProfileVisibilityKind.VisiblePresent, []),
            ],
            VisibleStoredCollectionRows:
            [
                new VisibleStoredCollectionRow(itemOneAddress, []),
                new VisibleStoredCollectionRow(itemTwoAddress, []),
            ]
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "ClassPeriod",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_returns_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_MissingRootStoredScopeState_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // On UPDATE, the merge reads root HiddenMemberPaths via a null-coalescing fallback
        // early in synthesis. If Core drops the root StoredScopeState, the merge would
        // silently treat root as having no hidden members and could overwrite preserved
        // root columns. The validator surfaces this as a category-5 mismatch before merge.
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new(
                JsonScope: "$",
                ScopeKind: ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["schoolId"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            // Root StoredScopeState deliberately missing.
            StoredScopeStates: [],
            VisibleStoredCollectionRows: []
        );

        _result = ProfileWriteContractValidator.ValidateWriteContext(
            context,
            scopeCatalog,
            profileName: "TestProfile",
            resourceName: "School",
            method: "PUT",
            operation: "update"
        );
    }

    [Test]
    public void It_emits_one_contract_mismatch_failure_for_the_missing_root_stored_scope_state()
    {
        _result.Should().ContainSingle();
        _result[0].Should().BeAssignableTo<CoreBackendContractMismatchFailure>();
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
        _result[0].Message.Should().Contain("root scope '$'").And.Contain("StoredScopeState");
    }
}
