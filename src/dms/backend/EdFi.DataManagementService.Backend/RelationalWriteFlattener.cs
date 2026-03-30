// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend;

internal interface IRelationalWriteFlattener
{
    FlattenedWriteSet Flatten(FlatteningInput flatteningInput);
}

internal sealed class RelationalWriteFlattener : IRelationalWriteFlattener
{
    public FlattenedWriteSet Flatten(FlatteningInput flatteningInput)
    {
        ArgumentNullException.ThrowIfNull(flatteningInput);

        var writePlan = flatteningInput.WritePlan;
        var rootTablePlan = GetRootTablePlan(writePlan);
        var selectedBodyIndex = SelectedBodyIndex.Create(flatteningInput.SelectedBody);
        var resolvedReferenceLookups = FlatteningResolvedReferenceLookupSet.Create(
            writePlan,
            flatteningInput.ResolvedReferences
        );
        var rootDocumentIdValue = ResolveRootDocumentIdValue(flatteningInput.TargetContext);

        var rootRow = new RootWriteRowBuffer(
            tableWritePlan: rootTablePlan,
            values: MaterializeValues(
                flatteningInput,
                rootTablePlan,
                selectedBodyIndex,
                resolvedReferenceLookups,
                parentKeyParts: []
            ),
            nonCollectionRows: MaterializeRootExtensionRows(
                flatteningInput,
                selectedBodyIndex,
                resolvedReferenceLookups,
                rootDocumentIdValue
            ),
            collectionCandidates: []
        );

        return new FlattenedWriteSet(rootRow);
    }

    private static TableWritePlan GetRootTablePlan(ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        var rootPlans = writePlan
            .TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.Root
            )
            .Take(2)
            .ToArray();

