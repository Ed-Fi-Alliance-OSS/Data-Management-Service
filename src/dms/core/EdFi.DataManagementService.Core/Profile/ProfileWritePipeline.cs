// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Pipeline result carrying the outcome of profile write processing.
/// </summary>
public sealed record ProfileWritePipelineResult
{
    /// <summary>
    /// Whether the request is governed by a writable profile.
    /// False when the resolved write content type is null (no profile applies).
    /// </summary>
    public bool HasProfile { get; init; }

    /// <summary>
    /// The request-side contract when processing succeeds.
    /// Null when <see cref="HasProfile"/> is false or <see cref="Failures"/> is non-empty.
    /// </summary>
    public ProfileAppliedWriteRequest? Request { get; init; }

    /// <summary>
    /// The full stored-side context for update/upsert flows.
    /// Null for create flows or when a stored-state projector is not provided.
    /// </summary>
    public ProfileAppliedWriteContext? Context { get; init; }

    /// <summary>
    /// Typed profile failures across all categories encountered during processing.
    /// Empty on success.
    /// </summary>
    public ImmutableArray<ProfileFailure> Failures { get; init; }

    /// <summary>
    /// True when a profile applies and no failures were emitted.
    /// </summary>
    public bool IsSuccess => HasProfile && Failures.IsEmpty;

    /// <summary>
    /// Short-circuit result when no writable profile governs the request.
    /// </summary>
    public static ProfileWritePipelineResult NoProfile() => new() { HasProfile = false, Failures = [] };

    /// <summary>
    /// Successful pipeline result with the request contract and optional stored context.
    /// </summary>
    public static ProfileWritePipelineResult Success(
        ProfileAppliedWriteRequest request,
        ProfileAppliedWriteContext? context = null
    ) =>
        new()
        {
            HasProfile = true,
            Request = request,
            Context = context,
            Failures = [],
        };

    /// <summary>
    /// Failed pipeline result with one or more typed failures.
    /// </summary>
    public static ProfileWritePipelineResult Failure(params ProfileFailure[] failures) =>
        new() { HasProfile = true, Failures = [.. failures] };

    /// <summary>
    /// Failed pipeline result with a collection of typed failures.
    /// </summary>
    public static ProfileWritePipelineResult Failure(IEnumerable<ProfileFailure> failures) =>
        new() { HasProfile = true, Failures = [.. failures] };
}

/// <summary>
/// C5 orchestrator that chains profile write processing steps and produces
/// the final <see cref="ProfileAppliedWriteRequest"/> (and optionally
/// <see cref="ProfileAppliedWriteContext"/> for update/upsert flows).
/// </summary>
/// <remarks>
/// <para>
/// Pipeline sequence:
/// <list type="number">
///   <item>No-profile short-circuit when WriteContentType is null</item>
///   <item>Profile-mode validation (must be Write)</item>
///   <item>C2: Semantic identity compatibility validation</item>
///   <item>C3: Request-side visibility and writable request shaping</item>
///   <item>Build stored-side existence lookup</item>
///   <item>C4: Creatability analysis and duplicate collection-item detection</item>
///   <item>Assemble ProfileAppliedWriteRequest</item>
///   <item>For update/upsert: invoke C6 stored-state projector</item>
/// </list>
/// </para>
/// </remarks>
internal static class ProfileWritePipeline
{
    /// <summary>
    /// Orchestrates the full profile write pipeline from request validation
    /// through stored-state projection.
    /// </summary>
    /// <param name="canonicalizedRequestBody">
    /// The canonicalized JSON request body to shape.
    /// </param>
    /// <param name="writeContentType">
    /// The writable profile's content-type definition, or null if no profile applies.
    /// </param>
    /// <param name="resolvedContentType">
    /// The resolved profile content type (Read or Write), or null if no profile applies.
    /// </param>
    /// <param name="scopeCatalog">
    /// The compiled scope descriptors for the target resource.
    /// </param>
    /// <param name="storedDocument">
    /// The stored JSON document for update/upsert flows, or null for create flows.
    /// </param>
    /// <param name="isCreate">
    /// True for POST/create flows, false for PUT/upsert or update flows.
    /// </param>
    /// <param name="profileName">The profile name for failure reporting.</param>
    /// <param name="resourceName">The resource name for failure reporting.</param>
    /// <param name="method">The HTTP method for failure reporting.</param>
    /// <param name="operation">The operation label for failure reporting.</param>
    /// <param name="effectiveSchemaRequiredMembersByScope">
    /// Schema-required members by scope for creatability analysis.
    /// </param>
    /// <param name="storedStateProjector">
    /// Optional C6 stored-state projector for update/upsert flows.
    /// </param>
    /// <returns>
    /// A <see cref="ProfileWritePipelineResult"/> containing the request contract,
    /// optional stored context, or typed failures.
    /// </returns>
    public static ProfileWritePipelineResult Execute(
        JsonNode canonicalizedRequestBody,
        ContentTypeDefinition? writeContentType,
        ProfileContentType? resolvedContentType,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        JsonNode? storedDocument,
        bool isCreate,
        string profileName,
        string resourceName,
        string method,
        string operation,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope,
        IStoredStateProjector? storedStateProjector = null
    )
    {
        // ------------------------------------------------------------------
        // Step 1: No-profile short-circuit
        // ------------------------------------------------------------------
        if (writeContentType == null)
        {
            return ProfileWritePipelineResult.NoProfile();
        }

        // ------------------------------------------------------------------
        // Step 2: Profile-mode validation
        // ------------------------------------------------------------------
        if (resolvedContentType != ProfileContentType.Write)
        {
            return ProfileWritePipelineResult.Failure(
                ProfileFailures.ProfileModeMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    expectedUsage: "Write",
                    actualUsage: resolvedContentType?.ToString() ?? "None"
                )
            );
        }

