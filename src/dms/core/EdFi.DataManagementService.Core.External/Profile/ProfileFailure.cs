// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Shared category discriminator for typed profile write failures.
/// The profile error classification layer owns any later mapping from these
/// internal contract types to client-visible HTTP/problem-details shapes.
/// </summary>
public enum ProfileFailureCategory
{
    /// <summary>
    /// Category 1. Emitted when a writable profile definition is invalid
    /// before runtime execution begins.
    /// </summary>
    InvalidProfileDefinition,

    /// <summary>
    /// Category 2. Emitted when the resolved profile is used with an
    /// unsupported request mode or operation.
    /// </summary>
    InvalidProfileUsage,

    /// <summary>
    /// Category 3. Emitted when submitted data is forbidden by the writable
    /// profile or collides after shaping.
    /// </summary>
    WritableProfileValidationFailure,

    /// <summary>
    /// Category 4. Emitted when a request attempts to create new visible data
    /// that the writable profile does not allow.
    /// </summary>
    CreatabilityViolation,

    /// <summary>
    /// Category 5. Emitted when backend profile write integration cannot
    /// align Core-emitted profile metadata with compiled backend scope state.
    /// </summary>
    CoreBackendContractMismatch,

    /// <summary>
    /// Category 6. Emitted when a profiled binding cannot be classified
    /// deterministically during failure mapping/accounting.
    /// </summary>
    BindingAccountingFailure,
}

/// <summary>
/// Identifies the pipeline-stage emission point for a typed profile failure.
/// Factory entry points validate that callers only use emitters that own the
/// requested category.
/// </summary>
public enum ProfileFailureEmitter
{
    /// <summary>
    /// Semantic identity compatibility validation.
    /// Owns category 1.
    /// </summary>
    SemanticIdentityCompatibilityValidation,

    /// <summary>
    /// Request-side visibility classification and writable request shaping.
    /// Owns category 3.
    /// </summary>
    RequestVisibilityAndWritableShaping,

    /// <summary>
    /// Request-side creatability analysis and duplicate visible
    /// collection-item validation.
    /// Owns categories 3 and 4.
    /// </summary>
    RequestCreatabilityAndDuplicateValidation,

    /// <summary>
    /// Profile write pipeline orchestration and profile-mode validation.
    /// Owns category 2.
    /// </summary>
    ProfileWritePipelineAssembly,

    /// <summary>
    /// Backend profile write-context integration.
    /// Owns category 5.
    /// </summary>
    BackendProfileWriteContext,

    /// <summary>
    /// Profile failure classification and public API mapping.
    /// Owns category 6 and the eventual client-visible translation layer.
    /// </summary>
    ProfileErrorClassification,
}

/// <summary>
/// The exactly-one binding outcome backend must prove for each profiled binding
/// before DML proceeds.
/// </summary>
public enum ProfileBindingDisposition
{
    /// <summary>
    /// Binding is visible to the active writable profile and updated from the
    /// visible request/resource state.
    /// </summary>
    VisibleWritable,

    /// <summary>
    /// Binding is governed by hidden members and preserved from stored state.
    /// </summary>
    HiddenPreserved,

    /// <summary>
    /// Binding is cleared because the scope is visible but absent.
    /// </summary>
    ClearOnVisibleAbsent,

    /// <summary>
    /// Binding is owned by executor/storage mechanics rather than profile
    /// visibility.
    /// </summary>
    StorageManaged,
}

/// <summary>
/// Distinguishes the concrete create target rejected by a category-4
/// creatability violation.
/// </summary>
public enum ProfileCreatabilityTargetKind
{
    /// <summary>
    /// New visible root resource instance.
    /// </summary>
    RootResource,

    /// <summary>
    /// New visible 1:1 non-collection scope.
    /// </summary>
    OneToOneScope,

    /// <summary>
    /// New visible nested/common-type non-collection scope.
    /// </summary>
    NestedOrCommonTypeScope,

    /// <summary>
    /// New visible collection/common-type item.
    /// </summary>
    CollectionOrCommonTypeItem,

    /// <summary>
    /// New visible extension scope.
    /// </summary>
    ExtensionScope,

    /// <summary>
    /// New visible extension collection item.
    /// </summary>
    ExtensionCollectionItem,
}

/// <summary>
/// Describes why a related parent/descendant target appears in a creatability
/// diagnostic chain.
/// </summary>
public enum ProfileCreatabilityDependencyKind
{
    /// <summary>
    /// The current create attempt is blocked because its immediate visible
    /// parent neither exists nor is creatable in this request.
    /// </summary>
    ImmediateVisibleParent,

    /// <summary>
    /// The current create attempt is blocked because a required visible
    /// descendant that must co-create is not creatable.
    /// </summary>
    RequiredVisibleDescendant,
}

/// <summary>
/// Shared high-level context carried by all typed profile failures.
/// Later category-specific payloads add scope, address, and binding detail via
/// <see cref="ProfileFailureDiagnostic"/> instances rather than redefining the
/// common profile/request identity surface.
/// </summary>
/// <param name="ProfileName">Writable/readable profile name when one is resolved.</param>
/// <param name="ResourceName">Affected resource name when known.</param>
/// <param name="Method">HTTP method or equivalent request method label.</param>
/// <param name="Operation">Optional higher-level operation label.</param>
public sealed record ProfileFailureContext(
    string? ProfileName,
    string? ResourceName,
    string? Method,
    string? Operation
)
{
    public static ProfileFailureContext Empty { get; } = new(null, null, null, null);
}

/// <summary>
/// Reusable diagnostic primitives shared by the typed profile failure contract.
/// Specific category leaves can compose these diagnostics without redefining
/// core scope/address/member/binding payload shapes.
/// </summary>
public abstract record ProfileFailureDiagnostic
{
    /// <summary>
    /// Compiled scope identifier and shape classification.
    /// Used across categories 1, 3, 4, 5, and 6.
    /// </summary>
    public sealed record Scope(string JsonScope, ScopeKind ScopeKind) : ProfileFailureDiagnostic;

    /// <summary>
    /// Address for a visible or affected non-collection scope instance.
    /// Used primarily by categories 4, 5, and 6.
    /// </summary>
    public sealed record ScopeAddress(ScopeInstanceAddress Address) : ProfileFailureDiagnostic;

    /// <summary>
    /// Address for a visible or affected collection row.
    /// Used primarily by categories 3, 4, 5, and 6.
    /// </summary>
    public sealed record CollectionRow(CollectionRowAddress Address) : ProfileFailureDiagnostic;

    /// <summary>
    /// Ordered compiled semantic identity parts.
    /// Used by duplicate-validation, creatability, and backend contract
    /// mismatch diagnostics.
    /// </summary>
    public sealed record SemanticIdentity(ImmutableArray<SemanticIdentityPart> PartsInOrder)
        : ProfileFailureDiagnostic;