        return rootPlans.Length switch
        {
            1 => rootPlans[0],
            0 => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' does not contain a root table plan."
            ),
            _ => throw new InvalidOperationException(
                $"Write plan for resource '{RelationalWriteSupport.FormatResource(writePlan.Model.Resource)}' contains multiple root table plans."
            ),
        };
    }

    private static IEnumerable<StandaloneScopeWriteRowBuffer> MaterializeRootExtensionRows(
        FlatteningInput flatteningInput,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        FlattenedWriteValue rootDocumentIdValue
    )
    {
        var rootParentKeyParts = new[] { rootDocumentIdValue };

        foreach (
            var tableWritePlan in flatteningInput.WritePlan.TablePlansInDependencyOrder.Where(static plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.RootExtension
            )
        )
        {
            if (!selectedBodyIndex.HasBoundDataForScope(tableWritePlan.TableModel.JsonScope.Canonical))
            {
                continue;
            }

            yield return new StandaloneScopeWriteRowBuffer(
                tableWritePlan,
                MaterializeValues(
                    flatteningInput,
                    tableWritePlan,
                    selectedBodyIndex,
                    resolvedReferenceLookups,
                    rootParentKeyParts
                ),
                nonCollectionRows: [],
                collectionCandidates: []
            );
        }
    }

    private static IReadOnlyList<FlattenedWriteValue> MaterializeValues(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts
    )
    {
        FlattenedWriteValue[] values = new FlattenedWriteValue[tableWritePlan.ColumnBindings.Length];

        for (var bindingIndex = 0; bindingIndex < tableWritePlan.ColumnBindings.Length; bindingIndex++)
        {
            values[bindingIndex] = MaterializeValue(
                flatteningInput,
                tableWritePlan,
                tableWritePlan.ColumnBindings[bindingIndex],
                selectedBodyIndex,
                resolvedReferenceLookups,
                parentKeyParts
            );
        }

        return values;
    }

    private static FlattenedWriteValue MaterializeValue(
        FlatteningInput flatteningInput,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        SelectedBodyIndex selectedBodyIndex,
        FlatteningResolvedReferenceLookupSet resolvedReferenceLookups,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts
    )
    {
        return columnBinding.Source switch
        {
            WriteValueSource.DocumentId => ResolveRootDocumentIdValue(flatteningInput.TargetContext),
            WriteValueSource.ParentKeyPart parentKeyPart => ResolveParentKeyPart(
                tableWritePlan,
                columnBinding,
                parentKeyPart,
                parentKeyParts
            ),
            WriteValueSource.Scalar scalar => ResolveScalarValue(
                tableWritePlan,
                columnBinding,
                scalar,
                selectedBodyIndex
            ),
            WriteValueSource.DocumentReference documentReference => new FlattenedWriteValue.Literal(
                resolvedReferenceLookups.GetDocumentId(documentReference.BindingIndex, [])
            ),
            WriteValueSource.DescriptorReference descriptorReference => new FlattenedWriteValue.Literal(
                resolvedReferenceLookups.GetDescriptorId(tableWritePlan, descriptorReference, [])
            ),
            WriteValueSource.Ordinal => throw CreateUnsupportedValueSourceException(
                tableWritePlan,
                columnBinding,
                "collection ordinals"
            ),
            WriteValueSource.Precomputed => throw CreateUnsupportedValueSourceException(
                tableWritePlan,
                columnBinding,
                "precomputed write values"
            ),
            _ => throw new InvalidOperationException(
                $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' uses unsupported write source '{columnBinding.Source.GetType().Name}'."
            ),
        };
    }

    private static FlattenedWriteValue ResolveParentKeyPart(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.ParentKeyPart parentKeyPart,
        IReadOnlyList<FlattenedWriteValue> parentKeyParts
    )
    {
        if (parentKeyPart.Index >= 0 && parentKeyPart.Index < parentKeyParts.Count)
        {
            return parentKeyParts[parentKeyPart.Index];
        }

        throw new InvalidOperationException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' requested parent key part index {parentKeyPart.Index}, but only {parentKeyParts.Count} parent key part values were available."
        );
    }

    private static FlattenedWriteValue ResolveScalarValue(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        WriteValueSource.Scalar scalar,
        SelectedBodyIndex selectedBodyIndex
    )
    {
        var absolutePath = RestrictedJsonPath.CombineCanonical(
            tableWritePlan.TableModel.JsonScope,
            scalar.RelativePath
        );

        if (!selectedBodyIndex.TryGetLeafNode(absolutePath, out var scalarNode))
        {
            return new FlattenedWriteValue.Literal(null);
        }

        if (scalarNode is null)
        {
            return new FlattenedWriteValue.Literal(null);
        }

        if (scalarNode is not JsonValue jsonValue)
        {
            throw CreateInvalidScalarReadException(
                tableWritePlan,
                columnBinding,
                absolutePath,
                scalar.Type,
                $"encountered non-scalar JSON node type '{scalarNode.GetType().Name}'"
            );
        }

        return new FlattenedWriteValue.Literal(
            ConvertScalarValue(jsonValue, scalar.Type, tableWritePlan, columnBinding, absolutePath)
        );
    }

    private static object ConvertScalarValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath
    )
    {
        return scalarType.Kind switch
        {
            ScalarKind.String => ReadRequiredJsonValue<string>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.Int32 => ReadRequiredJsonValue<int>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.Int64 => ReadRequiredJsonValue<long>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.Decimal => ReadRequiredJsonValue<decimal>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.Boolean => ReadRequiredJsonValue<bool>(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.Date => ReadDateOnlyValue(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.DateTime => ReadDateTimeValue(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            ScalarKind.Time => ReadTimeOnlyValue(
                jsonValue,
                scalarType,
                tableWritePlan,
                columnBinding,
                absolutePath
            ),
            _ => throw new InvalidOperationException(
                $"Scalar kind '{scalarType.Kind}' is not supported by the relational write flattener."
            ),
        };
    }

    private static T ReadRequiredJsonValue<T>(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath
    )
        where T : notnull
    {
        if (jsonValue.TryGetValue<T>(out var value))
        {
            return value;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnBinding,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static DateOnly ReadDateOnlyValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath
    )
    {
        if (
            jsonValue.TryGetValue<string>(out var rawValue)
            && DateOnly.TryParseExact(
                rawValue,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateOnlyValue
            )
        )
        {
            return dateOnlyValue;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnBinding,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static DateTime ReadDateTimeValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath
    )
    {
        if (
            jsonValue.TryGetValue<string>(out var rawValue)
            && DateTime.TryParse(
                rawValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dateTimeValue
            )
        )
        {
            return dateTimeValue;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnBinding,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static TimeOnly ReadTimeOnlyValue(
        JsonValue jsonValue,
        RelationalScalarType scalarType,
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath
    )
    {
        if (
            jsonValue.TryGetValue<string>(out var rawValue)
            && TimeOnly.TryParse(
                rawValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var timeOnlyValue
            )
        )
        {
            return timeOnlyValue;
        }

        throw CreateInvalidScalarReadException(
            tableWritePlan,
            columnBinding,
            absolutePath,
            scalarType,
            $"encountered JSON value kind '{jsonValue.GetValueKind()}' with raw value {jsonValue.ToJsonString()}"
        );
    }

    private static FlattenedWriteValue ResolveRootDocumentIdValue(RelationalWriteTargetContext targetContext)
    {
        return targetContext switch
        {
            RelationalWriteTargetContext.CreateNew => FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
            RelationalWriteTargetContext.ExistingDocument existingDocument => new FlattenedWriteValue.Literal(
                existingDocument.DocumentId
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational write target context '{targetContext.GetType().Name}'."
            ),
        };
    }

    private static NotSupportedException CreateUnsupportedValueSourceException(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string featureDescription
    )
    {
        return new NotSupportedException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' depends on {featureDescription}, which are not implemented in the initial relational write flattener."
        );
    }

    private static InvalidOperationException CreateInvalidScalarReadException(
        TableWritePlan tableWritePlan,
        WriteColumnBinding columnBinding,
        string absolutePath,
        RelationalScalarType scalarType,
        string reason
    )
    {
        return new InvalidOperationException(
            $"Column '{columnBinding.Column.ColumnName.Value}' on table '{FormatTable(tableWritePlan)}' expected scalar kind '{scalarType.Kind}' at path '{absolutePath}', but {reason}."
        );
    }

    private static string FormatTable(TableWritePlan tableWritePlan)
    {
        ArgumentNullException.ThrowIfNull(tableWritePlan);
        return $"{tableWritePlan.TableModel.Table.Schema.Value}.{tableWritePlan.TableModel.Table.Name}";
    }

    private sealed class SelectedBodyIndex
    {
        private readonly Dictionary<string, JsonNode?> _leafNodesByPath;
        private readonly HashSet<string> _scopesWithBoundData;

        private SelectedBodyIndex(
            Dictionary<string, JsonNode?> leafNodesByPath,
            HashSet<string> scopesWithBoundData
        )
        {
            _leafNodesByPath = leafNodesByPath ?? throw new ArgumentNullException(nameof(leafNodesByPath));
            _scopesWithBoundData =
                scopesWithBoundData ?? throw new ArgumentNullException(nameof(scopesWithBoundData));
        }

        public static SelectedBodyIndex Create(JsonNode selectedBody)
        {
            ArgumentNullException.ThrowIfNull(selectedBody);

            if (selectedBody is not JsonObject rootObject)
            {
                throw new InvalidOperationException(
                    $"Selected write body must be a JSON object, but found '{selectedBody.GetType().Name}'."
                );
            }

            Dictionary<string, JsonNode?> leafNodesByPath = new(StringComparer.Ordinal);
            HashSet<string> scopesWithBoundData = ["$"];

            VisitObject(rootObject, "$", leafNodesByPath, scopesWithBoundData);

            return new SelectedBodyIndex(leafNodesByPath, scopesWithBoundData);
        }

        public bool TryGetLeafNode(string absolutePath, out JsonNode? value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
            return _leafNodesByPath.TryGetValue(absolutePath, out value);
        }

        public bool HasBoundDataForScope(string scopePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);
            return _scopesWithBoundData.Contains(scopePath);
        }

        private static void VisitObject(
            JsonObject jsonObject,
            string currentPath,
            Dictionary<string, JsonNode?> leafNodesByPath,
            HashSet<string> scopesWithBoundData
        )
        {
            foreach (var property in jsonObject)
            {
                var propertyPath = $"{currentPath}.{property.Key}";

                switch (property.Value)
                {
                    case JsonObject childObject:
                        VisitObject(childObject, propertyPath, leafNodesByPath, scopesWithBoundData);
                        break;
                    case JsonArray:
                        break;
                    default:
                        leafNodesByPath[propertyPath] = property.Value;
                        MarkAncestorScopes(propertyPath, scopesWithBoundData);
                        break;
                }
            }
        }

        private static void MarkAncestorScopes(string leafPath, HashSet<string> scopesWithBoundData)
        {
            var currentPath = leafPath;

            while (true)
            {
                var lastDotIndex = currentPath.LastIndexOf('.');

                if (lastDotIndex <= 0)
                {
                    scopesWithBoundData.Add("$");
                    return;
                }

                currentPath = currentPath[..lastDotIndex];
                scopesWithBoundData.Add(currentPath);
            }
        }
    }

    private static class RestrictedJsonPath
    {
        public static string CombineCanonical(JsonPathExpression scopePath, JsonPathExpression relativePath)
        {
            JsonPathSegment[] combinedSegments = [.. GetSegments(scopePath), .. GetSegments(relativePath)];

            return BuildCanonical(combinedSegments);
        }

        private static IReadOnlyList<JsonPathSegment> GetSegments(JsonPathExpression path)
        {
            if (path.Canonical == "$")
            {
                return [];
            }

            if (path.Segments.Count > 0)
            {
                return path.Segments;
            }

            return Parse(path.Canonical);
        }

        private static JsonPathSegment[] Parse(string canonicalPath)
        {
            if (string.IsNullOrWhiteSpace(canonicalPath) || canonicalPath[0] != '$')
            {
                throw new InvalidOperationException(
                    $"Restricted JSONPath '{canonicalPath}' is not canonical."
                );
            }

            List<JsonPathSegment> segments = [];
            var index = 1;

            while (index < canonicalPath.Length)
            {
                switch (canonicalPath[index])
                {
                    case '.':
                        index = AppendProperty(canonicalPath, index, segments);
                        break;
                    case '[' when IsArrayWildcard(canonicalPath, index):
                        segments.Add(new JsonPathSegment.AnyArrayElement());
                        index += 3;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Restricted JSONPath '{canonicalPath}' is not canonical."
                        );
                }
            }

            return [.. segments];
        }

        private static int AppendProperty(string canonicalPath, int dotIndex, List<JsonPathSegment> segments)
        {
            var startIndex = dotIndex + 1;
            var index = startIndex;

            while (index < canonicalPath.Length && canonicalPath[index] is not ('.' or '['))
            {
                index++;
            }

            if (index == startIndex)
            {
                throw new InvalidOperationException(
                    $"Restricted JSONPath '{canonicalPath}' is not canonical."
                );
            }

            segments.Add(new JsonPathSegment.Property(canonicalPath[startIndex..index]));

            return index;
        }

        private static bool IsArrayWildcard(string canonicalPath, int openBracketIndex)
        {
            return openBracketIndex + 2 < canonicalPath.Length
                && canonicalPath[openBracketIndex + 1] == '*'
                && canonicalPath[openBracketIndex + 2] == ']';
        }

        private static string BuildCanonical(IReadOnlyList<JsonPathSegment> segments)
        {
            ArgumentNullException.ThrowIfNull(segments);

            return string.Create(
                CalculateCanonicalLength(segments),
                segments,
                static (buffer, state) =>
                {
                    buffer[0] = '$';
                    var index = 1;

                    foreach (var segment in state)
                    {
                        switch (segment)
                        {
                            case JsonPathSegment.Property property:
                                buffer[index++] = '.';
                                property.Name.AsSpan().CopyTo(buffer[index..]);
                                index += property.Name.Length;
                                break;
                            case JsonPathSegment.AnyArrayElement:
                                "[*]".AsSpan().CopyTo(buffer[index..]);
                                index += 3;
                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Restricted JSONPath segment '{segment.GetType().Name}' is not supported."
                                );
                        }
                    }
                }
            );
        }

        private static int CalculateCanonicalLength(IReadOnlyList<JsonPathSegment> segments)
        {
            var length = 1;

            for (var index = 0; index < segments.Count; index++)
            {
                length += segments[index] switch
                {
                    JsonPathSegment.Property property => property.Name.Length + 1,
                    JsonPathSegment.AnyArrayElement => 3,
                    _ => throw new InvalidOperationException(
                        $"Restricted JSONPath segment '{segments[index].GetType().Name}' is not supported."
                    ),
                };
            }

            return length;
        }
    }
}
