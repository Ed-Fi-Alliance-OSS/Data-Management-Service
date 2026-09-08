// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Compiles shared-descriptor endpoint query capability metadata for one descriptor resource.
/// </summary>
internal sealed class DescriptorQueryCapabilityCompiler
{
    private const string DescriptorQueryCapabilityPlanKind = "descriptor query capability";
    private static readonly StringComparer QueryFieldNameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly DescriptorQueryFieldDefinition[] FieldDefinitions =
    [
        new("id", "$.id", "string"),
        new("namespace", "$.namespace", "string"),
        new("codeValue", "$.codeValue", "string"),
        new("shortDescription", "$.shortDescription", "string"),
        new("description", "$.description", "string"),
        new("effectiveBeginDate", "$.effectiveBeginDate", "date"),
        new("effectiveEndDate", "$.effectiveEndDate", "date"),
    ];
    private static readonly FrozenSet<string> ExpectedFieldNames = FieldDefinitions
        .Select(static field => field.QueryFieldName)
        .ToFrozenSet(QueryFieldNameComparer);

    public static DescriptorQueryCapability Compile(ConcreteResourceModel concreteResourceModel)
    {
        ArgumentNullException.ThrowIfNull(concreteResourceModel);

        var resource = concreteResourceModel.RelationalModel.Resource;

        if (concreteResourceModel.StorageKind is not ResourceStorageKind.SharedDescriptorTable)
        {
            throw new InvalidOperationException(
                $"Cannot compile {DescriptorQueryCapabilityPlanKind}: resource '{resource.ProjectName}.{resource.ResourceName}' "
                    + $"has unsupported storage kind '{concreteResourceModel.StorageKind}'."
            );
        }

        var descriptorMetadata =
            concreteResourceModel.DescriptorMetadata
            ?? throw new InvalidOperationException(
                $"Cannot compile {DescriptorQueryCapabilityPlanKind}: resource '{resource.ProjectName}.{resource.ResourceName}' "
                    + $"uses storage kind '{ResourceStorageKind.SharedDescriptorTable}' but descriptor metadata is missing."
            );

        var supportedFields = CreateSupportedFields(resource, descriptorMetadata.ColumnContract);
        var apiSchemaMismatchReason = TryCreateApiSchemaMismatchReason(concreteResourceModel);

        if (apiSchemaMismatchReason is not null)
        {
            return new DescriptorQueryCapability(
                new DescriptorQuerySupport.Omitted(
                    new DescriptorQueryCapabilityOmission(
                        DescriptorQueryCapabilityOmissionKind.ApiSchemaMismatch,
                        apiSchemaMismatchReason
                    )
                ),
                CreateEmptySupportedFields()
            );
        }

        return new DescriptorQueryCapability(
            new DescriptorQuerySupport.Supported(),
            supportedFields.ToFrozenDictionary(static field => field.QueryFieldName, QueryFieldNameComparer)
        );
    }

    private static IReadOnlyList<SupportedDescriptorQueryField> CreateSupportedFields(
        QualifiedResourceName resource,
        DescriptorColumnContract columnContract
    )
    {
        return
        [
            new("id", new DescriptorQueryFieldTarget.DocumentUuid()),
            new("namespace", new DescriptorQueryFieldTarget.Namespace(columnContract.Namespace)),
            new("codeValue", new DescriptorQueryFieldTarget.CodeValue(columnContract.CodeValue)),
            new(
                "shortDescription",
                new DescriptorQueryFieldTarget.ShortDescription(
                    GetRequiredDescriptorColumn(resource, "shortDescription", columnContract.ShortDescription)
                )
            ),
            new(
                "description",
                new DescriptorQueryFieldTarget.Description(
                    GetRequiredDescriptorColumn(resource, "description", columnContract.Description)
                )
            ),
            new(
                "effectiveBeginDate",
                new DescriptorQueryFieldTarget.EffectiveBeginDate(
                    GetRequiredDescriptorColumn(
                        resource,
                        "effectiveBeginDate",
                        columnContract.EffectiveBeginDate
                    )
                )
            ),
            new(
                "effectiveEndDate",
                new DescriptorQueryFieldTarget.EffectiveEndDate(
                    GetRequiredDescriptorColumn(resource, "effectiveEndDate", columnContract.EffectiveEndDate)
                )
            ),
        ];
    }

    private static DbColumnName GetRequiredDescriptorColumn(
        QualifiedResourceName resource,
        string queryFieldName,
        DbColumnName? column
    )
    {
        return column
            ?? throw new InvalidOperationException(
                $"Cannot compile {DescriptorQueryCapabilityPlanKind}: resource '{resource.ProjectName}.{resource.ResourceName}' "
                    + $"uses storage kind '{ResourceStorageKind.SharedDescriptorTable}' but descriptor metadata column contract is missing "
                    + $"required field '{queryFieldName}'."
            );
    }

