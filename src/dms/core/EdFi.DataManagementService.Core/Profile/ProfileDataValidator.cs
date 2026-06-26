// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Profile.ProfileExtensionSchemaResolver;

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Validates profile definitions against the API schema to ensure they reference valid resources and members.
/// Provides detailed validation with error and warning severity levels.
/// Errors prevent profile loading, while warnings allow loading but are logged.
/// </summary>
internal class ProfileDataValidator(ILogger<ProfileDataValidator> logger) : IProfileDataValidator
{
    /// <summary>
    /// Encapsulates common validation parameters to reduce method parameter counts and improve maintainability.
    /// </summary>
    private sealed record ValidationContext(
        string ProfileName,
        string ResourceName,
        string ContentTypeName,
        string PathPrefix,
        ValidationSeverity Severity,
        HashSet<string>? IdentityPaths,
        ResourceSchema? ResourceSchema
    )
    {
        public ValidationContext WithPathPrefix(string newPrefix) => this with { PathPrefix = newPrefix };

        public ValidationContext ForMemberSelection(MemberSelection memberSelection) =>
            this with
            {
                Severity =
                    memberSelection == MemberSelection.IncludeOnly
                        ? ValidationSeverity.Error
                        : ValidationSeverity.Warning,
                IdentityPaths =
                    memberSelection == MemberSelection.ExcludeOnly
                        ? ResourceSchema?.IdentityJsonPaths.Select(jp => jp.Value).ToHashSet()
                        : null,
            };
    }

    /// <summary>
    /// Validates a profile definition against the effective API schema.
    /// Performs comprehensive validation of all resources, properties, and extensions
    /// according to IncludeOnly and ExcludeOnly rules.
    /// </summary>
    /// <param name="profileDefinition">The parsed profile definition to validate.</param>
    /// <param name="effectiveApiSchemaProvider">Provider for accessing the effective API schema documents.</param>
    /// <returns>A validation result containing any errors or warnings found.</returns>
    /// <remarks>
    /// Validation Rules:
    /// - IncludeOnly mode: All references must exist (errors if not)
    /// - ExcludeOnly mode: References may not exist (warnings if not), but identity members cannot be excluded (errors)
    /// - Empty profiles are valid
    /// - Multiple resources are validated independently
    /// - Both ReadContentType and WriteContentType are validated
    ///
    /// This method intentionally continues to return
    /// <see cref="ProfileValidationResult" /> until category-1
    /// <see cref="ProfileFailure" /> wiring lands. Keep any future adaptation at
    /// this validator boundary or its immediate caller rather than in
    /// middleware/API mapping.
    /// </remarks>
    public ProfileValidationResult Validate(
        ProfileDefinition profileDefinition,
        IEffectiveApiSchemaProvider effectiveApiSchemaProvider
    )
    {
        string sanitizedProfileName = LoggingSanitizer.SanitizeForLogging(profileDefinition.ProfileName);

        using var scope = logger.BeginScope("Profile validation for '{ProfileName}'", sanitizedProfileName);

        logger.LogDebug("Starting validation of profile '{ProfileName}'", sanitizedProfileName);

        var failures = ValidateResources(profileDefinition, effectiveApiSchemaProvider);

        if (failures.Count == 0)
        {
            logger.LogDebug(
                "Profile '{ProfileName}' validation successful - no errors or warnings",
                sanitizedProfileName
            );
            return ProfileValidationResult.Success;
        }

        var errorCount = failures.Count(f => f.Severity == ValidationSeverity.Error);
        var warningCount = failures.Count(f => f.Severity == ValidationSeverity.Warning);

        logger.LogInformation(
            "Profile '{ProfileName}' validation completed with {ErrorCount} errors and {WarningCount} warnings",
            sanitizedProfileName,
            errorCount,
            warningCount
        );

        // Log individual failures
        foreach (var failure in failures)
        {
            var logLevel = failure.Severity == ValidationSeverity.Error ? LogLevel.Error : LogLevel.Warning;
            string sanitizedMessage = LoggingSanitizer.SanitizeForLogging(failure.Message);
            string sanitizedFailureProfile = LoggingSanitizer.SanitizeForLogging(failure.ProfileName);
            string sanitizedResource = LoggingSanitizer.SanitizeForLogging(failure.ResourceName ?? "N/A");
            string sanitizedMember = LoggingSanitizer.SanitizeForLogging(failure.MemberName ?? "N/A");
            logger.Log(
                logLevel,
                "Profile validation {Severity}: {Message} (Profile: {ProfileName}, Resource: {ResourceName}, Member: {MemberName})",
                failure.Severity switch
                {
                    ValidationSeverity.Error => "error",
                    ValidationSeverity.Warning => "warning",
                    _ => "unknown",
                },
                sanitizedMessage,
                sanitizedFailureProfile,
                sanitizedResource,
                sanitizedMember
            );
        }

        return ProfileValidationResult.Failure(failures);
    }

