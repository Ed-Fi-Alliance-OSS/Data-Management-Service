// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

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

        return contentType.MemberSelection switch
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
        };
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

        // Validate properties
        failures.AddRange(
            contentType.Properties.SelectMany(property =>
            {
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

    private static List<ValidationFailure> ValidateExtensionRule(
        ExtensionRule extension,
        JsonObject schemaProperties,
        ValidationContext context
    )
    {
        var failures = new List<ValidationFailure>();

        if (!schemaProperties.ContainsKey("_ext"))
        {
            failures.Add(
                new ValidationFailure(
                    context.Severity,
                    context.ProfileName,
                    context.ResourceName,
                    extension.Name,
                    $"Extension '{extension.Name}' in {context.ContentTypeName} content type cannot be validated - resource has no _ext property."
                )
            );
            return failures;
        }

        var extNode = schemaProperties["_ext"] as JsonObject;
        var extProperties = extNode?["properties"] as JsonObject;
        var extensionSchemaNode = extProperties?[extension.Name] as JsonObject;
        var extensionProperties = extensionSchemaNode?["properties"] as JsonObject;

        if (extensionProperties is null)
        {
            failures.Add(
                new ValidationFailure(
                    context.Severity,
                    context.ProfileName,
                    context.ResourceName,
                    extension.Name,
                    $"Extension '{extension.Name}' in {context.ContentTypeName} content type does not exist in resource '{context.ResourceName}'."
                )
            );
            return failures;
        }

        failures.AddRange(
            ValidateExtensionRuleMembers(
                extension,
                extensionProperties,
                context
                    .WithPathPrefix($"_ext.{extension.Name}.")
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

        // Validate nested objects with member selection
        foreach (
            var obj in contentType.Objects.Where(o =>
                o.MemberSelection != MemberSelection.IncludeAll && schemaProperties.ContainsKey(o.Name)
            )
        )
        {
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

        // Validate nested collections with member selection
        foreach (
            var collection in contentType.Collections.Where(c =>
                c.MemberSelection != MemberSelection.IncludeAll && schemaProperties.ContainsKey(c.Name)
            )
        )
        {
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

        // Validate extensions with member selection
        foreach (
            var extension in contentType.Extensions.Where(e =>
                e.MemberSelection != MemberSelection.IncludeAll
            )
        )
        {
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