    private static string? TryCreateApiSchemaMismatchReason(ConcreteResourceModel concreteResourceModel)
    {
        var collidingQueryFieldGroups = concreteResourceModel
            .QueryFieldMappingsByQueryField.Values.GroupBy(
                static queryFieldMapping => queryFieldMapping.QueryFieldName,
                QueryFieldNameComparer
            )
            .Select(static queryFieldGroup =>
                queryFieldGroup
                    .Select(static queryFieldMapping => queryFieldMapping.QueryFieldName)
                    .OrderBy(static queryFieldName => queryFieldName, StringComparer.Ordinal)
                    .ToArray()
            )
            .Where(static queryFieldGroup => queryFieldGroup.Length > 1)
            .OrderBy(static queryFieldGroup => queryFieldGroup[0], StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (collidingQueryFieldGroups.Length > 0)
        {
            var collisionSummary = string.Join(
                "; ",
                collidingQueryFieldGroups.Select(static queryFieldGroup =>
                    string.Join(", ", queryFieldGroup.Select(static queryFieldName => $"'{queryFieldName}'"))
                )
            );

            return "ApiSchema queryFieldMapping disagrees with the shared descriptor query contract: "
                + $"case-insensitive query field name collisions were found: {collisionSummary}.";
        }

        var queryFieldMappingsByField = concreteResourceModel
            .QueryFieldMappingsByQueryField.Values.OrderBy(
                static queryFieldMapping => queryFieldMapping.QueryFieldName,
                StringComparer.OrdinalIgnoreCase
            )
            .ToDictionary(
                static queryFieldMapping => queryFieldMapping.QueryFieldName,
                static queryFieldMapping => queryFieldMapping,
                QueryFieldNameComparer
            );

        List<string> mismatchSummaries = [];
        List<string> missingFields = [];
        foreach (var fieldDefinition in FieldDefinitions)
        {
            if (
                !queryFieldMappingsByField.TryGetValue(
                    fieldDefinition.QueryFieldName,
                    out var queryFieldMapping
                )
            )
            {
                missingFields.Add(fieldDefinition.QueryFieldName);
                continue;
            }

            if (
                queryFieldMapping.Paths.Count != 1
                || !string.Equals(
                    queryFieldMapping.Paths[0].Path.Canonical,
                    fieldDefinition.ExpectedPath,
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    queryFieldMapping.Paths[0].Type,
                    fieldDefinition.ExpectedType,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                mismatchSummaries.Add(
                    $"field '{fieldDefinition.QueryFieldName}' must map to exactly one path '{fieldDefinition.ExpectedPath}' "
                        + $"with type '{fieldDefinition.ExpectedType}' (found: {FormatQueryFieldMapping(queryFieldMapping)})."
                );
            }
        }

        string[] unexpectedFields = queryFieldMappingsByField
            .Keys.Where(static queryFieldName => !ExpectedFieldNames.Contains(queryFieldName))
            .OrderBy(static queryFieldName => queryFieldName, StringComparer.OrdinalIgnoreCase)
            .Select(static queryFieldName => $"'{queryFieldName}'")
            .ToArray();

        if (missingFields.Count is 0 && mismatchSummaries.Count is 0 && unexpectedFields.Length is 0)
        {
            return null;
        }

        if (missingFields.Count > 0)
        {
            mismatchSummaries.Insert(
                0,
                $"missing fields: {string.Join(", ", missingFields.Select(static queryFieldName => $"'{queryFieldName}'"))}."
            );
        }

        if (unexpectedFields.Length > 0)
        {
            mismatchSummaries.Add($"unexpected fields: {string.Join(", ", unexpectedFields)}.");
        }

        return "ApiSchema queryFieldMapping disagrees with the shared descriptor query contract: "
            + string.Join(" ", mismatchSummaries);
    }

    private static string FormatQueryFieldMapping(RelationalQueryFieldMapping queryFieldMapping)
    {
        return string.Join(
            ", ",
            queryFieldMapping.Paths.Select(static queryFieldPath =>
                $"'{queryFieldPath.Path.Canonical}' ({queryFieldPath.Type})"
            )
        );
    }

    private static IReadOnlyDictionary<string, SupportedDescriptorQueryField> CreateEmptySupportedFields()
    {
        return new Dictionary<string, SupportedDescriptorQueryField>(
            QueryFieldNameComparer
        ).ToFrozenDictionary(QueryFieldNameComparer);
    }

    private sealed record DescriptorQueryFieldDefinition(
        string QueryFieldName,
        string ExpectedPath,
        string ExpectedType
    );
}