    /// <summary>
    /// Gets all project schemas (core + extensions) from the API schema documents.
    /// </summary>
    private static IEnumerable<ProjectSchema> GetProjectSchemas(ApiSchemaDocuments apiSchemaDocuments)
    {
        return new[] { apiSchemaDocuments.GetCoreProjectSchema() }.Concat(
            apiSchemaDocuments.GetExtensionProjectSchemas()
        );
    }

    /// <summary>
    /// Validates all resources in the profile definition in a single pass.
    /// Checks both resource existence and member selection validation.
    /// </summary>
    private static List<ValidationFailure> ValidateResources(
        ProfileDefinition profileDefinition,
        IEffectiveApiSchemaProvider effectiveApiSchemaProvider
    )
    {
        var failures = new List<ValidationFailure>();
        var apiSchemaDocuments = effectiveApiSchemaProvider.Documents;
        var projectSchemas = GetProjectSchemas(apiSchemaDocuments);

        foreach (var resourceProfile in profileDefinition.Resources)
        {
            // Find the resource schema across core and extensions (single traversal)
            JsonNode? resourceSchemaNode = FindResourceSchemaNode(
                resourceProfile.ResourceName,
                projectSchemas
            );

            // Resource doesn't exist - add error and skip member validation
            if (resourceSchemaNode is null)
            {
                failures.Add(
                    new ValidationFailure(
                        ValidationSeverity.Error,
                        profileDefinition.ProfileName,
                        resourceProfile.ResourceName,
                        null,
                        $"Resource '{resourceProfile.ResourceName}' does not exist in the API schema."
                    )
                );
                continue;
            }

            // Resource exists - validate member selection
            failures.AddRange(
                ValidateResourceMemberSelection(
                    resourceProfile,
                    resourceSchemaNode,
                    profileDefinition.ProfileName
                )
            );
        }

        return failures;
    }

    /// <summary>
    /// Finds the resource schema node by searching through all project schemas once.
    /// </summary>
    private static JsonNode? FindResourceSchemaNode(
        string resourceName,
        IEnumerable<ProjectSchema> projectSchemas
    )
    {
        foreach (var projectSchema in projectSchemas)
        {
            var resourceNode = projectSchema.FindResourceSchemaNodeByResourceName(
                new ResourceName(resourceName)
            );
            if (resourceNode is not null)
            {
                return resourceNode;
            }
        }
        return null;
    }

    /// <summary>
    /// Validates member selection for a single resource profile.
    /// </summary>
    private static List<ValidationFailure> ValidateResourceMemberSelection(
        ResourceProfile resourceProfile,
        JsonNode resourceSchemaNode,
        string profileName
    )
    {
        var failures = new List<ValidationFailure>();

        // Get the JSON schema properties
        var jsonSchema = resourceSchemaNode["jsonSchemaForInsert"] as JsonObject;
        var propertiesNode = jsonSchema?["properties"] as JsonObject;

        if (propertiesNode is null)
        {
            return failures;
        }

        // Get resource schema to access identity paths
        var resourceSchema = new ResourceSchema(resourceSchemaNode);

        // Validate ReadContentType members
        if (resourceProfile.ReadContentType is not null)
        {
            failures.AddRange(
                ValidateContentTypeMembers(
                    resourceProfile.ReadContentType,
                    propertiesNode,
                    profileName,
                    resourceProfile.ResourceName,
                    "read",
                    resourceSchema
                )
            );
        }

        // Validate WriteContentType members
        if (resourceProfile.WriteContentType is not null)
        {
            failures.AddRange(
                ValidateContentTypeMembers(
                    resourceProfile.WriteContentType,
                    propertiesNode,
                    profileName,
                    resourceProfile.ResourceName,
                    "write",
                    resourceSchema
                )
            );
        }

        return failures;
    }

    /// <summary>
    /// Server-generated field names are outside the profile DSL namespace and cannot
    /// be referenced by any MemberSelection mode. Returns a <see cref="ValidationFailure"/>
    /// with <see cref="ValidationSeverity.Error"/> if the member name is server-generated,
    /// otherwise null. Severity is always Error, independent of <paramref name="context"/>.
    /// </summary>
    private static ValidationFailure? CheckServerGeneratedField(
        string memberName,
        string memberKindLabel,
        ValidationContext context
    )
    {
        if (!ServerGeneratedFieldNames.Contains(memberName))
        {
            return null;
        }

        return new ValidationFailure(
            ValidationSeverity.Error,
            context.ProfileName,
            context.ResourceName,
            $"{context.PathPrefix}{memberName}",
            $"{memberKindLabel} '{memberName}' in {context.ContentTypeName} content type "
                + "is a server-generated field and is not profile-addressable."
        );
    }

