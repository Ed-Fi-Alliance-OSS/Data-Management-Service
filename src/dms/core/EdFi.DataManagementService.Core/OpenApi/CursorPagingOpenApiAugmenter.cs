// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Frozen;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ChangeQueries;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Utilities;

namespace EdFi.DataManagementService.Core.OpenApi;

/// <summary>
/// Publishes the cursor-paging contract onto an assembled OpenAPI document: the pageToken and pageSize
/// parameter references plus the Next-Page-Token response header on every eligible collection GET, and a
/// sibling partitions operation for each of those collections. Runs after all core, abstract, and
/// extension fragments are merged and before domain and profile filtering.
/// </summary>
/// <remarks>
/// Every published name, response member, and reserved-parameter set is spelled from the constants the
/// request pipeline reads, so published metadata cannot drift from runtime enforcement. Only collection
/// paths proven to come from an endpoint-owning resource schema for the document type being assembled are
/// augmented, and any malformed part of such an operation's contract fails assembly rather than
/// publishing a partial or dangling contract.
/// </remarks>
internal static class CursorPagingOpenApiAugmenter
{
    private const string PathsPath = "$.paths";
    private const string ComponentsPath = "$.components";
    private const string SchemasPath = "$.components.schemas";
    private const string ParametersPath = "$.components.parameters";
    private const string ParameterComponentRefPrefix = "#/components/parameters/";
    private const string SchemaComponentRefPrefix = "#/components/schemas/";

    private const string LimitComponent = "limit";
    private const string PageTokenComponent = "pageToken";
    private const string PageSizeComponent = "pageSize";
    private const string NumberOfPartitionsComponent = "numberOfPartitions";

    private const string PartitionTokensSchemaName = "partitionTokens";
    private const string PartitionsPathSuffix = "/partitions";
    private const string PartitionOperationIdSuffix = "Partitions";
    private const string ApplicationJsonContentType = "application/json";
    private const string SuccessResponseKey = "200";
    private const string QueryParameterLocation = "query";
    private const string DomainsExtensionKey = "x-Ed-Fi-domains";

    private const string PartitionOperationSummary =
        "Retrieves the page tokens that partition this resource for parallel cursor paging.";

    private const string PartitionOperationDescription =
        "This GET operation returns a set of opaque page tokens that divide the accessible items of this "
        + "resource into ranges that can be retrieved in parallel using the pageToken parameter of the "
        + "collection GET operation. Boundaries are calculated after the same filters and authorization the "
        + "collection GET applies, so the same filters must be repeated on every request. The response may "
        + "contain fewer tokens than requested and never contains more.";

    private const string PartitionSuccessResponseDescription =
        "The requested page tokens were successfully retrieved.";

    private const string PartitionTokensSchemaDescription =
        "A set of opaque page tokens that partition a resource's accessible items for parallel cursor paging.";

    /// <summary>
    /// Replaces whatever the base document says about an omitted partition count. The published default is
    /// the deployment's configured value, so any description promising a count derived from the number of
    /// accessible items would contradict both the published default and what the request pipeline applies.
    /// </summary>
    private const string NumberOfPartitionsDescription =
        "The number of evenly distributed partitions to provide for client-side parallel processing. If "
        + "unspecified, the configured default number of partitions for this deployment is used.";

    private const string NextPageTokenHeaderDescription =
        "An opaque token that retrieves the next page of results when supplied as the pageToken parameter "
        + "of this operation. Present only when a further page may exist.";

    /// <summary>
    /// The parameter components every assembled resource and descriptor document must declare, because the
    /// published contract references all of them and a dangling reference invalidates the whole document.
    /// </summary>
    private static readonly string[] _requiredParameterComponents =
    [
        LimitComponent,
        PageTokenComponent,
        PageSizeComponent,
        NumberOfPartitionsComponent,
    ];

    /// <summary>
    /// The query name each required component must publish, spelled from the request-pipeline validators
    /// rather than from a second copy. A published reference to a component naming anything else would
    /// advertise a query parameter the pipeline does not honor, which is worse than publishing nothing.
    /// Note that the component key and the query name differ for the partition count: the component is
    /// <c>numberOfPartitions</c> and the parameter it publishes is <c>number</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> _requiredParameterComponentNames =
        BuildRequiredParameterComponentNames();

    /// <summary>
    /// The parameter components whose published default and maximum are the runtime maximum page size.
    /// </summary>
    private static readonly string[] _pageSizeBoundedComponents = [LimitComponent, PageSizeComponent];

