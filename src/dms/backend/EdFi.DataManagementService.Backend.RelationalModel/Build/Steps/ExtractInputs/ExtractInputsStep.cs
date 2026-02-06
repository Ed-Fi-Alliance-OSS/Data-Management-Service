// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs.ApiSchemaNodeRequirements;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;

/// <summary>
/// Extracts the API schema inputs required to build a relational resource model and populates the
/// <see cref="RelationalModelBuilderContext"/> with normalized values and precompiled paths.
/// </summary>
public sealed class ExtractInputsStep : IRelationalModelBuilderStep
{
    /// <summary>
    /// Reads <see cref="RelationalModelBuilderContext.ApiSchemaRoot"/> and the current resource endpoint
    /// name, then populates the context with project metadata, resource metadata, and derived path maps
    /// consumed by subsequent relational model builder steps.
    /// </summary>
    /// <param name="context">The builder context holding the API schema and target resource endpoint.</param>
    public void Execute(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var apiSchemaRoot =
            context.ApiSchemaRoot ?? throw new InvalidOperationException("ApiSchema root must be provided.");

        var projectSchema = RequireObject(apiSchemaRoot["projectSchema"], "projectSchema");
        var projectName = RequireString(projectSchema, "projectName");
        var projectEndpointName = RequireString(projectSchema, "projectEndpointName");
        var projectVersion = RequireString(projectSchema, "projectVersion");

        var resourceEndpointName = context.ResourceEndpointName;
        if (string.IsNullOrWhiteSpace(resourceEndpointName))
        {
            throw new InvalidOperationException("Resource endpoint name must be provided.");
        }

        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );

        if (
            !resourceSchemas.TryGetPropertyValue(resourceEndpointName, out var resourceSchemaNode)
            || resourceSchemaNode is null
        )
        {
            throw new InvalidOperationException(
                $"Resource schema '{resourceEndpointName}' not found in project schema."
            );
        }

        if (resourceSchemaNode is not JsonObject resourceSchema)
        {
            throw new InvalidOperationException(
                $"Expected resource schema '{resourceEndpointName}' to be an object."
            );
        }

        var resourceName = RequireString(resourceSchema, "resourceName");
        var isDescriptor =
            resourceSchema["isDescriptor"]?.GetValue<bool>()
            ?? throw new InvalidOperationException(
                "Expected isDescriptor to be on ResourceSchema, invalid ApiSchema."
            );
        var isResourceExtension = TryGetOptionalBoolean(
            resourceSchema,
            "isResourceExtension",
            defaultValue: false
        );
        var superclassResourceName = RelationalModelSetSchemaHelpers.TryGetOptionalString(
            resourceSchema,
            "superclassResourceName"
        );
        var jsonSchemaForInsert =
            resourceSchema["jsonSchemaForInsert"]
            ?? throw new InvalidOperationException(
                "Expected jsonSchemaForInsert to be on ResourceSchema, invalid ApiSchema."
            );

        var identityJsonPaths = IdentityJsonPathsExtractor.ExtractIdentityJsonPaths(
            resourceSchema,
            projectName,
            resourceName
        );
        var identityJsonPathSet = new HashSet<string>(
            identityJsonPaths.Select(path => path.Canonical),
            StringComparer.Ordinal
        );
        var allowIdentityUpdates = RequireBoolean(resourceSchema, "allowIdentityUpdates");
        var documentReferenceMappings = DocumentReferenceMappingsExtractor.ExtractDocumentReferenceMappings(
            resourceSchema,
            projectName,
            resourceName,
            identityJsonPathSet
        );
        var arrayUniquenessConstraints =
            ArrayUniquenessConstraintsExtractor.ExtractArrayUniquenessConstraints(
                resourceSchema,
                projectName,
                resourceName
            );
        ArrayUniquenessConstraintsExtractor.ValidateArrayUniquenessReferenceIdentityCompleteness(
            arrayUniquenessConstraints,
            documentReferenceMappings,
            projectName,
            resourceName
        );
        var relationalObject = RelationalOverridesExtractor.GetRelationalObject(
            resourceSchema,
            projectName,
            resourceName,
            isDescriptor
        );
        var rootTableNameOverride = RelationalOverridesExtractor.ExtractRootTableNameOverride(
            relationalObject,
            isResourceExtension,
            projectName,
            resourceName
        );
        var nameOverrides = RelationalOverridesExtractor.ExtractNameOverrides(
            relationalObject,
            documentReferenceMappings,
            isResourceExtension,
            jsonSchemaForInsert,
            projectEndpointName,
            projectName,
            resourceName
        );
        var descriptorPathsByJsonPath = context.DescriptorPathSource switch
        {
            DescriptorPathSource.Precomputed => context.DescriptorPathsByJsonPath,
            _ => DescriptorPathsExtractor.ExtractDescriptorPaths(resourceSchema, projectSchema, projectName),
        };
        var decimalPropertyValidationInfosByPath =
            DecimalPropertyValidationInfosExtractor.ExtractDecimalPropertyValidationInfos(resourceSchema);
        var stringMaxLengthOmissionPaths = new HashSet<string>(StringComparer.Ordinal);

        context.ProjectName = projectName;
        context.ProjectEndpointName = projectEndpointName;
        context.ProjectVersion = projectVersion;
        context.ResourceName = resourceName;
        context.SuperclassResourceName = superclassResourceName;
        context.RootTableNameOverride = rootTableNameOverride;
        context.IsDescriptorResource = isDescriptor;
        context.JsonSchemaForInsert = jsonSchemaForInsert;
        context.IdentityJsonPaths = identityJsonPaths;
        context.AllowIdentityUpdates = allowIdentityUpdates;
        context.DocumentReferenceMappings = documentReferenceMappings;
        context.ArrayUniquenessConstraints = arrayUniquenessConstraints;
        context.NameOverridesByPath = nameOverrides;
        context.DescriptorPathsByJsonPath = descriptorPathsByJsonPath;
        context.DecimalPropertyValidationInfosByPath = decimalPropertyValidationInfosByPath;
        context.StringMaxLengthOmissionPaths = stringMaxLengthOmissionPaths;
    }
}