        // ------------------------------------------------------------------
        // Step 3: C2 — Semantic identity compatibility validation
        // ------------------------------------------------------------------
        ProfileDefinition syntheticDefinition = new(
            profileName,
            [new ResourceProfile(resourceName, LogicalSchema: null, ReadContentType: null, writeContentType)]
        );

        IReadOnlyList<HiddenSemanticIdentityMembersProfileDefinitionFailure> identityFailures =
            SemanticIdentityCompatibilityValidator.Validate(syntheticDefinition, resourceName, scopeCatalog);

        if (identityFailures.Count > 0)
        {
            return ProfileWritePipelineResult.Failure(identityFailures);
        }

        // ------------------------------------------------------------------
        // Step 4: C3 — Request-side visibility and writable request shaping
        // ------------------------------------------------------------------
        var classifier = new ProfileVisibilityClassifier(writeContentType, scopeCatalog);
        var addressEngine = new AddressDerivationEngine(scopeCatalog);
        var shaper = new WritableRequestShaper(
            classifier,
            addressEngine,
            profileName,
            resourceName,
            method,
            operation
        );

        WritableRequestShapingResult shapingResult = shaper.Shape(canonicalizedRequestBody);

        // Bail early if C3 shaping produced validation failures
        if (!shapingResult.ValidationFailures.IsEmpty)
        {
            return ProfileWritePipelineResult.Failure(
                shapingResult.ValidationFailures.CastArray<ProfileFailure>()
            );
        }

        // ------------------------------------------------------------------
        // Step 5: Build stored-side existence lookup
        // ------------------------------------------------------------------
        StoredSideExistenceLookupResult existenceLookupResult = StoredSideExistenceLookupBuilder.Build(
            storedDocument,
            scopeCatalog,
            writeContentType
        );

        // ------------------------------------------------------------------
        // Step 6: C4 — Creatability analysis and duplicate detection
        // ------------------------------------------------------------------
        var creatabilityAnalyzer = new CreatabilityAnalyzer(
            scopeCatalog,
            classifier,
            profileName,
            resourceName,
            method,
            operation
        );

        CreatabilityResult creatabilityResult = creatabilityAnalyzer.Analyze(
            shapingResult.RequestScopeStates,
            shapingResult.VisibleRequestCollectionItems,
            existenceLookupResult.Lookup,
            isCreate,
            effectiveSchemaRequiredMembersByScope
        );

        ImmutableArray<WritableProfileValidationFailure> duplicateFailures =
            DuplicateCollectionItemDetector.Detect(
                creatabilityResult.EnrichedCollectionItems,
                profileName,
                resourceName,
                method,
                operation
            );

        // Bail out if creatability or duplicate failures accumulated
        if (!creatabilityResult.Failures.IsEmpty || !duplicateFailures.IsEmpty)
        {
            ImmutableArray<ProfileFailure>.Builder failureBuilder =
                ImmutableArray.CreateBuilder<ProfileFailure>();
            failureBuilder.AddRange(creatabilityResult.Failures);
            failureBuilder.AddRange(duplicateFailures);
            return ProfileWritePipelineResult.Failure(failureBuilder.ToImmutable());
        }

        // ------------------------------------------------------------------
        // Step 7: Assemble ProfileAppliedWriteRequest
        // ------------------------------------------------------------------
        ProfileAppliedWriteRequest request = new(
            WritableRequestBody: shapingResult.WritableRequestBody,
            RootResourceCreatable: creatabilityResult.RootResourceCreatable,
            RequestScopeStates: creatabilityResult.EnrichedScopeStates,
            VisibleRequestCollectionItems: creatabilityResult.EnrichedCollectionItems
        );

        // ------------------------------------------------------------------
        // Step 8: For update/upsert — invoke C6 stored-state projector
        // ------------------------------------------------------------------
        ProfileAppliedWriteContext? context = null;

        if (!isCreate && storedStateProjector != null)
        {
            context = storedStateProjector.ProjectStoredState(request, existenceLookupResult);
        }

        return ProfileWritePipelineResult.Success(request, context);
    }
}