    /// <summary>
    /// The only referenced filters the partitions operation carries over from its collection GET. An
    /// allowlist rather than a denylist because the contract enumerates exactly these two.
    /// </summary>
    private static readonly FrozenSet<string> _changeVersionParameterNames =
        BuildChangeVersionParameterNames();

    /// <summary>
    /// The query names the partitions operation refuses to filter on, copied out of the request-pipeline
    /// validator into an immutable set. Copying a resource filter named for the partition count would also
    /// produce two query parameters of the same name on one operation.
    /// </summary>
    private static readonly FrozenSet<string> _partitionExcludedParameterNames =
        BuildPartitionExcludedParameterNames();

    /// <summary>
    /// Adds the collection paths a resource fragment owns to the eligible set. Called only for
    /// endpoint-owning resource schemas of the document type being assembled, so membership is proof that
    /// a path belongs to a regular resource or descriptor rather than to a discovery, management, or
    /// base-document path.
    /// </summary>
    internal static void CollectEligibleCollectionPaths(
        JsonObject fragmentPaths,
        HashSet<string> eligibleCollectionPaths
    )
    {
        foreach (
            string pathKey in fragmentPaths.Select(fragmentPath => fragmentPath.Key).Where(IsCollectionPath)
        )
        {
            eligibleCollectionPaths.Add(pathKey);
        }
    }

    /// <summary>
    /// A collection path is a two-segment, non-templated path. The shape excludes the item path declared
    /// beside it in the same fragment, and excludes derived siblings such as a change-query or partitions
    /// path, which carry a third segment.
    /// </summary>
    private static bool IsCollectionPath(string pathKey)
    {
        if (pathKey.Contains('{') || pathKey.Contains('}'))
        {
            return false;
        }

        return pathKey.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
    }

    /// <summary>
    /// Publishes the cursor-paging contract onto the merged document.
    /// </summary>
    internal static void Augment(
        JsonNode openApiSpecification,
        IReadOnlySet<string> eligibleCollectionPaths,
        OpenApiPagingSettings pagingSettings
    )
    {
        JsonObject paths = RequireObject(openApiSpecification["paths"], PathsPath);
        JsonObject components = RequireObject(openApiSpecification["components"], ComponentsPath);
        JsonObject componentSchemas = RequireObject(components["schemas"], SchemasPath);
        JsonObject componentParameters = RequireObject(components["parameters"], ParametersPath);

        ValidateRequiredParameterComponents(componentParameters);
        PublishRuntimePagingValues(componentParameters, pagingSettings);

        string pageTokenName = ComponentEffectiveName(componentParameters, PageTokenComponent);
        string pageSizeName = ComponentEffectiveName(componentParameters, PageSizeComponent);

        List<string> collectionPathKeys = paths
            .Where(path => eligibleCollectionPaths.Contains(path.Key))
            .Select(path => path.Key)
            .ToList();

        foreach (string pathKey in collectionPathKeys)
        {
            AugmentCollection(
                paths,
                componentParameters,
                componentSchemas,
                pathKey,
                pageTokenName,
                pageSizeName
            );
        }
    }

    private static void AugmentCollection(
        JsonObject paths,
        JsonObject componentParameters,
        JsonObject componentSchemas,
        string pathKey,
        string pageTokenName,
        string pageSizeName
    )
    {
        string partitionPathKey = pathKey + PartitionsPathSuffix;

        if (paths.ContainsKey(partitionPathKey))
        {
            throw new InvalidOperationException(
                $"Path '{Sanitize(partitionPathKey)}' is already present in the OpenAPI specification. "
                    + "Cursor-paging assembly will not replace an existing partitions operation."
            );
        }

        JsonObject pathItem = RequireObject(paths[pathKey], PathDiagnostic(pathKey));
        JsonObject getOperation = RequireObject(pathItem["get"], $"{PathDiagnostic(pathKey)}.get");
        string operationId = RequireOperationId(getOperation, pathKey);
        JsonArray collectionParameters = RequireParameters(getOperation, pathKey);
        JsonObject successResponse = RequireSuccessResponse(getOperation, pathKey);

        JsonObject partitionOperation = BuildPartitionOperation(
            getOperation,
            collectionParameters,
            componentParameters,
            operationId,
            pathKey
        );

        AppendParameterReference(
            collectionParameters,
            componentParameters,
            PageTokenComponent,
            pageTokenName,
            pathKey
        );
        AppendParameterReference(
            collectionParameters,
            componentParameters,
            PageSizeComponent,
            pageSizeName,
            pathKey
        );
        AddNextPageTokenHeader(successResponse, pathKey);
        EnsurePartitionTokensSchema(componentSchemas);

        JsonObject partitionPathItem = new() { ["get"] = partitionOperation };

        if (pathItem[DomainsExtensionKey] is JsonNode domains)
        {
            partitionPathItem[DomainsExtensionKey] = domains.DeepClone();
        }

        paths[partitionPathKey] = partitionPathItem;
    }