    /// <summary>
    /// Recursive walk over every explicit rule name in a content type, emitting a
    /// server-generated-field failure for any rule whose name is server-generated.
    /// Runs ahead of schema/member-selection validation so the rejection contract
    /// applies regardless of <see cref="MemberSelection"/>, and so siblings deeper
    /// in the tree are not silently skipped by an IncludeAll ancestor. When a rule
    /// name is server-generated the walk does not descend into its children: the
    /// outer name is already invalid, so reporting failures from its bogus subtree
    /// would be noise. Only server-gen failures are produced here — schema existence
    /// and identity-path checks remain on their original code paths.
    /// </summary>
    private static List<ValidationFailure> CollectServerGeneratedNameFailures(
        ContentTypeDefinition contentType,
        ValidationContext context
    )
    {
        var failures = new List<ValidationFailure>();

        foreach (var property in contentType.Properties)
        {
            AddIfServerGenerated(property.Name, "Property", context, failures);
        }
        foreach (var obj in contentType.Objects)
        {
            WalkObjectRuleNames(obj, context, failures);
        }
        foreach (var collection in contentType.Collections)
        {
            WalkCollectionRuleNames(collection, context, failures);
        }
        foreach (var extension in contentType.Extensions)
        {
            WalkExtensionRuleNames(extension, context, failures);
        }

        return failures;
    }

    private static void WalkObjectRuleNames(
        ObjectRule objectRule,
        ValidationContext context,
        List<ValidationFailure> failures
    )
    {
        if (AddIfServerGenerated(objectRule.Name, "Object", context, failures))
        {
            return;
        }

        var childContext = context.WithPathPrefix($"{context.PathPrefix}{objectRule.Name}.");
        foreach (var property in objectRule.Properties ?? [])
        {
            AddIfServerGenerated(property.Name, "Property", childContext, failures);
        }
        foreach (var nested in objectRule.NestedObjects ?? [])
        {
            WalkObjectRuleNames(nested, childContext, failures);
        }
        foreach (var nestedCollection in objectRule.Collections ?? [])
        {
            WalkCollectionRuleNames(nestedCollection, childContext, failures);
        }
        foreach (var nestedExtension in objectRule.Extensions ?? [])
        {
            WalkExtensionRuleNames(nestedExtension, childContext, failures);
        }
    }

    private static void WalkCollectionRuleNames(
        CollectionRule collectionRule,
        ValidationContext context,
        List<ValidationFailure> failures
    )
    {
        if (AddIfServerGenerated(collectionRule.Name, "Collection", context, failures))
        {
            return;
        }

        var childContext = context.WithPathPrefix($"{context.PathPrefix}{collectionRule.Name}[].");
        if (collectionRule.ItemFilter is not null)
        {
            AddIfServerGenerated(
                collectionRule.ItemFilter.PropertyName,
                "Collection item filter property",
                childContext,
                failures
            );
        }
        foreach (var property in collectionRule.Properties ?? [])
        {
            AddIfServerGenerated(property.Name, "Property", childContext, failures);
        }
        foreach (var nested in collectionRule.NestedObjects ?? [])
        {
            WalkObjectRuleNames(nested, childContext, failures);
        }
        foreach (var nestedCollection in collectionRule.NestedCollections ?? [])
        {
            WalkCollectionRuleNames(nestedCollection, childContext, failures);
        }
        foreach (var nestedExtension in collectionRule.Extensions ?? [])
        {
            WalkExtensionRuleNames(nestedExtension, childContext, failures);
        }
    }

    private static void WalkExtensionRuleNames(
        ExtensionRule extensionRule,
        ValidationContext context,
        List<ValidationFailure> failures
    )
    {
        if (AddIfServerGenerated(extensionRule.Name, "Extension", context, failures))
        {
            return;
        }

        var childContext = context.WithPathPrefix($"{context.PathPrefix}_ext.{extensionRule.Name}.");
        foreach (var property in extensionRule.Properties ?? [])
        {
            AddIfServerGenerated(property.Name, "Property", childContext, failures);
        }
        foreach (var nested in extensionRule.Objects ?? [])
        {
            WalkObjectRuleNames(nested, childContext, failures);
        }
        foreach (var nestedCollection in extensionRule.Collections ?? [])
        {
            WalkCollectionRuleNames(nestedCollection, childContext, failures);
        }
    }

    private static bool AddIfServerGenerated(
        string memberName,
        string memberKindLabel,
        ValidationContext context,
        List<ValidationFailure> failures
    )
    {
        var failure = CheckServerGeneratedField(memberName, memberKindLabel, context);
        if (failure is null)
        {
            return false;
        }
        failures.Add(failure);
        return true;
    }

