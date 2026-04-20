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
        var rootAddress = new ScopeInstanceAddress("$", []);
        var unknownAddress = new ScopeInstanceAddress("$.nonExistentScope", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
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
public class Given_Missing_RequestScopeState_for_required_noncollection_scope_When_Validating
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
                JsonScope: "$.birthData",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["birthCity"]
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
    public void It_returns_a_single_contract_mismatch_failure_for_the_missing_scope()
    {
        _result.Should().ContainSingle();
        var failure = _result[0].Should().BeOfType<GenericCoreBackendContractMismatchFailure>().Subject;
        failure.Message.Should().Contain("RequestScopeStates").And.Contain("$.birthData");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .JsonScope.Should()
            .Be("$.birthData");
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
            RequestScopeStates:
            [
                new RequestScopeState(parentAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
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
            RequestScopeStates:
            [
                new RequestScopeState(parentAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
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
        // Catalog: a NonCollection scope nested inside a collection — requires one ancestor instance
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
                JsonScope: "$.addresses[*].city",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.addresses[*]",
                CollectionAncestorsInOrder: ["$.addresses[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["city", "state"]
            ),
        };

        // Address targets the NonCollection scope but provides wrong ancestor chain
        // (empty instead of one ancestor "$.addresses[*]")
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
                // NonCollection scope with wrong ancestor chain (empty instead of one ancestor)
                new RequestScopeState(
                    new ScopeInstanceAddress(
                        "$.addresses[*].city",
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
public class Given_Missing_StoredScopeState_for_required_noncollection_scope_When_ValidatingWriteContext
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
                JsonScope: "$.birthData",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["birthCity"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var birthDataAddress = new ScopeInstanceAddress("$.birthData", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    birthDataAddress,
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
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
    public void It_returns_a_single_contract_mismatch_failure_for_the_missing_stored_scope()
    {
        _result.Should().ContainSingle();
        var failure = _result[0].Should().BeOfType<GenericCoreBackendContractMismatchFailure>().Subject;
        failure.Message.Should().Contain("StoredScopeStates").And.Contain("$.birthData");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .JsonScope.Should()
            .Be("$.birthData");
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
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
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
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
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
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
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

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
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

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
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

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
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
public class Given_RequestScopeState_targeting_collection_scope_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog: a collection scope at "$.classPeriods[*]"
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
        };

        // RequestScopeState targets a collection scope — should be rejected as kind mismatch
        var collectionAddress = new ScopeInstanceAddress("$.classPeriods[*]", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    collectionAddress,
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
    public void It_returns_a_single_ScopeKindMismatch_failure()
    {
        _result.Should().HaveCount(1);
        _result[0].Should().BeOfType<ScopeKindMismatchCoreBackendContractMismatchFailure>();
    }

    [Test]
    public void It_reports_the_emitted_address_kind_as_NonCollection()
    {
        var failure = (ScopeKindMismatchCoreBackendContractMismatchFailure)_result[0];
        failure.EmittedAddressKind.Should().Be(ScopeKind.NonCollection);
        failure.CompiledScopeKind.Should().Be(ScopeKind.Collection);
        failure.JsonScope.Should().Be("$.classPeriods[*]");
    }
}

[TestFixture]
public class Given_StoredScopeState_targeting_collection_scope_When_ValidatingWriteContext
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

        // StoredScopeState points at the collection scope — should be rejected
        var collectionAddress = new ScopeInstanceAddress("$.classPeriods[*]", []);
        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(collectionAddress, ProfileVisibilityKind.Hidden, []),
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
    public void It_returns_ScopeKindMismatch_from_stored_stream()
    {
        _result.Should().ContainSingle(f => f is ScopeKindMismatchCoreBackendContractMismatchFailure);
        var failure = (ScopeKindMismatchCoreBackendContractMismatchFailure)
            _result.Single(f => f is ScopeKindMismatchCoreBackendContractMismatchFailure);
        failure.EmittedAddressKind.Should().Be(ScopeKind.NonCollection);
        failure.CompiledScopeKind.Should().Be(ScopeKind.Collection);
        failure.JsonScope.Should().Be("$.classPeriods[*]");
    }
}

[TestFixture]
public class Given_VisibleRequestCollectionItem_targeting_noncollection_scope_When_Validating
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
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var rowAddress = new CollectionRowAddress("$._ext.sample", rootAddress, []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    new ScopeInstanceAddress("$._ext.sample", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    rowAddress,
                    Creatable: false,
                    RequestJsonPath: "$._ext.sample"
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
    public void It_returns_ScopeKindMismatch_with_emitted_kind_Collection()
    {
        _result.Should().ContainSingle(f => f is ScopeKindMismatchCoreBackendContractMismatchFailure);
        var failure = (ScopeKindMismatchCoreBackendContractMismatchFailure)
            _result.Single(f => f is ScopeKindMismatchCoreBackendContractMismatchFailure);
        failure.EmittedAddressKind.Should().Be(ScopeKind.Collection);
        failure.CompiledScopeKind.Should().Be(ScopeKind.NonCollection);
    }
}

[TestFixture]
public class Given_VisibleStoredCollectionRow_targeting_noncollection_scope_When_ValidatingWriteContext
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
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var rowAddress = new CollectionRowAddress("$._ext.sample", rootAddress, []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    new ScopeInstanceAddress("$._ext.sample", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(
                    new ScopeInstanceAddress("$._ext.sample", []),
                    ProfileVisibilityKind.VisiblePresent,
                    []
                ),
            ],
            VisibleStoredCollectionRows: [new VisibleStoredCollectionRow(rowAddress, [])]
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
    public void It_returns_ScopeKindMismatch_from_stored_row_stream()
    {
        _result.Should().ContainSingle(f => f is ScopeKindMismatchCoreBackendContractMismatchFailure);
    }
}

[TestFixture]
public class Given_CollectionRow_with_wrong_immediate_parent_jsonscope_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        // Catalog: root, a non-collection extension at "$._ext.sample",
        // and a collection "$.classPeriods[*]" whose immediate parent is "$" (not "$._ext.sample").
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
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
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
        };

        // Collection row claims its parent is "$._ext.sample" — but the compiled scope's
        // ImmediateParentJsonScope is "$". Parent is catalog-valid but wrong.
        var wrongParentAddress = new ScopeInstanceAddress("$._ext.sample", []);
        var rowAddress = new CollectionRowAddress(
            "$.classPeriods[*]",
            wrongParentAddress,
            [new SemanticIdentityPart("classPeriodName", JsonValue.Create("period1"), IsPresent: true)]
        );

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    new ScopeInstanceAddress("$._ext.sample", []),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    rowAddress,
                    Creatable: false,
                    RequestJsonPath: "$.classPeriods[0]"
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
    public void It_returns_a_single_ParentScopeMismatch_failure()
    {
        _result.Should().ContainSingle(f => f is ParentScopeMismatchCoreBackendContractMismatchFailure);
    }

    [Test]
    public void It_reports_the_emitted_and_expected_parent_jsonscope()
    {
        var failure = (ParentScopeMismatchCoreBackendContractMismatchFailure)
            _result.Single(f => f is ParentScopeMismatchCoreBackendContractMismatchFailure);
        failure.EmittedParentJsonScope.Should().Be("$._ext.sample");
        failure.ExpectedParentJsonScope.Should().Be("$");
    }
}

[TestFixture]
public class Given_Duplicate_RequestScopeState_When_Validating
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
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
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
    public void It_returns_a_single_DuplicateScopeAddress_failure_with_occurrence_count_two()
    {
        _result.Should().HaveCount(1);
        _result[0].Should().BeOfType<DuplicateScopeAddressCoreBackendContractMismatchFailure>();
        var failure = (DuplicateScopeAddressCoreBackendContractMismatchFailure)_result[0];
        failure.StreamName.Should().Be("RequestScopeStates");
        failure.OccurrenceCount.Should().Be(2);
        failure.JsonScope.Should().Be("$");
    }
}

[TestFixture]
public class Given_Duplicate_VisibleRequestCollectionItem_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new("$", ScopeKind.Root, null, [], [], []),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var rowAddress = new CollectionRowAddress(
            "$.classPeriods[*]",
            rootAddress,
            [new SemanticIdentityPart("classPeriodName", JsonValue.Create("period1"), IsPresent: true)]
        );

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems:
            [
                new VisibleRequestCollectionItem(
                    rowAddress,
                    Creatable: false,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
                new VisibleRequestCollectionItem(
                    rowAddress,
                    Creatable: false,
                    RequestJsonPath: "$.classPeriods[1]"
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
    public void It_returns_a_single_duplicate_failure()
    {
        _result.Should().HaveCount(1);
        var failure = (DuplicateScopeAddressCoreBackendContractMismatchFailure)_result[0];
        failure.StreamName.Should().Be("VisibleRequestCollectionItems");
        failure.OccurrenceCount.Should().Be(2);
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
    }
}

[TestFixture]
public class Given_Duplicate_StoredScopeState_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new("$", ScopeKind.Root, null, [], [], []),
            new("$._ext.sample", ScopeKind.NonCollection, "$", [], [], []),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var extAddress = new ScopeInstanceAddress("$._ext.sample", []);

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
                new StoredScopeState(extAddress, ProfileVisibilityKind.Hidden, []),
                new StoredScopeState(extAddress, ProfileVisibilityKind.Hidden, []),
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
    public void It_returns_a_single_duplicate_failure_for_stored_stream()
    {
        _result.Should().HaveCount(1);
        var failure = (DuplicateScopeAddressCoreBackendContractMismatchFailure)_result[0];
        failure.StreamName.Should().Be("StoredScopeStates");
        failure.OccurrenceCount.Should().Be(2);
    }
}

[TestFixture]
public class Given_Duplicate_VisibleStoredCollectionRow_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new("$", ScopeKind.Root, null, [], [], []),
            new("$.classPeriods[*]", ScopeKind.Collection, "$", [], ["classPeriodName"], ["classPeriodName"]),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var rowAddress = new CollectionRowAddress(
            "$.classPeriods[*]",
            rootAddress,
            [new SemanticIdentityPart("classPeriodName", JsonValue.Create("p1"), IsPresent: true)]
        );

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
            StoredScopeStates: [new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, [])],
            VisibleStoredCollectionRows:
            [
                new VisibleStoredCollectionRow(rowAddress, []),
                new VisibleStoredCollectionRow(rowAddress, []),
            ]
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
    public void It_returns_a_single_duplicate_failure_for_stored_rows()
    {
        _result.Should().HaveCount(1);
        var failure = (DuplicateScopeAddressCoreBackendContractMismatchFailure)_result[0];
        failure.StreamName.Should().Be("VisibleStoredCollectionRows");
        failure.OccurrenceCount.Should().Be(2);
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
    }
}

[TestFixture]
public class Given_Duplicates_In_Multiple_Streams_When_ValidatingWriteContext
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new("$", ScopeKind.Root, null, [], [], []),
            new("$._ext.sample", ScopeKind.NonCollection, "$", [], [], []),
        };

        var rootAddress = new ScopeInstanceAddress("$", []);
        var extAddress = new ScopeInstanceAddress("$._ext.sample", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
            ],
            VisibleRequestCollectionItems: []
        );
        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(extAddress, ProfileVisibilityKind.Hidden, []),
                new StoredScopeState(extAddress, ProfileVisibilityKind.Hidden, []),
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
    public void It_returns_exactly_two_duplicate_failures_and_no_per_entry_failures()
    {
        _result.Should().HaveCount(2);
        _result.Should().AllBeOfType<DuplicateScopeAddressCoreBackendContractMismatchFailure>();
        var streamNames = _result
            .OfType<DuplicateScopeAddressCoreBackendContractMismatchFailure>()
            .Select(f => f.StreamName)
            .ToList();
        streamNames.Should().BeEquivalentTo("RequestScopeStates", "StoredScopeStates");
    }
}

[TestFixture]
public class Given_Duplicates_And_UnknownJsonScope_In_Same_Request_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new("$", ScopeKind.Root, null, [], [], []),
            // "$.unknown" intentionally NOT in the catalog.
        };

        var unknownAddress = new ScopeInstanceAddress("$.unknown", []);
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(unknownAddress, ProfileVisibilityKind.VisiblePresent, Creatable: false),
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
    public void It_returns_only_the_DuplicateScopeAddress_failure()
    {
        _result.Should().HaveCount(1);
        _result[0].Should().BeOfType<DuplicateScopeAddressCoreBackendContractMismatchFailure>();
    }
}

