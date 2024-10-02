// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Provides information from a single DocumentPath from the documentPathsMapping portion
/// of a resourceSchema in an ApiSchema.json document.
///
/// For example, a partial documentPathsMapping for a document with a reference to a School document looks like:
///
/// "documentPathsMapping": {
///   "School": {
///     "isDescriptor": false,
///     "isReference": true,
///     "projectName": "Ed-Fi",
///     "referenceJsonPaths": [
///       {
///         "identityJsonPath": "$.schoolId",
///         "referenceJsonPath": "$.schoolReference.schoolId"
///       }
///     ],
///     "resourceName": "School"
///   }
/// }
///
/// Here, a DocumentPath object would represent the document object value whose key is "School"
///
/// </summary>
internal class DocumentPath(JsonNode _documentPathsNode)
{
    private readonly Lazy<bool> _isDescriptor =
        new(() =>
        {
            return _documentPathsNode["isDescriptor"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isDescriptor to be on DocumentPath, not a reference, or invalid ApiSchema"
                );
        });

    /// <summary>
    /// Whether the path reference is a descriptor reference, taken from isDescriptor
    /// </summary>
    public bool IsDescriptor => _isDescriptor.Value;

    private readonly Lazy<bool> _isReference =
        new(() =>
        {
            return _documentPathsNode["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on DocumentPath, invalid ApiSchema"
                );
        });

    /// <summary>
    /// Whether the path is a reference, taken from isReference
    /// </summary>
    public bool IsReference => _isReference.Value;

    private readonly Lazy<JsonPath> _path =
        new(() =>
        {
            string jsonPathString =
                (_documentPathsNode["path"]?.GetValue<string>())
                ?? throw new InvalidOperationException(
                    "Expected path to be on DocumentPath, this is a non-descriptor reference, or invalid ApiSchema"
                );
            return new(jsonPathString);
        });

    /// <summary>
    /// The JsonPath of a non-reference or a descriptor reference, taken from path
    /// </summary>
    public JsonPath Path => _path.Value;

    private readonly Lazy<ProjectName> _projectName =
        new(() =>
        {
            string projectNameString =
                (_documentPathsNode["projectName"]?.GetValue<string>())
                ?? throw new InvalidOperationException(
                    "Expected projectName to be on DocumentPath, not a reference, or invalid ApiSchema"
                );
            return new(projectNameString);
        });

    /// <summary>
    /// The project name of a reference, taken from projectName
    /// </summary>
    public ProjectName ProjectName => _projectName.Value;

    private readonly Lazy<ResourceName> _resourceName =
        new(() =>
        {
            string resourceNameString =
                (_documentPathsNode["resourceName"]?.GetValue<string>())
                ?? throw new InvalidOperationException(
                    "Expected resourceName to be on DocumentPath, not a reference, or invalid ApiSchema"
                );
            return new(resourceNameString);
        });

    /// <summary>
    /// The resource name of a reference, taken from projectName
    /// </summary>
    public ResourceName ResourceName => _resourceName.Value;

    private readonly Lazy<IEnumerable<ReferenceJsonPathsElement>> _referenceJsonPathsElements =
        new(() =>
        {
            JsonArray referenceJsonPathsArray =
                _documentPathsNode["referenceJsonPaths"]?.AsArray()
                ?? throw new InvalidOperationException(
                    "Expected referenceJsonPaths to be on ResourceSchema, not a reference, or invalid ApiSchema"
                );

            List<ReferenceJsonPathsElement> result = new();

            foreach (var referenceJsonPathsElement in referenceJsonPathsArray)
            {
                if (referenceJsonPathsElement == null)
                {
                    throw new InvalidOperationException(
                        "Expected referenceJsonPaths to not have null elements, invalid ApiSchema"
                    );
                }

                result.Add(
                    new ReferenceJsonPathsElement(
                        new(
                            referenceJsonPathsElement["identityJsonPath"]?.GetValue<string>()
                                ?? throw new InvalidOperationException(
                                    "Expected identityJsonPath to be on referenceJsonPaths element, invalid ApiSchema"
                                )
                        ),
                        new(
                            referenceJsonPathsElement["referenceJsonPath"]?.GetValue<string>()
                                ?? throw new InvalidOperationException(
                                    "Expected referenceJsonPath to be on referenceJsonPaths element, invalid ApiSchema"
                                )
                        )
                    )
                );
            }

            return result;
        });

    /// <summary>
    /// An ordered list of the ReferenceJsonPathsElements that describe the identity of this reference, taken
    /// from the referenceJsonPaths array
    /// </summary>
    public IEnumerable<ReferenceJsonPathsElement> ReferenceJsonPathsElements =>
        _referenceJsonPathsElements.Value;
}