    private static List<ValidationFailure> ValidateContentTypeMembers(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName,
        ResourceSchema resourceSchema
    )
    {
        var baseContext = new ValidationContext(
            profileName,
            resourceName,
            contentTypeName,
            string.Empty,
            ValidationSeverity.Error,
            null,
            resourceSchema
        );

        // Reject server-generated rule names everywhere before any schema or
        // member-selection pruning. Schema validation below skips server-gen
        // names so the pre-pass is the single source of those failures.
        var failures = CollectServerGeneratedNameFailures(contentType, baseContext);

        // Resolve every extension rule against the schema at its own location, regardless
        // of member selection (canonicalization walks the whole tree and drops unmatched
        // rules, so this is the single source of extension feedback). Unknown extensions
        // get the missing-reference severity (IncludeOnly error, otherwise warning); two
        // rules that resolve to the same schema key would throw when ExtensionRulesByName
        // is built, so that collision is always an error.
        failures.AddRange(CollectExtensionResolutionFailures(contentType, schemaProperties, baseContext));

        failures.AddRange(
            contentType.MemberSelection switch
            {
                MemberSelection.IncludeOnly => ValidateMembers(
                    contentType,
                    schemaProperties,
                    baseContext.ForMemberSelection(MemberSelection.IncludeOnly)
                ),
                MemberSelection.ExcludeOnly => ValidateMembers(
                    contentType,
                    schemaProperties,
                    baseContext.ForMemberSelection(MemberSelection.ExcludeOnly)
                ),
                MemberSelection.IncludeAll => ValidateNestedMemberSelection(
                    contentType,
                    schemaProperties,
                    baseContext
                ),
                _ => [],
            }
        );

        return failures;
    }