[TestFixture]
public class Given_SameJsonScope_Under_Different_AncestorCollectionInstances_When_Validating
{
    private ProfileFailure[] _result = null!;

    [SetUp]
    public void Setup()
    {
        var scopeCatalog = new List<CompiledScopeDescriptor>
        {
            new("$", ScopeKind.Root, null, [], [], []),
            new(
                JsonScope: "$.classPeriods[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
                CanonicalScopeRelativeMemberPaths: ["classPeriodName"]
            ),
            new(
                JsonScope: "$.classPeriods[*].meetingTimes[*]",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods[*]",
                CollectionAncestorsInOrder: ["$.classPeriods[*]"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: []
            ),
        };

        // Same JsonScope, different ancestor collection instances — NOT a duplicate.
        var ancestorA = new AncestorCollectionInstance(
            "$.classPeriods[*]",
            [new SemanticIdentityPart("classPeriodName", JsonValue.Create("periodA"), IsPresent: true)]
        );
        var ancestorB = new AncestorCollectionInstance(
            "$.classPeriods[*]",
            [new SemanticIdentityPart("classPeriodName", JsonValue.Create("periodB"), IsPresent: true)]
        );

        var addrA = new ScopeInstanceAddress("$.classPeriods[*].meetingTimes[*]", [ancestorA]);
        var addrB = new ScopeInstanceAddress("$.classPeriods[*].meetingTimes[*]", [ancestorB]);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(addrA, ProfileVisibilityKind.VisiblePresent, Creatable: false),
                new RequestScopeState(addrB, ProfileVisibilityKind.VisiblePresent, Creatable: false),
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
    public void It_does_not_emit_a_duplicate_failure()
    {
        _result.Should().NotContain(f => f is DuplicateScopeAddressCoreBackendContractMismatchFailure);
    }
}

[TestFixture]
public class Given_Complete_Request_And_Stored_NonCollection_Scope_Metadata_In_Multi_Table_Catalog_When_ValidatingWriteContext
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
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["campusCode"]
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

        var rootAddress = new ScopeInstanceAddress("$", []);
        var extensionAddress = new ScopeInstanceAddress("$._ext.sample", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    extensionAddress,
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
            StoredScopeStates:
            [
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(extensionAddress, ProfileVisibilityKind.VisibleAbsent, []),
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
public class Given_Missing_StoredScopeState_For_Extension_NonCollection_Scope_In_Multi_Table_Catalog_When_ValidatingWriteContext
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
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["campusCode"]
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

        var rootAddress = new ScopeInstanceAddress("$", []);
        var extensionAddress = new ScopeInstanceAddress("$._ext.sample", []);

        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    extensionAddress,
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        var context = new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: JsonNode.Parse("{}")!,
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
    public void It_returns_a_single_contract_mismatch_failure_for_the_missing_extension_scope()
    {
        _result.Should().ContainSingle();
        var failure = _result[0].Should().BeOfType<GenericCoreBackendContractMismatchFailure>().Subject;
        failure.Message.Should().Contain("StoredScopeStates").And.Contain("$._ext.sample");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .JsonScope.Should()
            .Be("$._ext.sample");
    }
}

[TestFixture]
public class Given_Missing_RequestScopeState_For_Extension_NonCollection_Scope_In_Multi_Table_Catalog_When_ValidatingWriteContext
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
                JsonScope: "$._ext.sample",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["campusCode"]
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

        var rootAddress = new ScopeInstanceAddress("$", []);
        var extensionAddress = new ScopeInstanceAddress("$._ext.sample", []);

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
                new StoredScopeState(rootAddress, ProfileVisibilityKind.VisiblePresent, []),
                new StoredScopeState(extensionAddress, ProfileVisibilityKind.VisibleAbsent, []),
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
    public void It_returns_a_single_contract_mismatch_failure_for_the_missing_extension_scope()
    {
        _result.Should().ContainSingle();
        var failure = _result[0].Should().BeOfType<GenericCoreBackendContractMismatchFailure>().Subject;
        failure.Message.Should().Contain("RequestScopeStates").And.Contain("$._ext.sample");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .JsonScope.Should()
            .Be("$._ext.sample");
    }
}