    /// <summary>
    /// Canonical scope-relative member paths published by the compiled adapter.
    /// Used for hidden/required member reporting and canonical-path mismatch
    /// diagnostics.
    /// </summary>
    public sealed record CanonicalMemberPaths(ImmutableArray<string> RelativePaths)
        : ProfileFailureDiagnostic;

    /// <summary>
    /// Creation-required member paths hidden by the writable profile.
    /// Used by creatability violations.
    /// </summary>
    public sealed record HiddenCreationRequiredMembers(ImmutableArray<string> RelativePaths)
        : ProfileFailureDiagnostic;

    /// <summary>
    /// Request JSON paths associated with forbidden submitted data.
    /// Used by request-side validation failures.
    /// </summary>
    public sealed record RequestPaths(ImmutableArray<string> JsonPaths) : ProfileFailureDiagnostic;

    /// <summary>
    /// Full compiled-scope metadata when a failure needs the adapter-published
    /// scope contract, such as backend/runtime alignment failures.
    /// </summary>
    public sealed record CompiledScope(CompiledScopeDescriptor Descriptor) : ProfileFailureDiagnostic;

    /// <summary>
    /// Binding-level metadata for executor classification/accounting failures.
    /// </summary>
    public sealed record Binding(string BindingName, string BindingKind, string? CanonicalMemberPath)
        : ProfileFailureDiagnostic;

    /// <summary>
    /// Related parent/descendant creatability state that participates in a
    /// top-down or bottom-up create rejection chain.
    /// </summary>
    public sealed record CreatabilityDependency(
        ProfileCreatabilityDependencyKind DependencyKind,
        ProfileCreatabilityTargetKind TargetKind,
        string JsonScope,
        ScopeKind ScopeKind,
        bool ExistsInStoredState,
        bool CreatableInRequest
    ) : ProfileFailureDiagnostic;
}

/// <summary>
/// Base typed profile failure contract shared across Core and backend.
/// Category-specific leaf records can inherit from this abstraction in later
/// stories without changing the common discriminator, ownership, or diagnostic
/// surface.
/// </summary>
public abstract record ProfileFailure(
    ProfileFailureCategory Category,
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
);

/// <summary>
/// Base category-1 typed failure emitted for invalid writable profile
/// definitions discovered before runtime execution.
/// </summary>
public abstract record InvalidProfileDefinitionFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : ProfileFailure(ProfileFailureCategory.InvalidProfileDefinition, Emitter, Message, Context, Diagnostics);

/// <summary>
/// Generic category-1 shape for invalid writable profile definitions that do
/// not require a more specific leaf contract.
/// </summary>
public sealed record GenericInvalidProfileDefinitionFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : InvalidProfileDefinitionFailure(Emitter, Message, Context, Diagnostics);

/// <summary>
/// Category-1 failure for writable profiles that hide compiled semantic
/// identity members required by a persisted collection scope.
/// </summary>
public sealed record HiddenSemanticIdentityMembersProfileDefinitionFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ImmutableArray<string> HiddenCanonicalMemberPaths,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : InvalidProfileDefinitionFailure(
        ProfileFailureEmitter.SemanticIdentityCompatibilityValidation,
        "Writable profile hides required semantic identity members for a persisted collection scope.",
        Context,
        Diagnostics
    );

/// <summary>
/// Base category-2 typed failure emitted for invalid profile usage at request
/// orchestration time.
/// </summary>
public abstract record InvalidProfileUsageFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : ProfileFailure(ProfileFailureCategory.InvalidProfileUsage, Emitter, Message, Context, Diagnostics);

/// <summary>
/// Generic category-2 shape for invalid profile usage cases that do not
/// require a more specific leaf contract.
/// </summary>
public sealed record GenericInvalidProfileUsageFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : InvalidProfileUsageFailure(Emitter, Message, Context, Diagnostics);