    private static JsonObject BuildPartitionOperation(
        JsonObject getOperation,
        JsonArray collectionParameters,
        JsonObject componentParameters,
        string operationId,
        string pathKey
    )
    {
        JsonArray partitionParameters = [ParameterReference(NumberOfPartitionsComponent)];

        for (int index = 0; index < collectionParameters.Count; index += 1)
        {
            ParameterFacts facts = ResolveParameterFacts(
                collectionParameters[index],
                componentParameters,
                pathKey,
                index
            );

            if (ShouldCopyToPartitionOperation(facts))
            {
                partitionParameters.Add(collectionParameters[index]!.DeepClone());
            }
        }

        JsonObject partitionOperation = new()
        {
            ["description"] = PartitionOperationDescription,
            ["operationId"] = operationId + PartitionOperationIdSuffix,
            ["parameters"] = partitionParameters,
            ["responses"] = new JsonObject
            {
                [SuccessResponseKey] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        [ApplicationJsonContentType] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["$ref"] = SchemaComponentRefPrefix + PartitionTokensSchemaName,
                            },
                        },
                    },
                    ["description"] = PartitionSuccessResponseDescription,
                },
            },
        };

        if (getOperation["security"] is JsonNode security)
        {
            partitionOperation["security"] = security.DeepClone();
        }

        partitionOperation["summary"] = PartitionOperationSummary;

        if (getOperation["tags"] is JsonNode tags)
        {
            partitionOperation["tags"] = tags.DeepClone();
        }

        return partitionOperation;
    }

    /// <summary>
    /// A referenced filter is carried over only when it is one of the two live change-version filters. An
    /// inline filter is carried over when it is a query parameter the partitions operation will actually
    /// filter on.
    /// </summary>
    private static bool ShouldCopyToPartitionOperation(ParameterFacts facts)
    {
        if (facts.IsReference)
        {
            return _changeVersionParameterNames.Contains(facts.EffectiveName);
        }

        return string.Equals(facts.Location, QueryParameterLocation, StringComparison.Ordinal)
            && !_partitionExcludedParameterNames.Contains(facts.EffectiveName);
    }

    private static void AppendParameterReference(
        JsonArray collectionParameters,
        JsonObject componentParameters,
        string componentName,
        string effectiveName,
        string pathKey
    )
    {
        JsonObject expected = ParameterReference(componentName);

        for (int index = 0; index < collectionParameters.Count; index += 1)
        {
            ParameterFacts facts = ResolveParameterFacts(
                collectionParameters[index],
                componentParameters,
                pathKey,
                index
            );

            if (!string.Equals(facts.EffectiveName, effectiveName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (JsonNode.DeepEquals(collectionParameters[index], expected))
            {
                return;
            }

            throw new InvalidOperationException(
                $"The GET operation at path '{Sanitize(pathKey)}' already declares a "
                    + $"'{Sanitize(effectiveName)}' parameter that differs from the cursor-paging parameter "
                    + "reference. Cursor-paging assembly will not replace a conflicting parameter."
            );
        }

        collectionParameters.Add(expected);
    }

    private static void AddNextPageTokenHeader(JsonObject successResponse, string pathKey)
    {
        JsonObject expected = new()
        {
            ["description"] = NextPageTokenHeaderDescription,
            ["schema"] = new JsonObject { ["type"] = "string" },
        };

        if (successResponse["headers"] is null)
        {
            successResponse["headers"] = new JsonObject
            {
                [QueryRequestHandler.NextPageTokenHeaderName] = expected,
            };
            return;
        }

        JsonObject headers = RequireObject(
            successResponse["headers"],
            $"{PathDiagnostic(pathKey)}.get.responses.{SuccessResponseKey}.headers"
        );

        if (headers[QueryRequestHandler.NextPageTokenHeaderName] is null)
        {
            headers[QueryRequestHandler.NextPageTokenHeaderName] = expected;
            return;
        }

        if (JsonNode.DeepEquals(headers[QueryRequestHandler.NextPageTokenHeaderName], expected))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The GET operation at path '{Sanitize(pathKey)}' already declares a "
                + $"'{QueryRequestHandler.NextPageTokenHeaderName}' response header that differs from the "
                + "cursor-paging header. Cursor-paging assembly will not replace a conflicting header."
        );
    }

    /// <summary>
    /// Adds the shared partition response schema on first use, so a document with no partitions operation
    /// gains no unreferenced schema.
    /// </summary>
    private static void EnsurePartitionTokensSchema(JsonObject componentSchemas)
    {
        JsonObject expected = new()
        {
            ["description"] = PartitionTokensSchemaDescription,
            ["properties"] = new JsonObject
            {
                [PartitionRequestHandler.PageTokensMember] = new JsonObject
                {
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["type"] = "array",
                },
            },
            // The handler emits this member on every 200, including when no partition is accessible and
            // the array is empty, so the published contract states it is always present. The array itself
            // stays unconstrained, because an empty set of tokens is a real response.
            ["required"] = new JsonArray { PartitionRequestHandler.PageTokensMember },
            ["type"] = "object",
        };

        if (componentSchemas[PartitionTokensSchemaName] is null)
        {
            componentSchemas[PartitionTokensSchemaName] = expected;
            return;
        }

        if (JsonNode.DeepEquals(componentSchemas[PartitionTokensSchemaName], expected))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Schema '{PartitionTokensSchemaName}' is already present in the OpenAPI specification with "
                + "different content. Cursor-paging assembly will not replace a conflicting schema."
        );
    }

    private static void ValidateRequiredParameterComponents(JsonObject componentParameters)
    {
        foreach (string componentName in _requiredParameterComponents)
        {
            string componentPath = $"{ParametersPath}.{componentName}";
            JsonObject component = RequireObject(componentParameters[componentName], componentPath);
            RequireObject(component["schema"], $"{componentPath}.schema");

            string expectedName = _requiredParameterComponentNames[componentName];
            string publishedName = RequireName(component["name"], componentPath);

            if (!string.Equals(publishedName, expectedName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Parameter component at '{componentPath}' publishes the query name "
                        + $"'{Sanitize(publishedName)}', but the request pipeline reads '{expectedName}'. "
                        + "Cursor-paging assembly will not publish a parameter the pipeline does not honor."
                );
            }

            string? location = StringValue(component["in"]);

            if (!string.Equals(location, QueryParameterLocation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Parameter component at '{componentPath}' is carried in "
                        + $"'{Sanitize(location ?? "(none)")}', but the request pipeline reads it from the "
                        + $"'{QueryParameterLocation}' location."
                );
            }
        }
    }

    private static void PublishRuntimePagingValues(
        JsonObject componentParameters,
        OpenApiPagingSettings pagingSettings
    )
    {
        foreach (string componentName in _pageSizeBoundedComponents)
        {
            JsonObject schema = ComponentSchema(componentParameters, componentName);
            schema["default"] = pagingSettings.MaximumPageSize;
            schema["maximum"] = pagingSettings.MaximumPageSize;
        }

        ComponentSchema(componentParameters, NumberOfPartitionsComponent)["default"] =
            pagingSettings.DefaultPartitionCount;

        RequireObject(
            componentParameters[NumberOfPartitionsComponent],
            $"{ParametersPath}.{NumberOfPartitionsComponent}"
        )["description"] = NumberOfPartitionsDescription;
    }

    private static ParameterFacts ResolveParameterFacts(
        JsonNode? parameterEntry,
        JsonObject componentParameters,
        string pathKey,
        int index
    )
    {
        string parameterDiagnostic = $"{PathDiagnostic(pathKey)}.get.parameters[{index}]";
        JsonObject parameterObject = RequireObject(parameterEntry, parameterDiagnostic);
        string? reference = StringValue(parameterObject["$ref"]);

        if (reference is null)
        {
            return new ParameterFacts(
                IsReference: false,
                EffectiveName: RequireName(parameterObject["name"], parameterDiagnostic),
                Location: StringValue(parameterObject["in"])
            );
        }

        if (!reference.StartsWith(ParameterComponentRefPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Parameter at '{parameterDiagnostic}' references '{Sanitize(reference)}', which is not a "
                    + "parameter component reference."
            );
        }

        string componentName = reference[ParameterComponentRefPrefix.Length..];
        string componentDiagnostic = $"{ParametersPath}.{Sanitize(componentName)}";
        JsonObject component = RequireObject(componentParameters[componentName], componentDiagnostic);

        return new ParameterFacts(
            IsReference: true,
            EffectiveName: RequireName(component["name"], componentDiagnostic),
            Location: StringValue(component["in"])
        );
    }

    private static JsonObject ComponentSchema(JsonObject componentParameters, string componentName) =>
        RequireObject(
            componentParameters[componentName]?["schema"],
            $"{ParametersPath}.{componentName}.schema"
        );

    private static string ComponentEffectiveName(JsonObject componentParameters, string componentName) =>
        RequireName(componentParameters[componentName]?["name"], $"{ParametersPath}.{componentName}");

    private static string RequireOperationId(JsonObject getOperation, string pathKey)
    {
        string? operationId = StringValue(getOperation["operationId"]);

        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException(
                $"The GET operation at path '{Sanitize(pathKey)}' has no operationId. A partitions operation "
                    + "identifier is derived from it, so the collection contract is incomplete."
            );
        }

        return operationId;
    }

    private static JsonArray RequireParameters(JsonObject getOperation, string pathKey)
    {
        if (getOperation["parameters"] is null)
        {
            JsonArray created = [];
            getOperation["parameters"] = created;
            return created;
        }

        if (getOperation["parameters"] is not JsonArray parameters)
        {
            throw new InvalidOperationException(
                $"Node at path '{PathDiagnostic(pathKey)}.get.parameters' is not a JSON array"
            );
        }

        return parameters;
    }

    private static JsonObject RequireSuccessResponse(JsonObject getOperation, string pathKey)
    {
        JsonObject responses = RequireObject(
            getOperation["responses"],
            $"{PathDiagnostic(pathKey)}.get.responses"
        );

        return RequireObject(
            responses[SuccessResponseKey],
            $"{PathDiagnostic(pathKey)}.get.responses.{SuccessResponseKey}"
        );
    }

    private static JsonObject ParameterReference(string componentName) =>
        new() { ["$ref"] = ParameterComponentRefPrefix + componentName };

    private static JsonObject RequireObject(JsonNode? node, string diagnosticPath)
    {
        if (node is null)
        {
            throw new InvalidOperationException($"Node at path '{diagnosticPath}' not found");
        }

        if (node is not JsonObject nodeObject)
        {
            throw new InvalidOperationException($"Node at path '{diagnosticPath}' is not a JSON object");
        }

        return nodeObject;
    }

    private static string RequireName(JsonNode? node, string diagnosticPath)
    {
        string? name = StringValue(node);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException(
                $"Parameter at '{diagnosticPath}' has no name, so its published query name cannot be "
                    + "determined."
            );
        }

        return name;
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// A diagnostic path whose only untrusted fragment, the path key, is sanitized. The surrounding
    /// JSONPath scaffolding is this file's own text and must survive intact to stay readable.
    /// </summary>
    private static string PathDiagnostic(string pathKey) => $"{PathsPath}['{Sanitize(pathKey)}']";

    private static string Sanitize(string value) => LoggingSanitizer.SanitizeForLogging(value);

    private static FrozenSet<string> BuildChangeVersionParameterNames()
    {
        string[] changeVersionNames =
        [
            ChangeVersionParameterValidator.MinChangeVersion,
            ChangeVersionParameterValidator.MaxChangeVersion,
        ];

        return changeVersionNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, string> BuildRequiredParameterComponentNames()
    {
        Dictionary<string, string> componentNames = new(StringComparer.Ordinal)
        {
            [LimitComponent] = CursorRequestValidator.LimitParameter,
            [PageTokenComponent] = CursorRequestValidator.PageTokenParameter,
            [PageSizeComponent] = CursorRequestValidator.PageSizeParameter,
            [NumberOfPartitionsComponent] = PartitionRequestValidator.NumberParameter,
        };

        return componentNames.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenSet<string> BuildPartitionExcludedParameterNames()
    {
        string[] excludedNames =
        [
            .. PartitionRequestValidator.ReservedParameters,
            PartitionRequestValidator.NumberParameter,
        ];

        return excludedNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What assembly needs to know about one parameter entry: whether it arrived as a component reference,
    /// the query name it publishes, and where it is carried.
    /// </summary>
    private readonly record struct ParameterFacts(bool IsReference, string EffectiveName, string? Location);
}
