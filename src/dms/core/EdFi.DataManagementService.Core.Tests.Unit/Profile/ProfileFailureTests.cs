// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileFailureTests
{
    protected static ProfileFailureContext BuildContext() =>
        new(
            ProfileName: "RestrictedStudentWrite",
            ResourceName: "StudentSchoolAssociation",
            Method: "PUT",
            Operation: "Update"
        );

    protected static ImmutableArray<ProfileFailureDiagnostic> BuildDiagnostics()
    {
        ScopeInstanceAddress rootAddress = new("$", []);
        SemanticIdentityPart semanticIdentityPart = new(
            RelativePath: "classPeriodName",
            Value: JsonValue.Create("First Period"),
            IsPresent: true
        );

        CollectionRowAddress rowAddress = new(
            JsonScope: "$.classPeriods[*]",
            ParentAddress: rootAddress,
            SemanticIdentityInOrder: [semanticIdentityPart]
        );

        CompiledScopeDescriptor compiledScope = new(
            JsonScope: "$.classPeriods[*]",
            ScopeKind: ScopeKind.Collection,
            ImmediateParentJsonScope: "$",
            CollectionAncestorsInOrder: [],
            SemanticIdentityRelativePathsInOrder: ["classPeriodName"],
            CanonicalScopeRelativeMemberPaths: ["classPeriodName", "officialAttendancePeriod"]
        );

        return
        [
            new ProfileFailureDiagnostic.Scope("$.classPeriods[*]", ScopeKind.Collection),
            new ProfileFailureDiagnostic.ScopeAddress(rootAddress),
            new ProfileFailureDiagnostic.CollectionRow(rowAddress),
            new ProfileFailureDiagnostic.SemanticIdentity([semanticIdentityPart]),
            new ProfileFailureDiagnostic.CanonicalMemberPaths([
                "classPeriodName",
                "officialAttendancePeriod",
            ]),
            new ProfileFailureDiagnostic.RequestPaths(["$.classPeriods[0].classPeriodName"]),
            new ProfileFailureDiagnostic.CompiledScope(compiledScope),
            new ProfileFailureDiagnostic.Binding("ClassPeriodName", "Column", "classPeriodName"),
        ];
    }

    protected static ScopeInstanceAddress BuildRootAddress() =>
        BuildDiagnostics().OfType<ProfileFailureDiagnostic.ScopeAddress>().Single().Address;

    protected static CollectionRowAddress BuildCollectionRowAddress() =>
        BuildDiagnostics().OfType<ProfileFailureDiagnostic.CollectionRow>().Single().Address;

    protected static CompiledScopeDescriptor BuildCompiledScope() =>
        BuildDiagnostics().OfType<ProfileFailureDiagnostic.CompiledScope>().Single().Descriptor;

    protected static ProfileFailure CreateFailure(
        ProfileFailureCategory category,
        ProfileFailureContext context,
        ImmutableArray<ProfileFailureDiagnostic> diagnostics
    ) =>
        category switch
        {
            ProfileFailureCategory.InvalidProfileDefinition => ProfileFailures.InvalidProfileDefinition(
                ProfileFailureEmitter.SemanticIdentityCompatibilityValidation,
                "Invalid profile definition",
                context,
                [.. diagnostics]
            ),
            ProfileFailureCategory.InvalidProfileUsage => ProfileFailures.InvalidProfileUsage(
                ProfileFailureEmitter.ProfileWritePipelineAssembly,
                "Invalid profile usage",
                context,
                [.. diagnostics]
            ),
            ProfileFailureCategory.WritableProfileValidationFailure =>
                ProfileFailures.WritableProfileValidationFailure(
                    ProfileFailureEmitter.RequestVisibilityAndWritableShaping,
                    "Writable profile validation failed",
                    context,
                    [.. diagnostics]
                ),
            ProfileFailureCategory.CreatabilityViolation => ProfileFailures.CreatabilityViolation(
                ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
                "Creatability violation",
                context,
                [.. diagnostics]
            ),
            ProfileFailureCategory.CoreBackendContractMismatch => ProfileFailures.CoreBackendContractMismatch(
                ProfileFailureEmitter.BackendProfileWriteContext,
                "Core/backend contract mismatch",
                context,
                [.. diagnostics]
            ),
            ProfileFailureCategory.BindingAccountingFailure => ProfileFailures.BindingAccountingFailure(
                ProfileFailureEmitter.ProfileErrorClassification,
                "Binding accounting failure",
                context,
                [.. diagnostics]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };
}

[TestFixture]
public class Given_ProfileFailureCategory
{
    [Test]
    public void It_should_define_exactly_six_categories()
    {
        Enum.GetValues<ProfileFailureCategory>()
            .Should()
            .Equal(
                ProfileFailureCategory.InvalidProfileDefinition,
                ProfileFailureCategory.InvalidProfileUsage,
                ProfileFailureCategory.WritableProfileValidationFailure,
                ProfileFailureCategory.CreatabilityViolation,
                ProfileFailureCategory.CoreBackendContractMismatch,
                ProfileFailureCategory.BindingAccountingFailure
            );
    }
}

[TestFixture]
public class Given_ProfileFailureFactories : ProfileFailureTests
{
    [TestCase(ProfileFailureCategory.InvalidProfileDefinition)]
    [TestCase(ProfileFailureCategory.InvalidProfileUsage)]
    [TestCase(ProfileFailureCategory.WritableProfileValidationFailure)]
    [TestCase(ProfileFailureCategory.CreatabilityViolation)]
    [TestCase(ProfileFailureCategory.CoreBackendContractMismatch)]
    [TestCase(ProfileFailureCategory.BindingAccountingFailure)]
    public void It_should_create_a_failure_with_the_expected_category(ProfileFailureCategory category)
    {
        ProfileFailureContext context = BuildContext();
        ImmutableArray<ProfileFailureDiagnostic> diagnostics = BuildDiagnostics();

        ProfileFailure failure = CreateFailure(category, context, diagnostics);

        failure.Category.Should().Be(category);
        failure.Context.Should().Be(context);
        failure.Diagnostics.Should().HaveCount(diagnostics.Length);
    }

    [Test]
    public void It_should_allow_writable_validation_failures_from_c3()
    {
        ProfileFailure failure = ProfileFailures.WritableProfileValidationFailure(
            ProfileFailureEmitter.RequestVisibilityAndWritableShaping,
            "Forbidden submitted member",
            BuildContext()
        );

        failure.Category.Should().Be(ProfileFailureCategory.WritableProfileValidationFailure);
        failure.Emitter.Should().Be(ProfileFailureEmitter.RequestVisibilityAndWritableShaping);
    }

    [Test]
    public void It_should_allow_writable_validation_failures_from_c4()
    {
        ProfileFailure failure = ProfileFailures.WritableProfileValidationFailure(
            ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
            "Duplicate visible collection item",
            BuildContext()
        );

        failure.Category.Should().Be(ProfileFailureCategory.WritableProfileValidationFailure);
        failure.Emitter.Should().Be(ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation);
    }

    [Test]
    public void It_should_create_a_generic_writable_profile_validation_failure()
    {
        WritableProfileValidationFailure failure = ProfileFailures.WritableProfileValidationFailure(
            ProfileFailureEmitter.RequestVisibilityAndWritableShaping,
            "Submitted data is forbidden by the writable profile.",
            BuildContext()
        );

        failure.Should().BeOfType<GenericWritableProfileValidationFailure>();
        failure.Category.Should().Be(ProfileFailureCategory.WritableProfileValidationFailure);
        failure.Emitter.Should().Be(ProfileFailureEmitter.RequestVisibilityAndWritableShaping);
    }

    [Test]
    public void It_should_create_a_generic_invalid_profile_definition_failure()
    {
        InvalidProfileDefinitionFailure failure = ProfileFailures.InvalidProfileDefinition(
            ProfileFailureEmitter.SemanticIdentityCompatibilityValidation,
            "Profile contains an unsupported structural rule.",
            BuildContext()
        );

        failure.Should().BeOfType<GenericInvalidProfileDefinitionFailure>();
        failure.Category.Should().Be(ProfileFailureCategory.InvalidProfileDefinition);
        failure.Emitter.Should().Be(ProfileFailureEmitter.SemanticIdentityCompatibilityValidation);
    }

    [Test]
    public void It_should_create_a_hidden_semantic_identity_definition_failure()
    {
        HiddenSemanticIdentityMembersProfileDefinitionFailure failure =
            ProfileFailures.HiddenSemanticIdentityMembers(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                jsonScope: "$.classPeriods[*]",
                hiddenCanonicalMemberPaths: ["classPeriodName", "meetingTime"]
            );

        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
        failure.Context.ResourceName.Should().Be("StudentSchoolAssociation");
        failure.JsonScope.Should().Be("$.classPeriods[*]");
        failure.HiddenCanonicalMemberPaths.Should().Equal("classPeriodName", "meetingTime");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .ScopeKind.Should()
            .Be(ScopeKind.Collection);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CanonicalMemberPaths>()
            .Single()
            .RelativePaths.Should()
            .Equal("classPeriodName", "meetingTime");
    }

    [Test]
    public void It_should_create_a_generic_invalid_profile_usage_failure()
    {
        InvalidProfileUsageFailure failure = ProfileFailures.InvalidProfileUsage(
            ProfileFailureEmitter.ProfileWritePipelineAssembly,
            "Profile usage is invalid for the current request.",
            BuildContext()
        );

        failure.Should().BeOfType<GenericInvalidProfileUsageFailure>();
        failure.Category.Should().Be(ProfileFailureCategory.InvalidProfileUsage);
        failure.Emitter.Should().Be(ProfileFailureEmitter.ProfileWritePipelineAssembly);
    }

    [Test]
    public void It_should_create_a_generic_creatability_violation_failure()
    {
        CreatabilityViolationFailure failure = ProfileFailures.CreatabilityViolation(
            ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
            "New visible scope is non-creatable.",
            BuildContext()
        );

        failure.Should().BeOfType<GenericCreatabilityViolationFailure>();
        failure.Category.Should().Be(ProfileFailureCategory.CreatabilityViolation);
        failure.Emitter.Should().Be(ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation);
    }

    [Test]
    public void It_should_create_a_root_create_rejected_when_non_creatable_failure()
    {
        ProfileFailureDiagnostic.CreatabilityDependency blockedDescendant = new(
            ProfileCreatabilityDependencyKind.RequiredVisibleDescendant,
            ProfileCreatabilityTargetKind.ExtensionScope,
            "$._ext.project.interventionPlan",
            ScopeKind.NonCollection,
            ExistsInStoredState: false,
            CreatableInRequest: false
        );

        RootCreateRejectedWhenNonCreatableCreatabilityViolationFailure failure =
            ProfileFailures.RootCreateRejectedWhenNonCreatable(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "POST",
                operation: "Create",
                hiddenCreationRequiredMemberPaths: ["schoolReference.schoolId"],
                missingCreationRequiredMemberPaths: ["entryDate"],
                dependencies: [blockedDescendant]
            );

        failure.Context.Method.Should().Be("POST");
        failure.Context.Operation.Should().Be("Create");
        failure.HiddenCreationRequiredMemberPaths.Should().Equal("schoolReference.schoolId");
        failure.MissingCreationRequiredMemberPaths.Should().Equal("entryDate");
        failure.Dependencies.Should().ContainSingle();
        failure.Dependencies[0].TargetKind.Should().Be(ProfileCreatabilityTargetKind.ExtensionScope);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .ScopeKind.Should()
            .Be(ScopeKind.Root);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.HiddenCreationRequiredMembers>()
            .Single()
            .RelativePaths.Should()
            .Equal("schoolReference.schoolId");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.MissingCreationRequiredMembers>()
            .Single()
            .RelativePaths.Should()
            .Equal("entryDate");
    }

    [TestCase(ProfileCreatabilityTargetKind.OneToOneScope, "$.calendarReference", "calendarTypeDescriptor")]
    [TestCase(
        ProfileCreatabilityTargetKind.NestedOrCommonTypeScope,
        "$.birthData",
        "countryOfBirthDescriptor"
    )]
    [TestCase(ProfileCreatabilityTargetKind.ExtensionScope, "$._ext.project.interventionPlan", "beginDate")]
    public void It_should_create_a_non_collection_visible_scope_insert_rejected_when_non_creatable_failure(
        ProfileCreatabilityTargetKind targetKind,
        string jsonScope,
        string hiddenCreationRequiredMemberPath
    )
    {
        ScopeInstanceAddress affectedAddress = new(jsonScope, []);

        VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure failure =
            ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                targetKind: targetKind,
                affectedAddress: affectedAddress,
                hiddenCreationRequiredMemberPaths: [hiddenCreationRequiredMemberPath],
                missingCreationRequiredMemberPaths: []
            );

        failure.JsonScope.Should().Be(jsonScope);
        failure.AffectedScopeKind.Should().Be(ScopeKind.NonCollection);
        failure.TargetKind.Should().Be(targetKind);
        failure.HiddenCreationRequiredMemberPaths.Should().Equal(hiddenCreationRequiredMemberPath);
        failure.MissingCreationRequiredMemberPaths.Should().BeEmpty();
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .JsonScope.Should()
            .Be(jsonScope);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.JsonScope.Should()
            .Be(jsonScope);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.HiddenCreationRequiredMembers>()
            .Single()
            .RelativePaths.Should()
            .Equal(hiddenCreationRequiredMemberPath);
    }

    [Test]
    public void It_should_create_a_collection_item_insert_rejected_when_non_creatable_failure()
    {
        SemanticIdentityPart sessionIdentity = new(
            RelativePath: "sessionName",
            Value: JsonValue.Create("Summer"),
            IsPresent: true
        );

        CollectionRowAddress affectedAddress = new(
            JsonScope: "$.sessions[*]",
            ParentAddress: BuildRootAddress(),
            SemanticIdentityInOrder: [sessionIdentity]
        );

        VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure failure =
            ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                targetKind: ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem,
                affectedAddress: affectedAddress,
                hiddenCreationRequiredMemberPaths: ["startDate"],
                missingCreationRequiredMemberPaths: ["sessionName"]
            );

        failure.JsonScope.Should().Be("$.sessions[*]");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.TargetKind.Should().Be(ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem);
        failure.HiddenCreationRequiredMemberPaths.Should().Equal("startDate");
        failure.MissingCreationRequiredMemberPaths.Should().Equal("sessionName");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$.sessions[*]");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.SemanticIdentity>()
            .Single()
            .PartsInOrder[0]
            .RelativePath.Should()
            .Be("sessionName");
    }

    [Test]
    public void It_should_create_an_extension_collection_item_insert_rejected_when_non_creatable_failure()
    {
        SemanticIdentityPart programIdentity = new(
            RelativePath: "programName",
            Value: JsonValue.Create("Tutoring"),
            IsPresent: true
        );

        SemanticIdentityPart serviceIdentity = new(
            RelativePath: "serviceDescriptor",
            Value: JsonValue.Create("Counseling"),
            IsPresent: true
        );

        ScopeInstanceAddress parentAddress = new(
            JsonScope: "$.programs[*]._ext.project",
            AncestorCollectionInstances: [new AncestorCollectionInstance("$.programs[*]", [programIdentity])]
        );

        CollectionRowAddress affectedAddress = new(
            JsonScope: "$.programs[*]._ext.project.services[*]",
            ParentAddress: parentAddress,
            SemanticIdentityInOrder: [serviceIdentity]
        );

        VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure failure =
            ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                targetKind: ProfileCreatabilityTargetKind.ExtensionCollectionItem,
                affectedAddress: affectedAddress,
                hiddenCreationRequiredMemberPaths: ["serviceDescriptor"],
                missingCreationRequiredMemberPaths: []
            );

        failure.JsonScope.Should().Be("$.programs[*]._ext.project.services[*]");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.TargetKind.Should().Be(ProfileCreatabilityTargetKind.ExtensionCollectionItem);
        failure.HiddenCreationRequiredMemberPaths.Should().Equal("serviceDescriptor");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.AncestorCollectionInstances.Should()
            .ContainSingle();
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>()
            .Single()
            .Address.ParentAddress.JsonScope.Should()
            .Be("$.programs[*]._ext.project");
    }

    [Test]
    public void It_should_capture_the_three_level_parent_and_descendant_create_denial_chain()
    {
        SemanticIdentityPart programIdentity = new(
            RelativePath: "programName",
            Value: JsonValue.Create("Mentoring"),
            IsPresent: true
        );

        CollectionRowAddress middleLevelAddress = new(
            JsonScope: "$.programs[*]",
            ParentAddress: BuildRootAddress(),
            SemanticIdentityInOrder: [programIdentity]
        );

        ProfileFailureDiagnostic.CreatabilityDependency blockedDescendant = new(
            ProfileCreatabilityDependencyKind.RequiredVisibleDescendant,
            ProfileCreatabilityTargetKind.ExtensionCollectionItem,
            "$.programs[*]._ext.project.services[*]",
            ScopeKind.Collection,
            ExistsInStoredState: false,
            CreatableInRequest: false
        );

        VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure middleLevelFailure =
            ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                targetKind: ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem,
                affectedAddress: middleLevelAddress,
                hiddenCreationRequiredMemberPaths: ["beginDate"],
                missingCreationRequiredMemberPaths: [],
                dependencies: [blockedDescendant]
            );

        SemanticIdentityPart serviceIdentity = new(
            RelativePath: "serviceDescriptor",
            Value: JsonValue.Create("Coaching"),
            IsPresent: true
        );

        ScopeInstanceAddress childParentAddress = new(
            JsonScope: "$.programs[*]._ext.project",
            AncestorCollectionInstances: [new AncestorCollectionInstance("$.programs[*]", [programIdentity])]
        );

        CollectionRowAddress childAddress = new(
            JsonScope: "$.programs[*]._ext.project.services[*]",
            ParentAddress: childParentAddress,
            SemanticIdentityInOrder: [serviceIdentity]
        );

        ProfileFailureDiagnostic.CreatabilityDependency blockedParent = new(
            ProfileCreatabilityDependencyKind.ImmediateVisibleParent,
            ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem,
            "$.programs[*]",
            ScopeKind.Collection,
            ExistsInStoredState: false,
            CreatableInRequest: false
        );

        VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure childFailure =
            ProfileFailures.VisibleScopeOrItemInsertRejectedWhenNonCreatable(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                targetKind: ProfileCreatabilityTargetKind.ExtensionCollectionItem,
                affectedAddress: childAddress,
                hiddenCreationRequiredMemberPaths: [],
                missingCreationRequiredMemberPaths: [],
                dependencies: [blockedParent]
            );

        middleLevelFailure.Dependencies.Should().ContainSingle();
        middleLevelFailure
            .Dependencies[0]
            .DependencyKind.Should()
            .Be(ProfileCreatabilityDependencyKind.RequiredVisibleDescendant);
        middleLevelFailure.Dependencies[0].JsonScope.Should().Be("$.programs[*]._ext.project.services[*]");
        childFailure.Dependencies.Should().ContainSingle();
        childFailure
            .Dependencies[0]
            .DependencyKind.Should()
            .Be(ProfileCreatabilityDependencyKind.ImmediateVisibleParent);
        childFailure.Dependencies[0].JsonScope.Should().Be("$.programs[*]");
        childFailure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CreatabilityDependency>()
            .Single()
            .DependencyKind.Should()
            .Be(ProfileCreatabilityDependencyKind.ImmediateVisibleParent);
    }

    [Test]
    public void It_should_create_a_generic_core_backend_contract_mismatch_failure()
    {
        CoreBackendContractMismatchFailure failure = ProfileFailures.CoreBackendContractMismatch(
            ProfileFailureEmitter.BackendProfileWriteContext,
            "Stored-side metadata could not be aligned.",
            BuildContext()
        );

        failure.Should().BeOfType<GenericCoreBackendContractMismatchFailure>();
        failure.Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
        failure.Emitter.Should().Be(ProfileFailureEmitter.BackendProfileWriteContext);
    }

    [Test]
    public void It_should_create_an_unknown_json_scope_contract_mismatch_failure()
    {
        UnknownJsonScopeCoreBackendContractMismatchFailure failure = ProfileFailures.UnknownJsonScope(
            profileName: "RestrictedStudentWrite",
            resourceName: "StudentSchoolAssociation",
            method: "PUT",
            operation: "Update",
            jsonScope: "$.unknownScope[*]",
            expectedScopeKind: ScopeKind.Collection
        );

        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
        failure.Context.ResourceName.Should().Be("StudentSchoolAssociation");
        failure.JsonScope.Should().Be("$.unknownScope[*]");
        failure.ExpectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.Emitter.Should().Be(ProfileFailureEmitter.BackendProfileWriteContext);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .JsonScope.Should()
            .Be("$.unknownScope[*]");
    }

    [Test]
    public void It_should_create_an_ancestor_chain_contract_mismatch_failure()
    {
        SemanticIdentityPart ancestorIdentity = new(
            RelativePath: "classPeriodName",
            Value: JsonValue.Create("First Period"),
            IsPresent: true
        );

        AncestorCollectionInstance emittedAncestor = new("$.classPeriods[*]", [ancestorIdentity]);
        ScopeInstanceAddress emittedAddress = new("$.classPeriods[*].attendanceEvent", [emittedAncestor]);
        CompiledScopeDescriptor compiledScope = new(
            JsonScope: "$.classPeriods[*].attendanceEvent",
            ScopeKind: ScopeKind.NonCollection,
            ImmediateParentJsonScope: "$.classPeriods[*]",
            CollectionAncestorsInOrder: ["$.sections[*]"],
            SemanticIdentityRelativePathsInOrder: [],
            CanonicalScopeRelativeMemberPaths: ["eventType"]
        );

        AncestorChainMismatchCoreBackendContractMismatchFailure failure =
            ProfileFailures.AncestorChainMismatch(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                compiledScope: compiledScope,
                emittedAddress: emittedAddress
            );

        failure.JsonScope.Should().Be("$.classPeriods[*].attendanceEvent");
        failure.AffectedScopeKind.Should().Be(ScopeKind.NonCollection);
        failure.EmittedAncestorCollectionInstances.Should().ContainSingle();
        failure.EmittedAncestorCollectionInstances[0].JsonScope.Should().Be("$.classPeriods[*]");
        failure.ExpectedAncestorJsonScopesInOrder.Should().Equal("$.sections[*]");
        failure.Emitter.Should().Be(ProfileFailureEmitter.BackendProfileWriteContext);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$.classPeriods[*].attendanceEvent");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CompiledScope>()
            .Single()
            .Descriptor.CollectionAncestorsInOrder.Should()
            .Equal("$.sections[*]");
    }

    [Test]
    public void It_should_create_a_canonical_member_path_contract_mismatch_failure()
    {
        CanonicalMemberPathMismatchCoreBackendContractMismatchFailure failure =
            ProfileFailures.CanonicalMemberPathMismatch(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                compiledScope: BuildCompiledScope(),
                emittedAddress: BuildCollectionRowAddress(),
                emittedCanonicalMemberPaths: ["classPeriodName", "unsupportedPath"]
            );

        failure.JsonScope.Should().Be("$.classPeriods[*]");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.EmittedCanonicalMemberPaths.Should().Equal("classPeriodName", "unsupportedPath");
        failure.AllowedCanonicalMemberPaths.Should().Equal("classPeriodName", "officialAttendancePeriod");
        failure.Emitter.Should().Be(ProfileFailureEmitter.BackendProfileWriteContext);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$.classPeriods[*]");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CanonicalMemberPaths>()
            .Single()
            .RelativePaths.Should()
            .Equal("classPeriodName", "officialAttendancePeriod");
    }

    [Test]
    public void It_should_create_an_unalignable_stored_visibility_metadata_failure()
    {
        UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure failure =
            ProfileFailures.UnalignableStoredVisibilityMetadata(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                compiledScope: BuildCompiledScope(),
                emittedAddress: BuildCollectionRowAddress(),
                metadataKind: "VisibleStoredCollectionRow",
                hiddenCanonicalMemberPaths: ["officialAttendancePeriod"]
            );

        failure.JsonScope.Should().Be("$.classPeriods[*]");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.MetadataKind.Should().Be("VisibleStoredCollectionRow");
        failure.HiddenCanonicalMemberPaths.Should().Equal("officialAttendancePeriod");
        failure.Emitter.Should().Be(ProfileFailureEmitter.BackendProfileWriteContext);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$.classPeriods[*]");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CanonicalMemberPaths>()
            .Single()
            .RelativePaths.Should()
            .Equal("officialAttendancePeriod");
    }

    [Test]
    public void It_should_create_a_generic_binding_accounting_failure()
    {
        BindingAccountingProfileFailure failure = ProfileFailures.BindingAccountingFailure(
            ProfileFailureEmitter.ProfileErrorClassification,
            "Binding accounting failed.",
            BuildContext()
        );

        failure.Should().BeOfType<GenericBindingAccountingProfileFailure>();
        failure.Category.Should().Be(ProfileFailureCategory.BindingAccountingFailure);
        failure.Emitter.Should().Be(ProfileFailureEmitter.ProfileErrorClassification);
    }

    [Test]
    public void It_should_create_an_unclassified_binding_accounting_failure()
    {
        UnclassifiedProfileBindingAccountingFailure failure = ProfileFailures.UnclassifiedBindingAccounting(
            profileName: "RestrictedStudentWrite",
            resourceName: "StudentSchoolAssociation",
            method: "PUT",
            operation: "Update",
            compiledScope: BuildCompiledScope(),
            emittedAddress: BuildCollectionRowAddress(),
            bindingName: "OfficialAttendancePeriod",
            bindingKind: "Column",
            canonicalMemberPath: "officialAttendancePeriod"
        );

        failure.JsonScope.Should().Be("$.classPeriods[*]");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.BindingName.Should().Be("OfficialAttendancePeriod");
        failure.BindingKind.Should().Be("Column");
        failure.CanonicalMemberPath.Should().Be("officialAttendancePeriod");
        failure.Emitter.Should().Be(ProfileFailureEmitter.ProfileErrorClassification);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Binding>()
            .Single()
            .CanonicalMemberPath.Should()
            .Be("officialAttendancePeriod");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$.classPeriods[*]");
    }

    [Test]
    public void It_should_create_a_multiply_classified_binding_accounting_failure()
    {
        CompiledScopeDescriptor rootCompiledScope = new(
            JsonScope: "$",
            ScopeKind: ScopeKind.Root,
            ImmediateParentJsonScope: null,
            CollectionAncestorsInOrder: [],
            SemanticIdentityRelativePathsInOrder: [],
            CanonicalScopeRelativeMemberPaths: ["studentUniqueId", "firstName"]
        );

        MultiplyClassifiedProfileBindingAccountingFailure failure =
            ProfileFailures.MultiplyClassifiedBindingAccounting(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                compiledScope: rootCompiledScope,
                emittedAddress: BuildRootAddress(),
                bindingName: "StudentUniqueId",
                bindingKind: "Column",
                canonicalMemberPath: "studentUniqueId",
                assignedDispositions:
                [
                    ProfileBindingDisposition.VisibleWritable,
                    ProfileBindingDisposition.HiddenPreserved,
                ]
            );

        failure.JsonScope.Should().Be("$");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Root);
        failure.BindingName.Should().Be("StudentUniqueId");
        failure.BindingKind.Should().Be("Column");
        failure.CanonicalMemberPath.Should().Be("studentUniqueId");
        failure
            .AssignedDispositions.Should()
            .Equal(ProfileBindingDisposition.VisibleWritable, ProfileBindingDisposition.HiddenPreserved);
        failure.Emitter.Should().Be(ProfileFailureEmitter.ProfileErrorClassification);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Binding>()
            .Single()
            .BindingName.Should()
            .Be("StudentUniqueId");
    }

    [Test]
    public void It_should_create_a_profile_mode_mismatch_failure()
    {
        ProfileModeMismatchProfileUsageFailure failure = ProfileFailures.ProfileModeMismatch(
            profileName: "RestrictedStudentWrite",
            resourceName: "StudentSchoolAssociation",
            method: "PUT",
            operation: "Upsert",
            expectedUsage: "Writable",
            actualUsage: "Readable"
        );

        failure.ExpectedUsage.Should().Be("Writable");
        failure.ActualUsage.Should().Be("Readable");
        failure.Context.Method.Should().Be("PUT");
        failure.Context.Operation.Should().Be("Upsert");
        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
    }

    [Test]
    public void It_should_create_a_profile_not_found_for_resource_failure()
    {
        ProfileNotFoundForResourceProfileUsageFailure failure = ProfileFailures.ProfileNotFoundForResource(
            profileName: "RestrictedStudentWrite",
            resourceName: "DisciplineAction",
            method: "POST",
            operation: "Create"
        );

        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
        failure.Context.ResourceName.Should().Be("DisciplineAction");
        failure.Context.Method.Should().Be("POST");
        failure.Context.Operation.Should().Be("Create");
        failure.Message.Should().Contain("does not define the requested resource");
    }

    [Test]
    public void It_should_create_an_unsupported_operation_failure()
    {
        UnsupportedOperationProfileUsageFailure failure = ProfileFailures.UnsupportedOperation(
            profileName: "RestrictedStudentWrite",
            resourceName: "StudentSchoolAssociation",
            method: "DELETE",
            operation: "Delete",
            unsupportedOperation: "Delete"
        );

        failure.UnsupportedOperation.Should().Be("Delete");
        failure.Context.Method.Should().Be("DELETE");
        failure.Context.Operation.Should().Be("Delete");
        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
    }

    [Test]
    public void It_should_create_a_forbidden_submitted_data_failure()
    {
        ForbiddenSubmittedDataWritableProfileValidationFailure failure =
            ProfileFailures.ForbiddenSubmittedData(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                jsonScope: "$.classPeriods[*]",
                scopeKind: ScopeKind.Collection,
                requestJsonPaths: ["$.classPeriods[0].officialAttendancePeriod"],
                forbiddenCanonicalMemberPaths: ["officialAttendancePeriod"]
            );

        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
        failure.Context.ResourceName.Should().Be("StudentSchoolAssociation");
        failure.Context.Method.Should().Be("PUT");
        failure.Context.Operation.Should().Be("Update");
        failure.JsonScope.Should().Be("$.classPeriods[*]");
        failure.AffectedScopeKind.Should().Be(ScopeKind.Collection);
        failure.RequestJsonPaths.Should().Equal("$.classPeriods[0].officialAttendancePeriod");
        failure.ForbiddenCanonicalMemberPaths.Should().Equal("officialAttendancePeriod");
        failure.Emitter.Should().Be(ProfileFailureEmitter.RequestVisibilityAndWritableShaping);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .ScopeKind.Should()
            .Be(ScopeKind.Collection);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.RequestPaths>()
            .Single()
            .JsonPaths.Should()
            .Equal("$.classPeriods[0].officialAttendancePeriod");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CanonicalMemberPaths>()
            .Single()
            .RelativePaths.Should()
            .Equal("officialAttendancePeriod");
    }

    [Test]
    public void It_should_allow_forbidden_submitted_value_failures_without_canonical_member_paths()
    {
        ForbiddenSubmittedDataWritableProfileValidationFailure failure =
            ProfileFailures.ForbiddenSubmittedData(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                jsonScope: "$.classPeriods[*]",
                scopeKind: ScopeKind.Collection,
                requestJsonPaths: ["$.classPeriods[0]"],
                forbiddenCanonicalMemberPaths: []
            );

        failure.ForbiddenCanonicalMemberPaths.Should().BeEmpty();
        failure.Diagnostics.OfType<ProfileFailureDiagnostic.CanonicalMemberPaths>().Should().BeEmpty();
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.RequestPaths>()
            .Single()
            .JsonPaths.Should()
            .Equal("$.classPeriods[0]");
    }

    [Test]
    public void It_should_create_a_duplicate_visible_collection_item_collision_failure()
    {
        ScopeInstanceAddress stableParentAddress = new("$", []);
        SemanticIdentityPart semanticIdentityPart = new(
            RelativePath: "classPeriodName",
            Value: JsonValue.Create("First Period"),
            IsPresent: true
        );

        DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure failure =
            ProfileFailures.DuplicateVisibleCollectionItemCollision(
                profileName: "RestrictedStudentWrite",
                resourceName: "StudentSchoolAssociation",
                method: "PUT",
                operation: "Update",
                jsonScope: "$.classPeriods[*]",
                stableParentAddress: stableParentAddress,
                semanticIdentityPartsInOrder: [semanticIdentityPart],
                requestJsonPaths: ["$.classPeriods[0]", "$.classPeriods[1]"]
            );

        failure.Context.ProfileName.Should().Be("RestrictedStudentWrite");
        failure.JsonScope.Should().Be("$.classPeriods[*]");
        failure.StableParentAddress.JsonScope.Should().Be("$");
        failure.SemanticIdentityPartsInOrder.Should().ContainSingle();
        failure.SemanticIdentityPartsInOrder[0].RelativePath.Should().Be("classPeriodName");
        failure.RequestJsonPaths.Should().Equal("$.classPeriods[0]", "$.classPeriods[1]");
        failure.Emitter.Should().Be(ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.Scope>()
            .Single()
            .ScopeKind.Should()
            .Be(ScopeKind.Collection);
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>()
            .Single()
            .Address.JsonScope.Should()
            .Be("$");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>()
            .Single()
            .Address.SemanticIdentityInOrder.Should()
            .ContainSingle();
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.SemanticIdentity>()
            .Single()
            .PartsInOrder[0]
            .RelativePath.Should()
            .Be("classPeriodName");
        failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.RequestPaths>()
            .Single()
            .JsonPaths.Should()
            .Equal("$.classPeriods[0]", "$.classPeriods[1]");
    }
}

