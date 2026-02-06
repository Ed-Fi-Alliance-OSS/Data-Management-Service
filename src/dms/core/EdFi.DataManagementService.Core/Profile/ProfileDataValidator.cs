// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Model;
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
        using var scope = logger.BeginScope(
            "Profile validation for '{ProfileName}'",
            profileDefinition.ProfileName
        );

        logger.LogDebug("Starting validation of profile '{ProfileName}'", profileDefinition.ProfileName);

        var failures = ValidateResources(profileDefinition, effectiveApiSchemaProvider);

        var errorCount = failures.Count(f => f.Severity == ValidationSeverity.Error);
        var warningCount = failures.Count(f => f.Severity == ValidationSeverity.Warning);

        if (failures.Count == 0)
        {
            logger.LogDebug(
                "Profile '{ProfileName}' validation successful - no errors or warnings",
                profileDefinition.ProfileName
            );
            return ProfileValidationResult.Success;
        }

        logger.LogInformation(
            "Profile '{ProfileName}' validation completed with {ErrorCount} errors and {WarningCount} warnings",
            profileDefinition.ProfileName,
            errorCount,
            warningCount
        );

        // Log individual failures
        foreach (var failure in failures)
        {
            var logLevel = failure.Severity == ValidationSeverity.Error ? LogLevel.Error : LogLevel.Warning;
            logger.Log(
                logLevel,
                "Profile validation {Severity}: {Message} (Profile: {ProfileName}, Resource: {ResourceName}, Member: {MemberName})",
                failure.Severity.ToString().ToLower(),
                failure.Message,
                failure.ProfileName,
                failure.ResourceName ?? "N/A",
                failure.MemberName ?? "N/A"
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
        var projectSchemas = GetProjectSchemas(apiSchemaDocuments).ToList();

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
        var failures = new List<ValidationFailure>();

        // Validate IncludeOnly mode - check for non-existent members (errors)
        if (contentType.MemberSelection == MemberSelection.IncludeOnly)
        {
            failures.AddRange(
                ValidateIncludeOnlyMembers(
                    contentType,
                    schemaProperties,
                    profileName,
                    resourceName,
                    contentTypeName
                )
            );
        }
        // Validate ExcludeOnly mode - check for non-existent members and identity exclusions (warnings)
        else if (contentType.MemberSelection == MemberSelection.ExcludeOnly)
        {
            failures.AddRange(
                ValidateExcludeOnlyMembers(
                    contentType,
                    schemaProperties,
                    profileName,
                    resourceName,
                    contentTypeName,
                    resourceSchema
                )
            );
        }
        // Even with IncludeAll at top level, validate nested structures with member selection
        else if (contentType.MemberSelection == MemberSelection.IncludeAll)
        {
            failures.AddRange(
                ValidateNestedMemberSelection(
                    contentType,
                    schemaProperties,
                    profileName,
                    resourceName,
                    contentTypeName,
                    resourceSchema
                )
            );
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateIncludeOnlyMembers(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName
    )
    {
        var failures = new List<ValidationFailure>();

        // Validate properties
        failures.AddRange(
            contentType
                .Properties.Where(property => !schemaProperties.ContainsKey(property.Name))
                .Select(property => new ValidationFailure(
                    ValidationSeverity.Error,
                    profileName,
                    resourceName,
                    property.Name,
                    $"Property '{property.Name}' in {contentTypeName} content type does not exist in resource '{resourceName}'."
                ))
        );

        // Validate objects (nested objects)
        foreach (var obj in contentType.Objects)
        {
            if (!schemaProperties.ContainsKey(obj.Name))
            {
                failures.Add(
                    new ValidationFailure(
                        ValidationSeverity.Error,
                        profileName,
                        resourceName,
                        obj.Name,
                        $"Object '{obj.Name}' in {contentTypeName} content type does not exist in resource '{resourceName}'."
                    )
                );
            }
            else
            {
                // Recursively validate nested object members
                var nestedSchemaNode = schemaProperties[obj.Name];
                if (nestedSchemaNode is JsonObject nestedObj)
                {
                    var nestedProperties = nestedObj["properties"] as JsonObject;
                    if (nestedProperties is not null)
                    {
                        failures.AddRange(
                            ValidateObjectRuleMembers(
                                obj,
                                nestedProperties,
                                profileName,
                                resourceName,
                                contentTypeName,
                                null, // No ResourceSchema for IncludeOnly nested validation
                                $"{obj.Name}."
                            )
                        );
                    }
                }
            }
        }

        // Validate collections
        failures.AddRange(
            contentType
                .Collections.Where(collection => !schemaProperties.ContainsKey(collection.Name))
                .Select(collection => new ValidationFailure(
                    ValidationSeverity.Error,
                    profileName,
                    resourceName,
                    collection.Name,
                    $"Collection '{collection.Name}' in {contentTypeName} content type does not exist in resource '{resourceName}'."
                ))
        );

        // Recursively validate collection item members if they have IncludeOnly
        failures.AddRange(
            contentType
                .Collections.Where(collection => schemaProperties.ContainsKey(collection.Name))
                .SelectMany(collection =>
                {
                    var collectionSchemaNode = schemaProperties[collection.Name];
                    if (
                        collection.MemberSelection == MemberSelection.IncludeOnly
                        && collectionSchemaNode is JsonObject collectionObj
                    )
                    {
                        var itemsNode = collectionObj["items"] as JsonObject;
                        var itemProperties = itemsNode?["properties"] as JsonObject;
                        if (itemProperties is not null)
                        {
                            return ValidateCollectionRuleMembers(
                                collection,
                                itemProperties,
                                profileName,
                                resourceName,
                                contentTypeName,
                                null, // No ResourceSchema for IncludeOnly nested validation
                                $"{collection.Name}[]."
                            );
                        }
                    }
                    return [];
                })
        );

        // Validate extensions
        foreach (var extension in contentType.Extensions)
        {
            if (!schemaProperties.ContainsKey("_ext"))
            {
                failures.Add(
                    new ValidationFailure(
                        ValidationSeverity.Error,
                        profileName,
                        resourceName,
                        extension.Name,
                        $"Extension '{extension.Name}' in {contentTypeName} content type cannot be validated - resource has no _ext property."
                    )
                );
            }
            else
            {
                var extNode = schemaProperties["_ext"] as JsonObject;
                var extProperties = extNode?["properties"] as JsonObject;
                var extensionSchemaNode = extProperties?[extension.Name] as JsonObject;
                var extensionProperties = extensionSchemaNode?["properties"] as JsonObject;

                if (extensionProperties is null)
                {
                    failures.Add(
                        new ValidationFailure(
                            ValidationSeverity.Error,
                            profileName,
                            resourceName,
                            extension.Name,
                            $"Extension '{extension.Name}' in {contentTypeName} content type does not exist in resource '{resourceName}'."
                        )
                    );
                }
                else
                {
                    failures.AddRange(
                        ValidateExtensionRuleMembers(
                            extension,
                            extensionProperties,
                            profileName,
                            resourceName,
                            contentTypeName,
                            null, // No ResourceSchema for IncludeOnly nested validation
                            $"_ext.{extension.Name}."
                        )
                    );
                }
            }
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateExcludeOnlyMembers(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName,
        ResourceSchema resourceSchema
    )
    {
        var failures = new List<ValidationFailure>();

        // Get identity paths for checking if excluded members are identity members
        var identityPaths = GetIdentityPaths(resourceSchema);

        // Validate properties
        failures.AddRange(
            contentType.Properties.SelectMany(property =>
            {
                var memberPath = $"$.{property.Name}";
                var warnings = new List<ValidationFailure>();

                // Check if property exists
                if (!schemaProperties.ContainsKey(property.Name))
                {
                    warnings.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            profileName,
                            resourceName,
                            property.Name,
                            $"Property '{property.Name}' in {contentTypeName} content type ExcludeOnly does not exist in resource '{resourceName}'."
                        )
                    );
                }
                // Check if trying to exclude an identity member
                else if (identityPaths?.Contains(memberPath) == true)
                {
                    warnings.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            profileName,
                            resourceName,
                            property.Name,
                            $"Property '{property.Name}' in {contentTypeName} content type ExcludeOnly is an identity member and cannot be excluded."
                        )
                    );
                }

                return warnings;
            })
        );

        // Validate objects
        failures.AddRange(
            contentType.Objects.SelectMany(obj =>
            {
                var warnings = new List<ValidationFailure>();

                // Check if object exists
                if (!schemaProperties.ContainsKey(obj.Name))
                {
                    warnings.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            profileName,
                            resourceName,
                            obj.Name,
                            $"Object '{obj.Name}' in {contentTypeName} content type ExcludeOnly does not exist in resource '{resourceName}'."
                        )
                    );
                }
                // Recursively validate if object has member selection
                else if (obj.MemberSelection != MemberSelection.IncludeAll)
                {
                    var objSchemaNode = schemaProperties[obj.Name];
                    if (objSchemaNode is JsonObject objSchema)
                    {
                        var objProperties = objSchema["properties"] as JsonObject;
                        if (objProperties is not null)
                        {
                            warnings.AddRange(
                                ValidateObjectRuleMembers(
                                    obj,
                                    objProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{obj.Name}."
                                )
                            );
                        }
                    }
                }

                return warnings;
            })
        );

        // Validate collections
        failures.AddRange(
            contentType.Collections.SelectMany(collection =>
            {
                var warnings = new List<ValidationFailure>();

                // Check if collection exists
                if (!schemaProperties.ContainsKey(collection.Name))
                {
                    warnings.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            profileName,
                            resourceName,
                            collection.Name,
                            $"Collection '{collection.Name}' in {contentTypeName} content type ExcludeOnly does not exist in resource '{resourceName}'."
                        )
                    );
                }
                // Recursively validate if collection has member selection
                else if (collection.MemberSelection != MemberSelection.IncludeAll)
                {
                    var collectionSchemaNode = schemaProperties[collection.Name];
                    if (collectionSchemaNode is JsonObject collectionObj)
                    {
                        var itemsNode = collectionObj["items"] as JsonObject;
                        var itemProperties = itemsNode?["properties"] as JsonObject;
                        if (itemProperties is not null)
                        {
                            warnings.AddRange(
                                ValidateCollectionRuleMembers(
                                    collection,
                                    itemProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{collection.Name}[]."
                                )
                            );
                        }
                    }
                }

                return warnings;
            })
        );

        // Validate extensions
        failures.AddRange(
            contentType.Extensions.SelectMany(extension =>
            {
                var warnings = new List<ValidationFailure>();

                if (!schemaProperties.ContainsKey("_ext"))
                {
                    warnings.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            profileName,
                            resourceName,
                            extension.Name,
                            $"Extension '{extension.Name}' in {contentTypeName} content type ExcludeOnly cannot be validated - resource has no _ext property."
                        )
                    );
                }
                else
                {
                    var extNode = schemaProperties["_ext"] as JsonObject;
                    var extProperties = extNode?["properties"] as JsonObject;
                    var extensionSchemaNode = extProperties?[extension.Name] as JsonObject;

                    if (extensionSchemaNode is null)
                    {
                        warnings.Add(
                            new ValidationFailure(
                                ValidationSeverity.Warning,
                                profileName,
                                resourceName,
                                extension.Name,
                                $"Extension '{extension.Name}' in {contentTypeName} content type ExcludeOnly does not exist in resource '{resourceName}'."
                            )
                        );
                    }
                    // Recursively validate if extension has member selection
                    else if (extension.MemberSelection != MemberSelection.IncludeAll)
                    {
                        var extensionSchemaProperties = extensionSchemaNode["properties"] as JsonObject;
                        if (extensionSchemaProperties is not null)
                        {
                            warnings.AddRange(
                                ValidateExtensionRuleMembers(
                                    extension,
                                    extensionSchemaProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"_ext.{extension.Name}."
                                )
                            );
                        }
                    }
                }

                return warnings;
            })
        );

        return failures;
    }

    private static List<ValidationFailure> ValidateNestedMemberSelection(
        ContentTypeDefinition contentType,
        JsonObject schemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName,
        ResourceSchema resourceSchema
    )
    {
        var failures = new List<ValidationFailure>();

        // Validate nested objects with member selection
        foreach (var obj in contentType.Objects)
        {
            if (obj.MemberSelection != MemberSelection.IncludeAll && schemaProperties.ContainsKey(obj.Name))
            {
                var objSchemaNode = schemaProperties[obj.Name];
                if (objSchemaNode is JsonObject objSchema)
                {
                    var objProperties = objSchema["properties"] as JsonObject;
                    if (objProperties is not null)
                    {
                        failures.AddRange(
                            ValidateObjectRuleMembers(
                                obj,
                                objProperties,
                                profileName,
                                resourceName,
                                contentTypeName,
                                resourceSchema,
                                $"{obj.Name}."
                            )
                        );
                    }
                }
            }
        }

        // Validate nested collections with member selection
        foreach (var collection in contentType.Collections)
        {
            if (
                collection.MemberSelection != MemberSelection.IncludeAll
                && schemaProperties.ContainsKey(collection.Name)
            )
            {
                var collectionSchemaNode = schemaProperties[collection.Name];
                if (collectionSchemaNode is JsonObject collectionObj)
                {
                    var itemsNode = collectionObj["items"] as JsonObject;
                    var itemProperties = itemsNode?["properties"] as JsonObject;
                    if (itemProperties is not null)
                    {
                        failures.AddRange(
                            ValidateCollectionRuleMembers(
                                collection,
                                itemProperties,
                                profileName,
                                resourceName,
                                contentTypeName,
                                resourceSchema,
                                $"{collection.Name}[]."
                            )
                        );
                    }
                }
            }
        }

        // Validate extensions with member selection
        foreach (var extension in contentType.Extensions)
        {
            if (extension.MemberSelection != MemberSelection.IncludeAll)
            {
                if (!schemaProperties.ContainsKey("_ext"))
                {
                    // No _ext property means no extensions exist
                    var severity = GetValidationSeverity(extension.MemberSelection);
                    failures.Add(
                        new ValidationFailure(
                            severity,
                            profileName,
                            resourceName,
                            $"_ext.{extension.Name}",
                            $"Extension '{extension.Name}' in {contentTypeName} content type does not exist in resource '{resourceName}'."
                        )
                    );
                }
                else
                {
                    var extNode = schemaProperties["_ext"] as JsonObject;
                    var extProperties = extNode?["properties"] as JsonObject;
                    var extensionSchemaNode = extProperties?[extension.Name] as JsonObject;

                    if (extensionSchemaNode is null)
                    {
                        // Extension doesn't exist in schema
                        var severity = GetValidationSeverity(extension.MemberSelection);
                        failures.Add(
                            new ValidationFailure(
                                severity,
                                profileName,
                                resourceName,
                                $"_ext.{extension.Name}",
                                $"Extension '{extension.Name}' in {contentTypeName} content type does not exist in resource '{resourceName}'."
                            )
                        );
                    }
                    else
                    {
                        var extensionSchemaProperties = extensionSchemaNode["properties"] as JsonObject;
                        if (extensionSchemaProperties is not null)
                        {
                            failures.AddRange(
                                ValidateExtensionRuleMembers(
                                    extension,
                                    extensionSchemaProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"_ext.{extension.Name}."
                                )
                            );
                        }
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// Gets identity paths from the resource schema as a HashSet for efficient lookup.
    /// Returns null if the resource schema is null.
    /// </summary>
    private static HashSet<string>? GetIdentityPaths(ResourceSchema? resourceSchema)
    {
        return resourceSchema?.IdentityJsonPaths.Select(jp => jp.Value).ToHashSet();
    }

    /// <summary>
    /// Gets the validation severity based on the member selection mode.
    /// IncludeOnly returns Error, ExcludeOnly returns Warning.
    /// </summary>
    private static ValidationSeverity GetValidationSeverity(MemberSelection memberSelection)
    {
        return memberSelection == MemberSelection.IncludeOnly
            ? ValidationSeverity.Error
            : ValidationSeverity.Warning;
    }

    /// <summary>
    /// Validates properties against schema, checking for existence and identity member exclusions.
    /// </summary>
    private static List<ValidationFailure> ValidateProperties(
        IEnumerable<PropertyRule> properties,
        JsonObject schemaProperties,
        ValidationSeverity severity,
        HashSet<string>? identityPaths,
        string profileName,
        string resourceName,
        string pathPrefix,
        string containerName,
        string contentTypeName
    )
    {
        return properties
            .SelectMany(property =>
            {
                var propertyFailures = new List<ValidationFailure>();
                var memberPath = $"$.{pathPrefix}{property.Name}";

                if (!schemaProperties.ContainsKey(property.Name))
                {
                    propertyFailures.Add(
                        new ValidationFailure(
                            severity,
                            profileName,
                            resourceName,
                            $"{pathPrefix}{property.Name}",
                            $"Property '{property.Name}' in {containerName} ({contentTypeName} content type) does not exist."
                        )
                    );
                }
                else if (identityPaths?.Contains(memberPath) == true)
                {
                    propertyFailures.Add(
                        new ValidationFailure(
                            ValidationSeverity.Warning,
                            profileName,
                            resourceName,
                            $"{pathPrefix}{property.Name}",
                            $"Property '{property.Name}' in {containerName} ({contentTypeName} content type) is an identity member and cannot be excluded."
                        )
                    );
                }

                return propertyFailures;
            })
            .ToList();
    }

    private static List<ValidationFailure> ValidateObjectRuleMembers(
        ObjectRule objectRule,
        JsonObject schemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName,
        ResourceSchema? resourceSchema,
        string pathPrefix
    )
    {
        var failures = new List<ValidationFailure>();

        if (objectRule.MemberSelection == MemberSelection.IncludeAll)
        {
            return failures;
        }

        var severity = GetValidationSeverity(objectRule.MemberSelection);

        // Get identity paths if validating ExcludeOnly
        var identityPaths =
            objectRule.MemberSelection == MemberSelection.ExcludeOnly
                ? GetIdentityPaths(resourceSchema)
                : null;

        // Validate properties in the object
        if (objectRule.Properties is not null)
        {
            failures.AddRange(
                ValidateProperties(
                    objectRule.Properties,
                    schemaProperties,
                    severity,
                    identityPaths,
                    profileName,
                    resourceName,
                    pathPrefix,
                    $"object '{objectRule.Name}'",
                    contentTypeName
                )
            );
        }

        // Validate nested objects (recursive)
        if (objectRule.NestedObjects is not null)
        {
            failures.AddRange(
                objectRule.NestedObjects.SelectMany(nestedObj =>
                {
                    if (!schemaProperties.ContainsKey(nestedObj.Name))
                    {
                        return
                        [
                            new ValidationFailure(
                                severity,
                                profileName,
                                resourceName,
                                $"{pathPrefix}{nestedObj.Name}",
                                $"Nested object '{nestedObj.Name}' in object '{objectRule.Name}' ({contentTypeName} content type) does not exist."
                            ),
                        ];
                    }

                    // Recursively validate if nested object has member selection
                    if (nestedObj.MemberSelection != MemberSelection.IncludeAll)
                    {
                        var nestedSchemaNode = schemaProperties[nestedObj.Name];
                        if (nestedSchemaNode is JsonObject nestedObjSchema)
                        {
                            var nestedProperties = nestedObjSchema["properties"] as JsonObject;
                            if (nestedProperties is not null)
                            {
                                return ValidateObjectRuleMembers(
                                    nestedObj,
                                    nestedProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{pathPrefix}{nestedObj.Name}."
                                );
                            }
                        }
                    }

                    return [];
                })
            );
        }

        // Validate collections in the object
        if (objectRule.Collections is not null)
        {
            failures.AddRange(
                objectRule.Collections.SelectMany(collection =>
                {
                    if (!schemaProperties.ContainsKey(collection.Name))
                    {
                        return
                        [
                            new ValidationFailure(
                                severity,
                                profileName,
                                resourceName,
                                $"{pathPrefix}{collection.Name}",
                                $"Collection '{collection.Name}' in object '{objectRule.Name}' ({contentTypeName} content type) does not exist."
                            ),
                        ];
                    }

                    // Recursively validate if collection has member selection
                    if (collection.MemberSelection != MemberSelection.IncludeAll)
                    {
                        var collectionSchemaNode = schemaProperties[collection.Name];
                        if (collectionSchemaNode is JsonObject collectionObj)
                        {
                            var itemsNode = collectionObj["items"] as JsonObject;
                            var itemProperties = itemsNode?["properties"] as JsonObject;
                            if (itemProperties is not null)
                            {
                                return ValidateCollectionRuleMembers(
                                    collection,
                                    itemProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{pathPrefix}{collection.Name}[]."
                                );
                            }
                        }
                    }

                    return [];
                })
            );
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateCollectionRuleMembers(
        CollectionRule collectionRule,
        JsonObject itemSchemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName,
        ResourceSchema? resourceSchema,
        string pathPrefix
    )
    {
        var failures = new List<ValidationFailure>();

        if (collectionRule.MemberSelection == MemberSelection.IncludeAll)
        {
            return failures;
        }

        var severity = GetValidationSeverity(collectionRule.MemberSelection);

        // Get identity paths if validating ExcludeOnly
        var identityPaths =
            collectionRule.MemberSelection == MemberSelection.ExcludeOnly
                ? GetIdentityPaths(resourceSchema)
                : null;

        // Validate properties in collection items
        if (collectionRule.Properties is not null)
        {
            failures.AddRange(
                ValidateProperties(
                    collectionRule.Properties,
                    itemSchemaProperties,
                    severity,
                    identityPaths,
                    profileName,
                    resourceName,
                    pathPrefix,
                    $"collection '{collectionRule.Name}'",
                    contentTypeName
                )
            );
        }

        // Validate nested objects in collection items
        if (collectionRule.NestedObjects is not null)
        {
            failures.AddRange(
                collectionRule.NestedObjects.SelectMany(nestedObj =>
                {
                    if (!itemSchemaProperties.ContainsKey(nestedObj.Name))
                    {
                        return
                        [
                            new ValidationFailure(
                                severity,
                                profileName,
                                resourceName,
                                $"{pathPrefix}{nestedObj.Name}",
                                $"Nested object '{nestedObj.Name}' in collection '{collectionRule.Name}' ({contentTypeName} content type) does not exist."
                            ),
                        ];
                    }

                    // Recursively validate if nested object has member selection
                    if (nestedObj.MemberSelection != MemberSelection.IncludeAll)
                    {
                        var nestedSchemaNode = itemSchemaProperties[nestedObj.Name];
                        if (nestedSchemaNode is JsonObject nestedObjSchema)
                        {
                            var nestedProperties = nestedObjSchema["properties"] as JsonObject;
                            if (nestedProperties is not null)
                            {
                                return ValidateObjectRuleMembers(
                                    nestedObj,
                                    nestedProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{pathPrefix}{nestedObj.Name}."
                                );
                            }
                        }
                    }

                    return [];
                })
            );
        }

        return failures;
    }

    private static List<ValidationFailure> ValidateExtensionRuleMembers(
        ExtensionRule extensionRule,
        JsonObject extensionSchemaProperties,
        string profileName,
        string resourceName,
        string contentTypeName,
        ResourceSchema? resourceSchema,
        string pathPrefix
    )
    {
        var failures = new List<ValidationFailure>();

        if (extensionRule.MemberSelection == MemberSelection.IncludeAll)
        {
            return failures;
        }

        var severity = GetValidationSeverity(extensionRule.MemberSelection);

        // Get identity paths if validating ExcludeOnly
        var identityPaths =
            extensionRule.MemberSelection == MemberSelection.ExcludeOnly
                ? GetIdentityPaths(resourceSchema)
                : null;

        // Validate properties in the extension
        if (extensionRule.Properties is not null)
        {
            failures.AddRange(
                ValidateProperties(
                    extensionRule.Properties,
                    extensionSchemaProperties,
                    severity,
                    identityPaths,
                    profileName,
                    resourceName,
                    pathPrefix,
                    $"extension '{extensionRule.Name}'",
                    contentTypeName
                )
            );
        }

        // Validate objects in the extension
        if (extensionRule.Objects is not null)
        {
            failures.AddRange(
                extensionRule.Objects.SelectMany(obj =>
                {
                    if (!extensionSchemaProperties.ContainsKey(obj.Name))
                    {
                        return
                        [
                            new ValidationFailure(
                                severity,
                                profileName,
                                resourceName,
                                $"{pathPrefix}{obj.Name}",
                                $"Object '{obj.Name}' in extension '{extensionRule.Name}' ({contentTypeName} content type) does not exist."
                            ),
                        ];
                    }

                    // Recursively validate if object has member selection
                    if (obj.MemberSelection != MemberSelection.IncludeAll)
                    {
                        var objSchemaNode = extensionSchemaProperties[obj.Name];
                        if (objSchemaNode is JsonObject objSchema)
                        {
                            var objProperties = objSchema["properties"] as JsonObject;
                            if (objProperties is not null)
                            {
                                return ValidateObjectRuleMembers(
                                    obj,
                                    objProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{pathPrefix}{obj.Name}."
                                );
                            }
                        }
                    }

                    return [];
                })
            );
        }

        // Validate collections in the extension
        if (extensionRule.Collections is not null)
        {
            failures.AddRange(
                extensionRule.Collections.SelectMany(collection =>
                {
                    if (!extensionSchemaProperties.ContainsKey(collection.Name))
                    {
                        return
                        [
                            new ValidationFailure(
                                severity,
                                profileName,
                                resourceName,
                                $"{pathPrefix}{collection.Name}",
                                $"Collection '{collection.Name}' in extension '{extensionRule.Name}' ({contentTypeName} content type) does not exist."
                            ),
                        ];
                    }

                    // Recursively validate if collection has member selection
                    if (collection.MemberSelection != MemberSelection.IncludeAll)
                    {
                        var collectionSchemaNode = extensionSchemaProperties[collection.Name];
                        if (collectionSchemaNode is JsonObject collectionObj)
                        {
                            var itemsNode = collectionObj["items"] as JsonObject;
                            var itemProperties = itemsNode?["properties"] as JsonObject;
                            if (itemProperties is not null)
                            {
                                return ValidateCollectionRuleMembers(
                                    collection,
                                    itemProperties,
                                    profileName,
                                    resourceName,
                                    contentTypeName,
                                    resourceSchema,
                                    $"{pathPrefix}{collection.Name}[]."
                                );
                            }
                        }
                    }

                    return [];
                })
            );
        }

        return failures;
    }
}