    /// <summary>
    /// Unified validation method for IncludeOnly and ExcludeOnly modes.
    /// </summary>
    private static List<ValidationFailure> ValidateMembers(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        ValidationContext context
    )
    {
        var failures = new List<ValidationFailure>();

        // Validate properties. Server-generated names are emitted by the
        // pre-pass; skip them here so they do not double-fail or surface a
        // misleading "does not exist" message.
        failures.AddRange(
            contentType.Properties.SelectMany(property =>
            {
                if (ServerGeneratedFieldNames.Contains(property.Name))
                {
                    return Enumerable.Empty<ValidationFailure>();
                }

                var memberPath = $"$.{context.PathPrefix}{property.Name}";
                var propertyFailures = new List<ValidationFailure>();

                if (!schemaProperties.ContainsKey(property.Name))
                {
                    propertyFailures.Add(
                        new ValidationFailure(
                            context.Severity,
                            context.ProfileName,
                            context.ResourceName,
                            $"{context.PathPrefix}{property.Name}",
                            $"Property '{property.Name}' in {context.ContentTypeName} content type does not exist in resource '{context.ResourceName}'."
                        )
                    );
                }
                else if (context.IdentityPaths?.Contains(memberPath) == true)
                {
                    propertyFailures.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            context.ProfileName,
                            context.ResourceName,
                            $"{context.PathPrefix}{property.Name}",
                            $"Property '{property.Name}' in {context.ContentTypeName} content type ExcludeOnly is an identity member and cannot be excluded."
                        )
                    );
                }

                return propertyFailures;
            })
        );

        // Validate objects
        foreach (var obj in contentType.Objects)
        {
            if (ServerGeneratedFieldNames.Contains(obj.Name))
            {
                continue;
            }

            if (!schemaProperties.ContainsKey(obj.Name))
            {
                failures.Add(
                    new ValidationFailure(
                        context.Severity,
                        context.ProfileName,
                        context.ResourceName,
                        obj.Name,
                        $"Object '{obj.Name}' in {context.ContentTypeName} content type does not exist in resource '{context.ResourceName}'."
                    )
                );
            }
            else if (
                schemaProperties[obj.Name] is JsonObject nestedObj
                && nestedObj["properties"] is JsonObject nestedProperties
            )
            {
                failures.AddRange(
                    ValidateObjectRuleMembers(
                        obj,
                        nestedProperties,
                        context.WithPathPrefix($"{obj.Name}.").ForMemberSelection(obj.MemberSelection)
                    )
                );
            }
        }

        // Validate collections
        foreach (var collection in contentType.Collections)
        {
            if (ServerGeneratedFieldNames.Contains(collection.Name))
            {
                continue;
            }

            if (!schemaProperties.ContainsKey(collection.Name))
            {
                failures.Add(
                    new ValidationFailure(
                        context.Severity,
                        context.ProfileName,
                        context.ResourceName,
                        collection.Name,
                        $"Collection '{collection.Name}' in {context.ContentTypeName} content type does not exist in resource '{context.ResourceName}'."
                    )
                );
            }
            else if (
                schemaProperties[collection.Name] is JsonObject collectionObj
                && collectionObj["items"] is JsonObject itemsNode
                && itemsNode["properties"] is JsonObject itemProperties
            )
            {
                failures.AddRange(
                    ValidateCollectionRuleMembers(
                        collection,
                        itemProperties,
                        context
                            .WithPathPrefix($"{collection.Name}[].")
                            .ForMemberSelection(collection.MemberSelection)
                    )
                );
            }
        }

        // Validate extensions
        foreach (var extension in contentType.Extensions)
        {
            failures.AddRange(ValidateExtensionRule(extension, schemaProperties, context));
        }

        return failures;
    }

    /// <summary>
    /// Resolves every extension rule against the schema's <c>_ext.properties</c> at that rule's
    /// own location — at the root and inside objects, collections, and extensions — regardless of
    /// member selection. Canonicalization walks the whole tree and drops any unmatched extension
    /// rule (including under IncludeAll branches that member-selection validation short-circuits),
    /// so this is the single source of extension feedback and the traversal mirrors it. Two kinds
    /// of failure are produced:
    /// <list type="bullet">
    /// <item>An extension whose name does not resolve to a schema key: a non-IncludeAll rule
    /// (the author explicitly selected/deselected members of the extension), or any rule under an
    /// IncludeOnly parent, is an error; an IncludeAll rule under a tolerant parent is a warning,
    /// and canonicalization then drops the rule so the profile loads without an unresolved scope.</item>
    /// <item>Two sibling rules that resolve to the <em>same</em> schema key (e.g. "sample" and
    /// "Sample"): always an error, because both survive canonicalization and the cached
    /// ExtensionRulesByName dictionary would throw on the duplicate key.</item>
    /// </list>
    /// Unresolved names are never treated as duplicates: each is dropped, so case-variant unknown
    /// names keep the missing-reference severity rather than being rejected as a collision.
    /// </summary>
    private static List<ValidationFailure> CollectExtensionResolutionFailures(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        ValidationContext context
    )
    {
        var failures = new List<ValidationFailure>();

        CheckExtensions(
            contentType.Extensions,
            schemaProperties,
            context,
            contentType.MemberSelection,
            failures
        );
        foreach (var obj in contentType.Objects)
        {
            WalkObjectExtensions(obj, schemaProperties, context, failures);
        }
        foreach (var collection in contentType.Collections)
        {
            WalkCollectionExtensions(collection, schemaProperties, context, failures);
        }

        return failures;
    }

    /// <summary>
    /// Severity for an extension rule that names an extension absent from the schema. An explicit
    /// non-IncludeAll rule selects or deselects specific members, which only makes sense if the
    /// extension exists, so it is an error regardless of parent; a reference under an IncludeOnly
    /// parent must also exist (error). An IncludeAll rule under a tolerant (ExcludeOnly/IncludeAll)
    /// parent is a warning — it asserts nothing specific and canonicalization drops it safely.
    /// </summary>
    private static ValidationSeverity SeverityForMissingExtension(
        MemberSelection parentSelection,
        MemberSelection extensionSelection
    ) =>
        parentSelection != MemberSelection.IncludeOnly && extensionSelection == MemberSelection.IncludeAll
            ? ValidationSeverity.Warning
            : ValidationSeverity.Error;

    private static void CheckExtensions(
        IReadOnlyList<ExtensionRule> extensions,
        JsonObject? schemaProperties,
        ValidationContext context,
        MemberSelection parentSelection,
        List<ValidationFailure> failures
    )
    {
        JsonObject? extProperties = ExtensionPropertiesAt(schemaProperties);
        HashSet<string>? resolvedKeys = null;

        foreach (var extension in extensions)
        {
            // Server-generated extension names are rejected by the dedicated pre-pass.
            if (ServerGeneratedFieldNames.Contains(extension.Name))
            {
                continue;
            }

            if (!TryResolveExtensionKey(extProperties, extension.Name, out string canonicalKey))
            {
                // Unknown name: severity per the missing-reference contract. Not a duplicate — an
                // unmatched rule is dropped by canonicalization, so case-variant unknown names
                // cannot collide.
                failures.Add(
                    new ValidationFailure(
                        SeverityForMissingExtension(parentSelection, extension.MemberSelection),
                        context.ProfileName,
                        context.ResourceName,
                        $"{context.PathPrefix}_ext.{extension.Name}",
                        $"Extension '{extension.Name}' in {context.ContentTypeName} content type does not exist in resource '{context.ResourceName}'."
                    )
                );
                continue;
            }

            resolvedKeys ??= new HashSet<string>(StringComparer.Ordinal);
            if (!resolvedKeys.Add(canonicalKey))
            {
                // Two sibling rules resolve to the same schema key: both survive canonicalization
                // and ExtensionRulesByName would throw on the duplicate key, so this is an error.
                failures.Add(
                    new ValidationFailure(
                        ValidationSeverity.Error,
                        context.ProfileName,
                        context.ResourceName,
                        $"{context.PathPrefix}_ext.{extension.Name}",
                        $"Extension '{extension.Name}' in {context.ContentTypeName} content type resolves to schema extension key "
                            + $"'{canonicalKey}', which is already used by another extension rule at the same level; extension names are "
                            + "matched to the schema key case-insensitively, so these collapse to the same key."
                    )
                );
                continue;
            }

            // Resolved and unique: recurse into the extension's own objects/collections at the
            // extension's child schema location.
            JsonObject? extensionProperties =
                (extProperties?[canonicalKey] as JsonObject)?["properties"] as JsonObject;
            ValidationContext childContext = context.WithPathPrefix(
                $"{context.PathPrefix}_ext.{extension.Name}."
            );
            foreach (var nestedObject in extension.Objects ?? [])
            {
                WalkObjectExtensions(nestedObject, extensionProperties, childContext, failures);
            }
            foreach (var nestedCollection in extension.Collections ?? [])
            {
                WalkCollectionExtensions(nestedCollection, extensionProperties, childContext, failures);
            }
        }
    }

    private static void WalkObjectExtensions(
        ObjectRule objectRule,
        JsonObject? schemaProperties,
        ValidationContext context,
        List<ValidationFailure> failures
    )
    {
        if (ServerGeneratedFieldNames.Contains(objectRule.Name))
        {
            return;
        }

        JsonObject? objectProperties = MemberProperties(schemaProperties, objectRule.Name);
        ValidationContext childContext = context.WithPathPrefix($"{context.PathPrefix}{objectRule.Name}.");

        if (objectRule.Extensions is not null)
        {
            CheckExtensions(
                objectRule.Extensions,
                objectProperties,
                childContext,
                objectRule.MemberSelection,
                failures
            );
        }
        foreach (var nested in objectRule.NestedObjects ?? [])
        {
            WalkObjectExtensions(nested, objectProperties, childContext, failures);
        }
        foreach (var collection in objectRule.Collections ?? [])
        {
            WalkCollectionExtensions(collection, objectProperties, childContext, failures);
        }
    }

    private static void WalkCollectionExtensions(
        CollectionRule collectionRule,
        JsonObject? schemaProperties,
        ValidationContext context,
        List<ValidationFailure> failures
    )
    {
        if (ServerGeneratedFieldNames.Contains(collectionRule.Name))
        {
            return;
        }

        JsonObject? itemProperties = CollectionItemProperties(schemaProperties, collectionRule.Name);
        ValidationContext childContext = context.WithPathPrefix(
            $"{context.PathPrefix}{collectionRule.Name}[]."
        );

        if (collectionRule.Extensions is not null)
        {
            CheckExtensions(
                collectionRule.Extensions,
                itemProperties,
                childContext,
                collectionRule.MemberSelection,
                failures
            );
        }
        foreach (var nested in collectionRule.NestedObjects ?? [])
        {
            WalkObjectExtensions(nested, itemProperties, childContext, failures);
        }
        foreach (var nestedCollection in collectionRule.NestedCollections ?? [])
        {
            WalkCollectionExtensions(nestedCollection, itemProperties, childContext, failures);
        }
    }

    private static List<ValidationFailure> ValidateExtensionRule(
        ExtensionRule extension,
        JsonObject schemaProperties,
        ValidationContext context
    )
    {
        var failures = new List<ValidationFailure>();

        if (ServerGeneratedFieldNames.Contains(extension.Name))
        {
            return failures;
        }

        // Existence of the extension namespace is validated independently of member
        // selection by CollectExtensionResolutionFailures, which walks every extension rule
        // in the tree (the same traversal canonicalization uses to drop unmatched rules).
        // Here we only validate the members of an extension that resolves; an unresolved
        // extension has no members to check and is reported by that pre-pass.
        JsonObject? extProperties = ExtensionPropertiesAt(schemaProperties);
        JsonObject? extensionProperties = TryResolveExtensionKey(
            extProperties,
            extension.Name,
            out string canonicalKey
        )
            ? MemberProperties(extProperties, canonicalKey)
            : null;

        if (extensionProperties is null)
        {
            return failures;
        }

        // Preserve the enclosing context prefix so error paths under a nested
        // extension (e.g. an Extensions rule inside an Object or Collection)
        // report as "schoolReference._ext.sample.x" rather than "_ext.sample.x".
        failures.AddRange(
            ValidateExtensionRuleMembers(
                extension,
                extensionProperties,
                context
                    .WithPathPrefix($"{context.PathPrefix}_ext.{extension.Name}.")
                    .ForMemberSelection(extension.MemberSelection)
            )
        );

        return failures;
    }

    private static List<ValidationFailure> ValidateNestedMemberSelection(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        ValidationContext context
    )
    {
        var failures = new List<ValidationFailure>();

        // Server-generated rule names are emitted by the pre-pass in
        // ValidateContentTypeMembers, so skip them here. Schema and member
        // selection checks below intentionally do not look at
        // contentType.Properties: under IncludeAll the projector ignores
        // explicit child Property rules, so emitting "does not exist" warnings
        // for them would be a behavior change unrelated to the namespace rule.
        foreach (var obj in contentType.Objects)
        {
            if (ServerGeneratedFieldNames.Contains(obj.Name))
            {
                continue;
            }

            if (obj.MemberSelection == MemberSelection.IncludeAll || !schemaProperties.ContainsKey(obj.Name))
            {
                continue;
            }

            if (
                schemaProperties[obj.Name] is JsonObject objSchema
                && objSchema["properties"] is JsonObject objProperties
            )
            {
                failures.AddRange(
                    ValidateObjectRuleMembers(
                        obj,
                        objProperties,
                        context.WithPathPrefix($"{obj.Name}.").ForMemberSelection(obj.MemberSelection)
                    )
                );
            }
        }

        foreach (var collection in contentType.Collections)
        {
            if (ServerGeneratedFieldNames.Contains(collection.Name))
            {
                continue;
            }

            if (
                collection.MemberSelection == MemberSelection.IncludeAll
                || !schemaProperties.ContainsKey(collection.Name)
            )
            {
                continue;
            }

            if (
                schemaProperties[collection.Name] is JsonObject collectionObj
                && collectionObj["items"] is JsonObject itemsNode
                && itemsNode["properties"] is JsonObject itemProperties
            )
            {
                failures.AddRange(
                    ValidateCollectionRuleMembers(
                        collection,
                        itemProperties,
                        context
                            .WithPathPrefix($"{collection.Name}[].")
                            .ForMemberSelection(collection.MemberSelection)
                    )
                );
            }
        }

        foreach (var extension in contentType.Extensions)
        {
            if (ServerGeneratedFieldNames.Contains(extension.Name))
            {
                continue;
            }

            // Validate the members of resolved extensions under an IncludeAll parent.
            // Existence of unknown extensions is reported separately by
            // CollectUnknownExtensionFailures, which runs regardless of member selection.
            failures.AddRange(ValidateExtensionRule(extension, schemaProperties, context));
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateObjectRuleMembers(
        ObjectRule objectRule,
        JsonObject schemaProperties,
        ValidationContext context
    )
    {
        if (objectRule.MemberSelection == MemberSelection.IncludeAll)
        {
            return [];
        }

        var failures = new List<ValidationFailure>();
        var containerName = $"object '{objectRule.Name}'";

        // Validate properties in the object
        if (objectRule.Properties is not null)
        {
            failures.AddRange(
                ValidateNestedPropertyRules(objectRule.Properties, schemaProperties, context, containerName)
            );
        }

        // Validate nested objects (recursive)
        if (objectRule.NestedObjects is not null)
        {
            failures.AddRange(
                ValidateNestedObjectRules(objectRule.NestedObjects, schemaProperties, context, containerName)
            );
        }

        // Validate collections in the object
        if (objectRule.Collections is not null)
        {
            failures.AddRange(
                ValidateNestedCollectionRules(
                    objectRule.Collections,
                    schemaProperties,
                    context,
                    containerName
                )
            );
        }

        // Validate extensions declared inside the object. Mirrors
        // ValidateExtensionRuleMembers' completeness so server-generated names
        // and existence errors apply on every branch the parser populates.
        if (objectRule.Extensions is not null)
        {
            foreach (var extension in objectRule.Extensions)
            {
                failures.AddRange(ValidateExtensionRule(extension, schemaProperties, context));
            }
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateNestedPropertyRules(
        IEnumerable<PropertyRule> properties,
        JsonObject schemaProperties,
        ValidationContext context,
        string containerName
    )
    {
        var failures = new List<ValidationFailure>();

        failures.AddRange(
            properties.SelectMany(property =>
            {
                if (ServerGeneratedFieldNames.Contains(property.Name))
                {
                    return Enumerable.Empty<ValidationFailure>();
                }

                var memberPath = $"$.{context.PathPrefix}{property.Name}";
                var propertyFailures = new List<ValidationFailure>();

                if (!schemaProperties.ContainsKey(property.Name))
                {
                    propertyFailures.Add(
                        new ValidationFailure(
                            context.Severity,
                            context.ProfileName,
                            context.ResourceName,
                            $"{context.PathPrefix}{property.Name}",
                            $"Property '{property.Name}' in {containerName} ({context.ContentTypeName} content type) does not exist."
                        )
                    );
                }
                else if (context.IdentityPaths?.Contains(memberPath) == true)
                {
                    propertyFailures.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            context.ProfileName,
                            context.ResourceName,
                            $"{context.PathPrefix}{property.Name}",
                            $"Property '{property.Name}' in {containerName} ({context.ContentTypeName} content type) is an identity member and cannot be excluded."
                        )
                    );
                }

                return propertyFailures;
            })
        );

        return failures;
    }

    private static List<ValidationFailure> ValidateNestedObjectRules(
        IEnumerable<ObjectRule> nestedObjects,
        JsonObject schemaProperties,
        ValidationContext context,
        string containerName
    )
    {
        var failures = new List<ValidationFailure>();

        foreach (var nestedObj in nestedObjects)
        {
            if (ServerGeneratedFieldNames.Contains(nestedObj.Name))
            {
                continue;
            }

            if (!schemaProperties.ContainsKey(nestedObj.Name))
            {
                failures.Add(
                    new ValidationFailure(
                        context.Severity,
                        context.ProfileName,
                        context.ResourceName,
                        $"{context.PathPrefix}{nestedObj.Name}",
                        $"Nested object '{nestedObj.Name}' in {containerName} ({context.ContentTypeName} content type) does not exist."
                    )
                );
                continue;
            }

            if (
                nestedObj.MemberSelection != MemberSelection.IncludeAll
                && schemaProperties[nestedObj.Name] is JsonObject nestedObjSchema
                && nestedObjSchema["properties"] is JsonObject nestedProperties
            )
            {
                failures.AddRange(
                    ValidateObjectRuleMembers(
                        nestedObj,
                        nestedProperties,
                        context
                            .WithPathPrefix($"{context.PathPrefix}{nestedObj.Name}.")
                            .ForMemberSelection(nestedObj.MemberSelection)
                    )
                );
            }
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateNestedCollectionRules(
        IEnumerable<CollectionRule> collections,
        JsonObject schemaProperties,
        ValidationContext context,
        string containerName
    )
    {
        var failures = new List<ValidationFailure>();

        foreach (var collection in collections)
        {
            if (ServerGeneratedFieldNames.Contains(collection.Name))
            {
                continue;
            }

            if (!schemaProperties.ContainsKey(collection.Name))
            {
                failures.Add(
                    new ValidationFailure(
                        context.Severity,
                        context.ProfileName,
                        context.ResourceName,
                        $"{context.PathPrefix}{collection.Name}",
                        $"Collection '{collection.Name}' in {containerName} ({context.ContentTypeName} content type) does not exist."
                    )
                );
                continue;
            }

            if (
                collection.MemberSelection != MemberSelection.IncludeAll
                && schemaProperties[collection.Name] is JsonObject collectionObj
                && collectionObj["items"] is JsonObject itemsNode
                && itemsNode["properties"] is JsonObject itemProperties
            )
            {
                failures.AddRange(
                    ValidateCollectionRuleMembers(
                        collection,
                        itemProperties,
                        context
                            .WithPathPrefix($"{context.PathPrefix}{collection.Name}[].")
                            .ForMemberSelection(collection.MemberSelection)
                    )
                );
            }
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateCollectionRuleMembers(
        CollectionRule collectionRule,
        JsonObject itemSchemaProperties,
        ValidationContext context
    )
    {
        if (collectionRule.MemberSelection == MemberSelection.IncludeAll)
        {
            return [];
        }

        var failures = new List<ValidationFailure>();
        var containerName = $"collection '{collectionRule.Name}'";

        // Validate properties in collection items
        if (collectionRule.Properties is not null)
        {
            failures.AddRange(
                ValidateNestedPropertyRules(
                    collectionRule.Properties,
                    itemSchemaProperties,
                    context,
                    containerName
                )
            );
        }

        // Validate nested objects in collection items
        if (collectionRule.NestedObjects is not null)
        {
            failures.AddRange(
                ValidateNestedObjectRules(
                    collectionRule.NestedObjects,
                    itemSchemaProperties,
                    context,
                    containerName
                )
            );
        }

        // Validate nested collections in collection items
        if (collectionRule.NestedCollections is not null)
        {
            failures.AddRange(
                ValidateNestedCollectionRules(
                    collectionRule.NestedCollections,
                    itemSchemaProperties,
                    context,
                    containerName
                )
            );
        }

        // Validate extensions declared inside collection items
        if (collectionRule.Extensions is not null)
        {
            foreach (var extension in collectionRule.Extensions)
            {
                failures.AddRange(ValidateExtensionRule(extension, itemSchemaProperties, context));
            }
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateExtensionRuleMembers(
        ExtensionRule extensionRule,
        JsonObject extensionSchemaProperties,
        ValidationContext context
    )
    {
        if (extensionRule.MemberSelection == MemberSelection.IncludeAll)
        {
            return [];
        }

        var failures = new List<ValidationFailure>();
        var containerName = $"extension '{extensionRule.Name}'";

        // Validate properties in the extension
        if (extensionRule.Properties is not null)
        {
            failures.AddRange(
                ValidateNestedPropertyRules(
                    extensionRule.Properties,
                    extensionSchemaProperties,
                    context,
                    containerName
                )
            );
        }

        // Validate objects in the extension
        if (extensionRule.Objects is not null)
        {
            failures.AddRange(
                ValidateNestedObjectRules(
                    extensionRule.Objects,
                    extensionSchemaProperties,
                    context,
                    containerName
                )
            );
        }

        // Validate collections in the extension
        if (extensionRule.Collections is not null)
        {
            failures.AddRange(
                ValidateNestedCollectionRules(
                    extensionRule.Collections,
                    extensionSchemaProperties,
                    context,
                    containerName
                )
            );
        }

        return failures;
    }
}