[TestFixture]
public class Given_ProfileFailureWithSharedDiagnostics : ProfileFailureTests
{
    private ProfileFailure _failure = null!;

    [SetUp]
    public void Setup()
    {
        _failure = ProfileFailures.CreatabilityViolation(
            ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
            "Visible collection item insert rejected when non-creatable",
            BuildContext(),
            [.. BuildDiagnostics()]
        );
    }

    [Test]
    public void It_should_preserve_scope_diagnostic_details()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.Scope>().Single();

        diagnostic.JsonScope.Should().Be("$.classPeriods[*]");
        diagnostic.ScopeKind.Should().Be(ScopeKind.Collection);
    }

    [Test]
    public void It_should_preserve_scope_address_details()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.ScopeAddress>().Single();

        diagnostic.Address.JsonScope.Should().Be("$");
        diagnostic.Address.AncestorCollectionInstances.Should().BeEmpty();
    }

    [Test]
    public void It_should_preserve_collection_row_details()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.CollectionRow>().Single();

        diagnostic.Address.JsonScope.Should().Be("$.classPeriods[*]");
        diagnostic.Address.ParentAddress.JsonScope.Should().Be("$");
        diagnostic.Address.SemanticIdentityInOrder.Should().ContainSingle();
    }

    [Test]
    public void It_should_preserve_semantic_identity_details()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.SemanticIdentity>().Single();

        diagnostic.PartsInOrder.Should().ContainSingle();
        diagnostic.PartsInOrder[0].RelativePath.Should().Be("classPeriodName");
        diagnostic.PartsInOrder[0].Value!.ToString().Should().Be("First Period");
        diagnostic.PartsInOrder[0].IsPresent.Should().BeTrue();
    }

    [Test]
    public void It_should_preserve_canonical_member_paths()
    {
        var diagnostic = _failure
            .Diagnostics.OfType<ProfileFailureDiagnostic.CanonicalMemberPaths>()
            .Single();

        diagnostic.RelativePaths.Should().Equal("classPeriodName", "officialAttendancePeriod");
    }

    [Test]
    public void It_should_preserve_request_paths()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.RequestPaths>().Single();

        diagnostic.JsonPaths.Should().Equal("$.classPeriods[0].classPeriodName");
    }

    [Test]
    public void It_should_preserve_compiled_scope_details()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.CompiledScope>().Single();

        diagnostic.Descriptor.JsonScope.Should().Be("$.classPeriods[*]");
        diagnostic.Descriptor.ScopeKind.Should().Be(ScopeKind.Collection);
        diagnostic.Descriptor.ImmediateParentJsonScope.Should().Be("$");
    }

    [Test]
    public void It_should_preserve_binding_details()
    {
        var diagnostic = _failure.Diagnostics.OfType<ProfileFailureDiagnostic.Binding>().Single();

        diagnostic.BindingName.Should().Be("ClassPeriodName");
        diagnostic.BindingKind.Should().Be("Column");
        diagnostic.CanonicalMemberPath.Should().Be("classPeriodName");
    }
}

[TestFixture]
public class Given_ProfileFailureFactoryWhenEmitterDoesNotOwnTheCategory : ProfileFailureTests
{
    [Test]
    public void It_should_throw_an_argument_exception()
    {
        Action act = () =>
            ProfileFailures.InvalidProfileDefinition(
                ProfileFailureEmitter.ProfileWritePipelineAssembly,
                "Invalid profile definition",
                BuildContext()
            );

        act.Should().Throw<ArgumentException>().WithMessage("*does not own category*");
    }
}