/// <summary>
/// Category-2 failure for readable-versus-writable profile mode mismatches.
/// </summary>
public sealed record ProfileModeMismatchProfileUsageFailure(
    ProfileFailureContext Context,
    string ExpectedUsage,
    string ActualUsage,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : InvalidProfileUsageFailure(
        ProfileFailureEmitter.ProfileWritePipelineAssembly,
        "Resolved profile mode does not match the requested operation.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-2 failure for requests targeting a resource that the resolved
/// profile does not define for the current operation.
/// </summary>
public sealed record ProfileNotFoundForResourceProfileUsageFailure(
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : InvalidProfileUsageFailure(
        ProfileFailureEmitter.ProfileWritePipelineAssembly,
        "Resolved profile does not define the requested resource for this operation.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-2 failure for request operations that the resolved profile mode
/// does not support.
/// </summary>
public sealed record UnsupportedOperationProfileUsageFailure(
    ProfileFailureContext Context,
    string UnsupportedOperation,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : InvalidProfileUsageFailure(
        ProfileFailureEmitter.ProfileWritePipelineAssembly,
        "Resolved profile does not support the requested operation.",
        Context,
        Diagnostics
    );

/// <summary>
/// Base category-3 typed failure emitted when submitted request data is
/// forbidden by the writable profile or collides after shaping.
/// </summary>
public abstract record WritableProfileValidationFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : ProfileFailure(
        ProfileFailureCategory.WritableProfileValidationFailure,
        Emitter,
        Message,
        Context,
        Diagnostics
    );

/// <summary>
/// Generic category-3 shape for writable-profile validation failures that do
/// not require a more specific leaf contract.
/// </summary>
public sealed record GenericWritableProfileValidationFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : WritableProfileValidationFailure(Emitter, Message, Context, Diagnostics);

/// <summary>
/// Category-3 failure for submitted member/value data that is forbidden by the
/// writable profile after request-side visibility and value-filter evaluation.
/// </summary>
public sealed record ForbiddenSubmittedDataWritableProfileValidationFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    ImmutableArray<string> RequestJsonPaths,
    ImmutableArray<string> ForbiddenCanonicalMemberPaths,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : WritableProfileValidationFailure(
        ProfileFailureEmitter.RequestVisibilityAndWritableShaping,
        "Submitted data is forbidden by the writable profile.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-3 failure for visible submitted collection items that collide on
/// the same stable parent address and compiled semantic identity.
/// </summary>
public sealed record DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeInstanceAddress StableParentAddress,
    ImmutableArray<SemanticIdentityPart> SemanticIdentityPartsInOrder,
    ImmutableArray<string> RequestJsonPaths,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : WritableProfileValidationFailure(
        ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
        "Visible collection items collide on compiled semantic identity after writable-profile shaping.",
        Context,
        Diagnostics
    );

/// <summary>
/// Base category-4 typed failure emitted when a request attempts to create new
/// visible data that the writable profile does not allow.
/// </summary>
public abstract record CreatabilityViolationFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : ProfileFailure(ProfileFailureCategory.CreatabilityViolation, Emitter, Message, Context, Diagnostics);

/// <summary>
/// Generic category-4 shape for creatability violations that do not require a
/// more specific leaf contract.
/// </summary>
public sealed record GenericCreatabilityViolationFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : CreatabilityViolationFailure(Emitter, Message, Context, Diagnostics);

/// <summary>
/// Category-4 failure for a POST/create attempt that would materialize a new
/// visible root resource instance that is not creatable under the profile.
/// </summary>
public sealed record RootCreateRejectedWhenNonCreatableCreatabilityViolationFailure(
    ProfileFailureContext Context,
    ImmutableArray<string> HiddenCreationRequiredMemberPaths,
    ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> Dependencies,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : CreatabilityViolationFailure(
        ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
        "Root resource create is rejected because the writable profile does not allow creating the new visible resource instance.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-4 failure for a non-root visible scope or item insert that is not
/// creatable under the writable profile.
/// </summary>
public sealed record VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    ProfileCreatabilityTargetKind TargetKind,
    ImmutableArray<string> HiddenCreationRequiredMemberPaths,
    ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> Dependencies,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : CreatabilityViolationFailure(
        ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
        "Visible scope or item insert is rejected because the writable profile does not allow creating the new visible instance.",
        Context,
        Diagnostics
    );

/// <summary>
/// Base category-5 typed failure emitted when backend cannot align Core-owned
/// profile scope/address metadata to the selected compiled backend shape.
/// </summary>
public abstract record CoreBackendContractMismatchFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : ProfileFailure(
        ProfileFailureCategory.CoreBackendContractMismatch,
        Emitter,
        Message,
        Context,
        Diagnostics
    );

/// <summary>
/// Generic category-5 shape for Core/backend contract mismatches that do not
/// require a more specific leaf contract.
/// </summary>
public sealed record GenericCoreBackendContractMismatchFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : CoreBackendContractMismatchFailure(Emitter, Message, Context, Diagnostics);

/// <summary>
/// Category-5 failure for Core-emitted scope identifiers that do not map to a
/// compiled backend scope.
/// </summary>
public sealed record UnknownJsonScopeCoreBackendContractMismatchFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind ExpectedScopeKind,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : CoreBackendContractMismatchFailure(
        ProfileFailureEmitter.BackendProfileWriteContext,
        "Core emitted a JsonScope that does not map to a compiled backend scope.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-5 failure for Core-emitted scope or row addresses whose ancestor
/// collection chain cannot be aligned to the compiled backend ancestry.
/// </summary>
public sealed record AncestorChainMismatchCoreBackendContractMismatchFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    ImmutableArray<AncestorCollectionInstance> EmittedAncestorCollectionInstances,
    ImmutableArray<string> ExpectedAncestorJsonScopesInOrder,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : CoreBackendContractMismatchFailure(
        ProfileFailureEmitter.BackendProfileWriteContext,
        "Core emitted an ancestor chain that does not align to the compiled backend collection ancestry.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-5 failure for Core-emitted canonical member paths that are not
/// published by the compiled backend scope vocabulary.
/// </summary>
public sealed record CanonicalMemberPathMismatchCoreBackendContractMismatchFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    ImmutableArray<string> EmittedCanonicalMemberPaths,
    ImmutableArray<string> AllowedCanonicalMemberPaths,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : CoreBackendContractMismatchFailure(
        ProfileFailureEmitter.BackendProfileWriteContext,
        "Core emitted canonical member paths that are not published by the compiled backend scope.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-5 failure for stored-side visibility metadata that cannot be lined
/// up to the compiled backend plan shape.
/// </summary>
public sealed record UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    string MetadataKind,
    ImmutableArray<string> HiddenCanonicalMemberPaths,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : CoreBackendContractMismatchFailure(
        ProfileFailureEmitter.BackendProfileWriteContext,
        "Core emitted stored-side visibility metadata that cannot be aligned to the compiled backend plan shape.",
        Context,
        Diagnostics
    );

/// <summary>
/// Base category-6 typed failure emitted when backend/API binding accounting
/// cannot classify a profiled binding into exactly one deterministic outcome.
/// </summary>
public abstract record BindingAccountingProfileFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : ProfileFailure(ProfileFailureCategory.BindingAccountingFailure, Emitter, Message, Context, Diagnostics);

/// <summary>
/// Generic category-6 shape for binding-accounting failures that do not
/// require a more specific leaf contract.
/// </summary>
public sealed record GenericBindingAccountingProfileFailure(
    ProfileFailureEmitter Emitter,
    string Message,
    ProfileFailureContext Context,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
) : BindingAccountingProfileFailure(Emitter, Message, Context, Diagnostics);

/// <summary>
/// Category-6 failure for a profiled binding that cannot be classified into
/// any permitted write outcome.
/// </summary>
public sealed record UnclassifiedProfileBindingAccountingFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    string BindingName,
    string BindingKind,
    string? CanonicalMemberPath,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : BindingAccountingProfileFailure(
        ProfileFailureEmitter.ProfileErrorClassification,
        "Profiled binding could not be classified into a deterministic write outcome.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-6 failure for a profiled binding that was classified into multiple
/// conflicting write outcomes.
/// </summary>
public sealed record MultiplyClassifiedProfileBindingAccountingFailure(
    ProfileFailureContext Context,
    string JsonScope,
    ScopeKind AffectedScopeKind,
    string BindingName,
    string BindingKind,
    string? CanonicalMemberPath,
    ImmutableArray<ProfileBindingDisposition> AssignedDispositions,
    ImmutableArray<ProfileFailureDiagnostic> Diagnostics
)
    : BindingAccountingProfileFailure(
        ProfileFailureEmitter.ProfileErrorClassification,
        "Profiled binding was classified into multiple conflicting write outcomes.",
        Context,
        Diagnostics
    );

/// <summary>
/// Category-level factory entry points for the shared typed profile failure
/// contract. These methods create generic categorized failures now and leave
/// room for later stories to add more specific leaf factories without changing
/// the common wire surface.
/// </summary>
public static class ProfileFailures
{
    public static InvalidProfileDefinitionFailure InvalidProfileDefinition(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        params ProfileFailureDiagnostic[] diagnostics
    ) => CreateInvalidProfileDefinition(emitter, message, context, diagnostics);

    public static HiddenSemanticIdentityMembersProfileDefinitionFailure HiddenSemanticIdentityMembers(
        string profileName,
        string resourceName,
        string jsonScope,
        IEnumerable<string> hiddenCanonicalMemberPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonScope);
        ArgumentNullException.ThrowIfNull(hiddenCanonicalMemberPaths);

        ImmutableArray<string> hiddenPaths = NormalizeRequiredStrings(
            hiddenCanonicalMemberPaths,
            nameof(hiddenCanonicalMemberPaths)
        );

        ProfileFailureContext context = new(
            ProfileName: profileName,
            ResourceName: resourceName,
            Method: null,
            Operation: null
        );

        return new HiddenSemanticIdentityMembersProfileDefinitionFailure(
            context,
            jsonScope,
            hiddenPaths,
            MergeDiagnostics(
                diagnostics,
                new ProfileFailureDiagnostic.Scope(jsonScope, ScopeKind.Collection),
                new ProfileFailureDiagnostic.CanonicalMemberPaths(hiddenPaths)
            )
        );
    }

    public static InvalidProfileUsageFailure InvalidProfileUsage(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        params ProfileFailureDiagnostic[] diagnostics
    ) => CreateInvalidProfileUsage(emitter, message, context, diagnostics);

    public static ProfileModeMismatchProfileUsageFailure ProfileModeMismatch(
        string profileName,
        string resourceName,
        string method,
        string operation,
        string expectedUsage,
        string actualUsage,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUsage);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualUsage);

        return new ProfileModeMismatchProfileUsageFailure(
            context,
            expectedUsage,
            actualUsage,
            [.. diagnostics]
        );
    }

    public static ProfileNotFoundForResourceProfileUsageFailure ProfileNotFoundForResource(
        string profileName,
        string resourceName,
        string method,
        string operation,
        params ProfileFailureDiagnostic[] diagnostics
    ) => new(CreateRequiredContext(profileName, resourceName, method, operation), [.. diagnostics]);

    public static UnsupportedOperationProfileUsageFailure UnsupportedOperation(
        string profileName,
        string resourceName,
        string method,
        string operation,
        string unsupportedOperation,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(unsupportedOperation);

        return new UnsupportedOperationProfileUsageFailure(context, unsupportedOperation, [.. diagnostics]);
    }

    public static WritableProfileValidationFailure WritableProfileValidationFailure(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        params ProfileFailureDiagnostic[] diagnostics
    ) => CreateWritableProfileValidationFailure(emitter, message, context, diagnostics);

    public static ForbiddenSubmittedDataWritableProfileValidationFailure ForbiddenSubmittedData(
        string profileName,
        string resourceName,
        string method,
        string operation,
        string jsonScope,
        ScopeKind scopeKind,
        IEnumerable<string> requestJsonPaths,
        IEnumerable<string> forbiddenCanonicalMemberPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(jsonScope);
        ArgumentNullException.ThrowIfNull(requestJsonPaths);
        ArgumentNullException.ThrowIfNull(forbiddenCanonicalMemberPaths);

        ImmutableArray<string> normalizedRequestJsonPaths = NormalizeRequiredStrings(
            requestJsonPaths,
            nameof(requestJsonPaths)
        );

        ImmutableArray<string> normalizedForbiddenCanonicalMemberPaths = NormalizeOptionalStrings(
            forbiddenCanonicalMemberPaths,
            nameof(forbiddenCanonicalMemberPaths)
        );

        ImmutableArray<ProfileFailureDiagnostic> mergedDiagnostics =
            normalizedForbiddenCanonicalMemberPaths.IsDefaultOrEmpty
                ? MergeDiagnostics(
                    diagnostics,
                    new ProfileFailureDiagnostic.Scope(jsonScope, scopeKind),
                    new ProfileFailureDiagnostic.RequestPaths(normalizedRequestJsonPaths)
                )
                : MergeDiagnostics(
                    diagnostics,
                    new ProfileFailureDiagnostic.Scope(jsonScope, scopeKind),
                    new ProfileFailureDiagnostic.RequestPaths(normalizedRequestJsonPaths),
                    new ProfileFailureDiagnostic.CanonicalMemberPaths(normalizedForbiddenCanonicalMemberPaths)
                );

        return new(
            context,
            jsonScope,
            scopeKind,
            normalizedRequestJsonPaths,
            normalizedForbiddenCanonicalMemberPaths,
            mergedDiagnostics
        );
    }

    public static DuplicateVisibleCollectionItemCollisionWritableProfileValidationFailure DuplicateVisibleCollectionItemCollision(
        string profileName,
        string resourceName,
        string method,
        string operation,
        string jsonScope,
        ScopeInstanceAddress stableParentAddress,
        IEnumerable<SemanticIdentityPart> semanticIdentityPartsInOrder,
        IEnumerable<string> requestJsonPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(jsonScope);
        ArgumentNullException.ThrowIfNull(stableParentAddress);
        ArgumentNullException.ThrowIfNull(semanticIdentityPartsInOrder);
        ArgumentNullException.ThrowIfNull(requestJsonPaths);

        ImmutableArray<SemanticIdentityPart> normalizedSemanticIdentityParts =
            NormalizeRequiredSemanticIdentityParts(
                semanticIdentityPartsInOrder,
                nameof(semanticIdentityPartsInOrder)
            );

        ImmutableArray<string> normalizedRequestJsonPaths = NormalizeRequiredStrings(
            requestJsonPaths,
            nameof(requestJsonPaths)
        );

        if (normalizedRequestJsonPaths.Length < 2)
        {
            throw new ArgumentException(
                "At least two request paths are required for a duplicate collection-item collision.",
                nameof(requestJsonPaths)
            );
        }

        CollectionRowAddress collectionRowAddress = new(
            jsonScope,
            stableParentAddress,
            normalizedSemanticIdentityParts
        );

        return new(
            context,
            jsonScope,
            stableParentAddress,
            normalizedSemanticIdentityParts,
            normalizedRequestJsonPaths,
            MergeDiagnostics(
                diagnostics,
                new ProfileFailureDiagnostic.Scope(jsonScope, ScopeKind.Collection),
                new ProfileFailureDiagnostic.ScopeAddress(stableParentAddress),
                new ProfileFailureDiagnostic.CollectionRow(collectionRowAddress),
                new ProfileFailureDiagnostic.SemanticIdentity(normalizedSemanticIdentityParts),
                new ProfileFailureDiagnostic.RequestPaths(normalizedRequestJsonPaths)
            )
        );
    }

    public static CreatabilityViolationFailure CreatabilityViolation(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        params ProfileFailureDiagnostic[] diagnostics
    ) => CreateCreatabilityViolation(emitter, message, context, diagnostics);

    public static RootCreateRejectedWhenNonCreatableCreatabilityViolationFailure RootCreateRejectedWhenNonCreatable(
        string profileName,
        string resourceName,
        string method,
        string operation,
        IEnumerable<string> hiddenCreationRequiredMemberPaths,
        IEnumerable<ProfileFailureDiagnostic.CreatabilityDependency>? dependencies = null,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentNullException.ThrowIfNull(hiddenCreationRequiredMemberPaths);

        ImmutableArray<string> normalizedHiddenCreationRequiredMemberPaths = NormalizeOptionalStrings(
            hiddenCreationRequiredMemberPaths,
            nameof(hiddenCreationRequiredMemberPaths)
        );

        ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> normalizedDependencies =
            NormalizeOptionalCreatabilityDependencies(dependencies, nameof(dependencies));

        EnsureCreatabilityBlockingReason(normalizedHiddenCreationRequiredMemberPaths, normalizedDependencies);

        ScopeInstanceAddress rootAddress = new("$", []);

        return new(
            context,
            normalizedHiddenCreationRequiredMemberPaths,
            normalizedDependencies,
            MergeCreatabilityDiagnostics(
                diagnostics,
                "$",
                ScopeKind.Root,
                [new ProfileFailureDiagnostic.ScopeAddress(rootAddress)],
                normalizedHiddenCreationRequiredMemberPaths,
                normalizedDependencies
            )
        );
    }

    public static VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure VisibleScopeOrItemInsertRejectedWhenNonCreatable(
        string profileName,
        string resourceName,
        string method,
        string operation,
        ProfileCreatabilityTargetKind targetKind,
        ScopeInstanceAddress affectedAddress,
        IEnumerable<string> hiddenCreationRequiredMemberPaths,
        IEnumerable<ProfileFailureDiagnostic.CreatabilityDependency>? dependencies = null,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentNullException.ThrowIfNull(affectedAddress);
        ValidateScopeCreatabilityTargetKind(targetKind);

        return CreateVisibleScopeOrItemInsertRejectedWhenNonCreatable(
            context,
            targetKind,
            affectedAddress.JsonScope,
            ScopeKind.NonCollection,
            [new ProfileFailureDiagnostic.ScopeAddress(affectedAddress)],
            hiddenCreationRequiredMemberPaths,
            dependencies,
            diagnostics
        );
    }

    public static VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure VisibleScopeOrItemInsertRejectedWhenNonCreatable(
        string profileName,
        string resourceName,
        string method,
        string operation,
        ProfileCreatabilityTargetKind targetKind,
        CollectionRowAddress affectedAddress,
        IEnumerable<string> hiddenCreationRequiredMemberPaths,
        IEnumerable<ProfileFailureDiagnostic.CreatabilityDependency>? dependencies = null,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentNullException.ThrowIfNull(affectedAddress);
        ValidateCollectionCreatabilityTargetKind(targetKind);

        return CreateVisibleScopeOrItemInsertRejectedWhenNonCreatable(
            context,
            targetKind,
            affectedAddress.JsonScope,
            ScopeKind.Collection,
            BuildCollectionCreatabilityAddressDiagnostics(affectedAddress),
            hiddenCreationRequiredMemberPaths,
            dependencies,
            diagnostics
        );
    }

    public static CoreBackendContractMismatchFailure CoreBackendContractMismatch(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        params ProfileFailureDiagnostic[] diagnostics
    ) => CreateCoreBackendContractMismatch(emitter, message, context, diagnostics);

    public static UnknownJsonScopeCoreBackendContractMismatchFailure UnknownJsonScope(
        string profileName,
        string resourceName,
        string method,
        string operation,
        string jsonScope,
        ScopeKind expectedScopeKind,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ProfileFailureContext context = CreateRequiredContext(profileName, resourceName, method, operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(jsonScope);

        return new(
            context,
            jsonScope,
            expectedScopeKind,
            MergeDiagnostics(diagnostics, new ProfileFailureDiagnostic.Scope(jsonScope, expectedScopeKind))
        );
    }

    public static AncestorChainMismatchCoreBackendContractMismatchFailure AncestorChainMismatch(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        ScopeInstanceAddress emittedAddress,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateAncestorChainMismatch(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            emittedAddress.AncestorCollectionInstances,
            new ProfileFailureDiagnostic.ScopeAddress(emittedAddress),
            diagnostics
        );
    }

    public static AncestorChainMismatchCoreBackendContractMismatchFailure AncestorChainMismatch(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        CollectionRowAddress emittedAddress,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateAncestorChainMismatch(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            emittedAddress.ParentAddress.AncestorCollectionInstances,
            new ProfileFailureDiagnostic.CollectionRow(emittedAddress),
            diagnostics
        );
    }

    public static CanonicalMemberPathMismatchCoreBackendContractMismatchFailure CanonicalMemberPathMismatch(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        ScopeInstanceAddress emittedAddress,
        IEnumerable<string> emittedCanonicalMemberPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateCanonicalMemberPathMismatch(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.ScopeAddress(emittedAddress),
            emittedCanonicalMemberPaths,
            diagnostics
        );
    }

    public static CanonicalMemberPathMismatchCoreBackendContractMismatchFailure CanonicalMemberPathMismatch(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        CollectionRowAddress emittedAddress,
        IEnumerable<string> emittedCanonicalMemberPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateCanonicalMemberPathMismatch(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.CollectionRow(emittedAddress),
            emittedCanonicalMemberPaths,
            diagnostics
        );
    }

    public static UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure UnalignableStoredVisibilityMetadata(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        ScopeInstanceAddress emittedAddress,
        string metadataKind,
        IEnumerable<string> hiddenCanonicalMemberPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateUnalignableStoredVisibilityMetadata(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.ScopeAddress(emittedAddress),
            metadataKind,
            hiddenCanonicalMemberPaths,
            diagnostics
        );
    }

    public static UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure UnalignableStoredVisibilityMetadata(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        CollectionRowAddress emittedAddress,
        string metadataKind,
        IEnumerable<string> hiddenCanonicalMemberPaths,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateUnalignableStoredVisibilityMetadata(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.CollectionRow(emittedAddress),
            metadataKind,
            hiddenCanonicalMemberPaths,
            diagnostics
        );
    }

    public static BindingAccountingProfileFailure BindingAccountingFailure(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        params ProfileFailureDiagnostic[] diagnostics
    ) => CreateBindingAccountingFailure(emitter, message, context, diagnostics);

    public static UnclassifiedProfileBindingAccountingFailure UnclassifiedBindingAccounting(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        ScopeInstanceAddress emittedAddress,
        string bindingName,
        string bindingKind,
        string? canonicalMemberPath,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateUnclassifiedBindingAccounting(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.ScopeAddress(emittedAddress),
            bindingName,
            bindingKind,
            canonicalMemberPath,
            diagnostics
        );
    }

    public static UnclassifiedProfileBindingAccountingFailure UnclassifiedBindingAccounting(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        CollectionRowAddress emittedAddress,
        string bindingName,
        string bindingKind,
        string? canonicalMemberPath,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateUnclassifiedBindingAccounting(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.CollectionRow(emittedAddress),
            bindingName,
            bindingKind,
            canonicalMemberPath,
            diagnostics
        );
    }

    public static MultiplyClassifiedProfileBindingAccountingFailure MultiplyClassifiedBindingAccounting(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        ScopeInstanceAddress emittedAddress,
        string bindingName,
        string bindingKind,
        string? canonicalMemberPath,
        IEnumerable<ProfileBindingDisposition> assignedDispositions,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateMultiplyClassifiedBindingAccounting(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.ScopeAddress(emittedAddress),
            bindingName,
            bindingKind,
            canonicalMemberPath,
            assignedDispositions,
            diagnostics
        );
    }

    public static MultiplyClassifiedProfileBindingAccountingFailure MultiplyClassifiedBindingAccounting(
        string profileName,
        string resourceName,
        string method,
        string operation,
        CompiledScopeDescriptor compiledScope,
        CollectionRowAddress emittedAddress,
        string bindingName,
        string bindingKind,
        string? canonicalMemberPath,
        IEnumerable<ProfileBindingDisposition> assignedDispositions,
        params ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(emittedAddress);

        return CreateMultiplyClassifiedBindingAccounting(
            CreateRequiredContext(profileName, resourceName, method, operation),
            compiledScope,
            emittedAddress.JsonScope,
            new ProfileFailureDiagnostic.CollectionRow(emittedAddress),
            bindingName,
            bindingKind,
            canonicalMemberPath,
            assignedDispositions,
            diagnostics
        );
    }

    private static InvalidProfileDefinitionFailure CreateInvalidProfileDefinition(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        diagnostics ??= [];
        EnsureCommonContract(ProfileFailureCategory.InvalidProfileDefinition, emitter, message, context);
        return new GenericInvalidProfileDefinitionFailure(emitter, message, context, [.. diagnostics]);
    }

    private static InvalidProfileUsageFailure CreateInvalidProfileUsage(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        diagnostics ??= [];
        EnsureCommonContract(ProfileFailureCategory.InvalidProfileUsage, emitter, message, context);
        return new GenericInvalidProfileUsageFailure(emitter, message, context, [.. diagnostics]);
    }

    private static WritableProfileValidationFailure CreateWritableProfileValidationFailure(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        diagnostics ??= [];
        EnsureCommonContract(
            ProfileFailureCategory.WritableProfileValidationFailure,
            emitter,
            message,
            context
        );
        return new GenericWritableProfileValidationFailure(emitter, message, context, [.. diagnostics]);
    }

    private static CoreBackendContractMismatchFailure CreateCoreBackendContractMismatch(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        diagnostics ??= [];
        EnsureCommonContract(ProfileFailureCategory.CoreBackendContractMismatch, emitter, message, context);
        return new GenericCoreBackendContractMismatchFailure(emitter, message, context, [.. diagnostics]);
    }

    private static CreatabilityViolationFailure CreateCreatabilityViolation(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        diagnostics ??= [];
        EnsureCommonContract(ProfileFailureCategory.CreatabilityViolation, emitter, message, context);
        return new GenericCreatabilityViolationFailure(emitter, message, context, [.. diagnostics]);
    }

    private static VisibleScopeOrItemInsertRejectedWhenNonCreatableCreatabilityViolationFailure CreateVisibleScopeOrItemInsertRejectedWhenNonCreatable(
        ProfileFailureContext context,
        ProfileCreatabilityTargetKind targetKind,
        string jsonScope,
        ScopeKind affectedScopeKind,
        IEnumerable<ProfileFailureDiagnostic> addressDiagnostics,
        IEnumerable<string> hiddenCreationRequiredMemberPaths,
        IEnumerable<ProfileFailureDiagnostic.CreatabilityDependency>? dependencies,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonScope);
        ArgumentNullException.ThrowIfNull(addressDiagnostics);
        ArgumentNullException.ThrowIfNull(hiddenCreationRequiredMemberPaths);

        ImmutableArray<string> normalizedHiddenCreationRequiredMemberPaths = NormalizeOptionalStrings(
            hiddenCreationRequiredMemberPaths,
            nameof(hiddenCreationRequiredMemberPaths)
        );

        ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> normalizedDependencies =
            NormalizeOptionalCreatabilityDependencies(dependencies, nameof(dependencies));

        EnsureCreatabilityBlockingReason(normalizedHiddenCreationRequiredMemberPaths, normalizedDependencies);

        return new(
            context,
            jsonScope,
            affectedScopeKind,
            targetKind,
            normalizedHiddenCreationRequiredMemberPaths,
            normalizedDependencies,
            MergeCreatabilityDiagnostics(
                diagnostics,
                jsonScope,
                affectedScopeKind,
                addressDiagnostics,
                normalizedHiddenCreationRequiredMemberPaths,
                normalizedDependencies
            )
        );
    }

    private static AncestorChainMismatchCoreBackendContractMismatchFailure CreateAncestorChainMismatch(
        ProfileFailureContext context,
        CompiledScopeDescriptor compiledScope,
        string emittedJsonScope,
        ImmutableArray<AncestorCollectionInstance> emittedAncestorCollectionInstances,
        ProfileFailureDiagnostic addressDiagnostic,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        ValidateCompiledScopeMatch(compiledScope, emittedJsonScope, nameof(compiledScope));

        return new(
            context,
            emittedJsonScope,
            compiledScope.ScopeKind,
            emittedAncestorCollectionInstances,
            compiledScope.CollectionAncestorsInOrder,
            MergeDiagnostics(
                diagnostics,
                new ProfileFailureDiagnostic.CompiledScope(compiledScope),
                addressDiagnostic
            )
        );
    }

    private static CanonicalMemberPathMismatchCoreBackendContractMismatchFailure CreateCanonicalMemberPathMismatch(
        ProfileFailureContext context,
        CompiledScopeDescriptor compiledScope,
        string emittedJsonScope,
        ProfileFailureDiagnostic addressDiagnostic,
        IEnumerable<string> emittedCanonicalMemberPaths,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        ValidateCompiledScopeMatch(compiledScope, emittedJsonScope, nameof(compiledScope));
        ArgumentNullException.ThrowIfNull(emittedCanonicalMemberPaths);

        ImmutableArray<string> normalizedEmittedCanonicalMemberPaths = NormalizeRequiredStrings(
            emittedCanonicalMemberPaths,
            nameof(emittedCanonicalMemberPaths)
        );

        if (
            normalizedEmittedCanonicalMemberPaths.All(path =>
                compiledScope.CanonicalScopeRelativeMemberPaths.Contains(path)
            )
        )
        {
            throw new ArgumentException(
                "At least one emitted canonical member path must fall outside the compiled scope vocabulary.",
                nameof(emittedCanonicalMemberPaths)
            );
        }

        return new(
            context,
            emittedJsonScope,
            compiledScope.ScopeKind,
            normalizedEmittedCanonicalMemberPaths,
            compiledScope.CanonicalScopeRelativeMemberPaths,
            MergeDiagnostics(
                diagnostics,
                new ProfileFailureDiagnostic.CompiledScope(compiledScope),
                addressDiagnostic,
                new ProfileFailureDiagnostic.CanonicalMemberPaths(
                    compiledScope.CanonicalScopeRelativeMemberPaths
                )
            )
        );
    }

    private static UnalignableStoredVisibilityMetadataCoreBackendContractMismatchFailure CreateUnalignableStoredVisibilityMetadata(
        ProfileFailureContext context,
        CompiledScopeDescriptor compiledScope,
        string emittedJsonScope,
        ProfileFailureDiagnostic addressDiagnostic,
        string metadataKind,
        IEnumerable<string> hiddenCanonicalMemberPaths,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        ValidateCompiledScopeMatch(compiledScope, emittedJsonScope, nameof(compiledScope));
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataKind);
        ArgumentNullException.ThrowIfNull(hiddenCanonicalMemberPaths);

        ImmutableArray<string> normalizedHiddenCanonicalMemberPaths = NormalizeOptionalStrings(
            hiddenCanonicalMemberPaths,
            nameof(hiddenCanonicalMemberPaths)
        );

        if (
            normalizedHiddenCanonicalMemberPaths.Any(path =>
                !compiledScope.CanonicalScopeRelativeMemberPaths.Contains(path)
            )
        )
        {
            throw new ArgumentException(
                "Hidden canonical member paths must be published by the compiled scope vocabulary.",
                nameof(hiddenCanonicalMemberPaths)
            );
        }

        ImmutableArray<ProfileFailureDiagnostic> mergedDiagnostics =
            normalizedHiddenCanonicalMemberPaths.IsDefaultOrEmpty
                ? MergeDiagnostics(
                    diagnostics,
                    new ProfileFailureDiagnostic.CompiledScope(compiledScope),
                    addressDiagnostic
                )
                : MergeDiagnostics(
                    diagnostics,
                    new ProfileFailureDiagnostic.CompiledScope(compiledScope),
                    addressDiagnostic,
                    new ProfileFailureDiagnostic.CanonicalMemberPaths(normalizedHiddenCanonicalMemberPaths)
                );

        return new(
            context,
            emittedJsonScope,
            compiledScope.ScopeKind,
            metadataKind,
            normalizedHiddenCanonicalMemberPaths,
            mergedDiagnostics
        );
    }

    private static BindingAccountingProfileFailure CreateBindingAccountingFailure(
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        diagnostics ??= [];
        EnsureCommonContract(ProfileFailureCategory.BindingAccountingFailure, emitter, message, context);
        return new GenericBindingAccountingProfileFailure(emitter, message, context, [.. diagnostics]);
    }

    private static UnclassifiedProfileBindingAccountingFailure CreateUnclassifiedBindingAccounting(
        ProfileFailureContext context,
        CompiledScopeDescriptor compiledScope,
        string emittedJsonScope,
        ProfileFailureDiagnostic addressDiagnostic,
        string bindingName,
        string bindingKind,
        string? canonicalMemberPath,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        ValidateCompiledScopeMatch(compiledScope, emittedJsonScope, nameof(compiledScope));
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKind);

        string? normalizedCanonicalMemberPath = NormalizeOptionalString(
            canonicalMemberPath,
            nameof(canonicalMemberPath)
        );

        return new(
            context,
            emittedJsonScope,
            compiledScope.ScopeKind,
            bindingName,
            bindingKind,
            normalizedCanonicalMemberPath,
            MergeDiagnostics(
                diagnostics,
                new ProfileFailureDiagnostic.CompiledScope(compiledScope),
                addressDiagnostic,
                new ProfileFailureDiagnostic.Binding(bindingName, bindingKind, normalizedCanonicalMemberPath)
            )
        );
    }

    private static MultiplyClassifiedProfileBindingAccountingFailure CreateMultiplyClassifiedBindingAccounting(
        ProfileFailureContext context,
        CompiledScopeDescriptor compiledScope,
        string emittedJsonScope,
        ProfileFailureDiagnostic addressDiagnostic,
        string bindingName,
        string bindingKind,
        string? canonicalMemberPath,
        IEnumerable<ProfileBindingDisposition> assignedDispositions,
        ProfileFailureDiagnostic[] diagnostics
    )
    {
        ValidateCompiledScopeMatch(compiledScope, emittedJsonScope, nameof(compiledScope));
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKind);
        ArgumentNullException.ThrowIfNull(assignedDispositions);

        string? normalizedCanonicalMemberPath = NormalizeOptionalString(
            canonicalMemberPath,
            nameof(canonicalMemberPath)
        );

        ImmutableArray<ProfileBindingDisposition> normalizedAssignedDispositions =
            NormalizeAssignedDispositions(assignedDispositions, nameof(assignedDispositions));

        if (normalizedAssignedDispositions.Length < 2)
        {
            throw new ArgumentException(
                "At least two distinct assigned dispositions are required for a multiply-classified binding.",
                nameof(assignedDispositions)
            );
        }

        return new(
            context,
            emittedJsonScope,
            compiledScope.ScopeKind,
            bindingName,
            bindingKind,
            normalizedCanonicalMemberPath,
            normalizedAssignedDispositions,
            MergeDiagnostics(
                diagnostics,
                new ProfileFailureDiagnostic.CompiledScope(compiledScope),
                addressDiagnostic,
                new ProfileFailureDiagnostic.Binding(bindingName, bindingKind, normalizedCanonicalMemberPath)
            )
        );
    }

    private static void EnsureCommonContract(
        ProfileFailureCategory category,
        ProfileFailureEmitter emitter,
        string message,
        ProfileFailureContext context
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(context);
        EnsureEmitterOwnership(category, emitter);
    }

    private static ProfileFailureContext CreateRequiredContext(
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        return new(
            ProfileName: profileName,
            ResourceName: resourceName,
            Method: method,
            Operation: operation
        );
    }

    private static void ValidateCompiledScopeMatch(
        CompiledScopeDescriptor compiledScope,
        string emittedJsonScope,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(compiledScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(emittedJsonScope);

        if (compiledScope.JsonScope != emittedJsonScope)
        {
            throw new ArgumentException(
                $"Compiled scope '{compiledScope.JsonScope}' does not match emitted JsonScope '{emittedJsonScope}'.",
                parameterName
            );
        }
    }

    private static ImmutableArray<string> NormalizeRequiredStrings(
        IEnumerable<string> values,
        string parameterName
    )
    {
        ImmutableArray<string> normalizedValues = [.. values];

        if (normalizedValues.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        if (normalizedValues.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Values cannot be null, empty, or whitespace.", parameterName);
        }

        return normalizedValues;
    }

    private static ImmutableArray<string> NormalizeOptionalStrings(
        IEnumerable<string> values,
        string parameterName
    )
    {
        ImmutableArray<string> normalizedValues = [.. values];

        if (normalizedValues.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Values cannot be null, empty, or whitespace.", parameterName);
        }

        return normalizedValues;
    }

    private static string? NormalizeOptionalString(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value;
    }

    private static ImmutableArray<SemanticIdentityPart> NormalizeRequiredSemanticIdentityParts(
        IEnumerable<SemanticIdentityPart> values,
        string parameterName
    )
    {
        ImmutableArray<SemanticIdentityPart> normalizedValues = [.. values];

        if (normalizedValues.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        if (normalizedValues.Any(value => value is null))
        {
            throw new ArgumentException("Values cannot contain null entries.", parameterName);
        }

        return normalizedValues;
    }

    private static ImmutableArray<ProfileBindingDisposition> NormalizeAssignedDispositions(
        IEnumerable<ProfileBindingDisposition> values,
        string parameterName
    )
    {
        ImmutableArray<ProfileBindingDisposition> normalizedValues = [.. values];

        if (normalizedValues.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        if (normalizedValues.Any(value => !Enum.IsDefined(value)))
        {
            throw new ArgumentException("Values must be defined binding dispositions.", parameterName);
        }

        return [.. normalizedValues.Distinct()];
    }

    private static ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> NormalizeOptionalCreatabilityDependencies(
        IEnumerable<ProfileFailureDiagnostic.CreatabilityDependency>? values,
        string parameterName
    )
    {
        if (values is null)
        {
            return [];
        }

        ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> normalizedValues = [.. values];

        if (normalizedValues.Any(value => value is null))
        {
            throw new ArgumentException("Values cannot contain null entries.", parameterName);
        }

        if (
            normalizedValues.Any(value =>
                string.IsNullOrWhiteSpace(value.JsonScope)
                || !Enum.IsDefined(value.DependencyKind)
                || !Enum.IsDefined(value.TargetKind)
                || !Enum.IsDefined(value.ScopeKind)
            )
        )
        {
            throw new ArgumentException(
                "Values must define dependency/target/scope kinds and non-empty JsonScope.",
                parameterName
            );
        }

        return normalizedValues;
    }

    private static ImmutableArray<ProfileFailureDiagnostic> MergeDiagnostics(
        IEnumerable<ProfileFailureDiagnostic> diagnostics,
        params ProfileFailureDiagnostic[] additionalDiagnostics
    ) => [.. additionalDiagnostics, .. diagnostics];

    private static ImmutableArray<ProfileFailureDiagnostic> MergeCreatabilityDiagnostics(
        IEnumerable<ProfileFailureDiagnostic> diagnostics,
        string jsonScope,
        ScopeKind scopeKind,
        IEnumerable<ProfileFailureDiagnostic> addressDiagnostics,
        ImmutableArray<string> hiddenCreationRequiredMemberPaths,
        ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> dependencies
    )
    {
        List<ProfileFailureDiagnostic> additionalDiagnostics =
        [
            new ProfileFailureDiagnostic.Scope(jsonScope, scopeKind),
        ];

        additionalDiagnostics.AddRange(addressDiagnostics);

        if (!hiddenCreationRequiredMemberPaths.IsDefaultOrEmpty)
        {
            additionalDiagnostics.Add(
                new ProfileFailureDiagnostic.HiddenCreationRequiredMembers(hiddenCreationRequiredMemberPaths)
            );
        }

        additionalDiagnostics.AddRange(dependencies);

        return MergeDiagnostics(diagnostics, [.. additionalDiagnostics]);
    }

    private static IReadOnlyList<ProfileFailureDiagnostic> BuildCollectionCreatabilityAddressDiagnostics(
        CollectionRowAddress affectedAddress
    ) =>
        [
            new ProfileFailureDiagnostic.ScopeAddress(affectedAddress.ParentAddress),
            new ProfileFailureDiagnostic.CollectionRow(affectedAddress),
            new ProfileFailureDiagnostic.SemanticIdentity(affectedAddress.SemanticIdentityInOrder),
        ];

    private static void EnsureCreatabilityBlockingReason(
        ImmutableArray<string> hiddenCreationRequiredMemberPaths,
        ImmutableArray<ProfileFailureDiagnostic.CreatabilityDependency> dependencies
    )
    {
        if (hiddenCreationRequiredMemberPaths.IsDefaultOrEmpty && dependencies.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "At least one hidden member or related dependency is required for a creatability violation."
            );
        }
    }

    private static void ValidateScopeCreatabilityTargetKind(ProfileCreatabilityTargetKind targetKind)
    {
        if (
            targetKind
            is not (
                ProfileCreatabilityTargetKind.OneToOneScope
                or ProfileCreatabilityTargetKind.NestedOrCommonTypeScope
                or ProfileCreatabilityTargetKind.ExtensionScope
            )
        )
        {
            throw new ArgumentException(
                $"Target kind '{targetKind}' requires a collection-row or root creatability contract.",
                nameof(targetKind)
            );
        }
    }

    private static void ValidateCollectionCreatabilityTargetKind(ProfileCreatabilityTargetKind targetKind)
    {
        if (
            targetKind
            is not (
                ProfileCreatabilityTargetKind.CollectionOrCommonTypeItem
                or ProfileCreatabilityTargetKind.ExtensionCollectionItem
            )
        )
        {
            throw new ArgumentException(
                $"Target kind '{targetKind}' requires a non-collection scope or root creatability contract.",
                nameof(targetKind)
            );
        }
    }

    private static void EnsureEmitterOwnership(ProfileFailureCategory category, ProfileFailureEmitter emitter)
    {
        var emitterOwnsCategory = category switch
        {
            ProfileFailureCategory.InvalidProfileDefinition => emitter
                == ProfileFailureEmitter.SemanticIdentityCompatibilityValidation,
            ProfileFailureCategory.InvalidProfileUsage => emitter
                == ProfileFailureEmitter.ProfileWritePipelineAssembly,
            ProfileFailureCategory.WritableProfileValidationFailure => emitter
                is ProfileFailureEmitter.RequestVisibilityAndWritableShaping
                    or ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
            ProfileFailureCategory.CreatabilityViolation => emitter
                == ProfileFailureEmitter.RequestCreatabilityAndDuplicateValidation,
            ProfileFailureCategory.CoreBackendContractMismatch => emitter
                == ProfileFailureEmitter.BackendProfileWriteContext,
            ProfileFailureCategory.BindingAccountingFailure => emitter
                == ProfileFailureEmitter.ProfileErrorClassification,
            _ => false,
        };

        if (!emitterOwnsCategory)
        {
            throw new ArgumentException(
                $"Emitter '{emitter}' does not own category '{category}'.",
                nameof(emitter)
            );
        }
    }
}
