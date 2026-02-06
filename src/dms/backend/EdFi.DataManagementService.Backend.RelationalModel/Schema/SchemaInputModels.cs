// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Schema;

/// <summary>
/// Represents a reference identity binding between a target resource identity path and the referencing JSONPath.
/// </summary>
/// <param name="IdentityJsonPath">The identity JSONPath on the target resource.</param>
/// <param name="ReferenceJsonPath">The JSONPath to the identity value under the reference object.</param>
public sealed record ReferenceJsonPathBinding(
    JsonPathExpression IdentityJsonPath,
    JsonPathExpression ReferenceJsonPath
);

/// <summary>
/// Represents a document-reference mapping entry derived from <c>documentPathsMapping</c>.
/// </summary>
/// <param name="MappingKey">The mapping key for the reference entry.</param>
/// <param name="TargetResource">The referenced resource type.</param>
/// <param name="IsRequired">Whether the reference is required.</param>
/// <param name="IsPartOfIdentity">Whether the reference contributes to the parent identity.</param>
/// <param name="ReferenceObjectPath">The JSONPath to the reference object.</param>
/// <param name="ReferenceJsonPaths">The identity path bindings for the reference.</param>
public sealed record DocumentReferenceMapping(
    string MappingKey,
    QualifiedResourceName TargetResource,
    bool IsRequired,
    bool IsPartOfIdentity,
    JsonPathExpression ReferenceObjectPath,
    IReadOnlyList<ReferenceJsonPathBinding> ReferenceJsonPaths
);

/// <summary>
/// Represents an array uniqueness constraint input, including optional nested constraints.
/// </summary>
/// <param name="BasePath">
/// Optional array base path. When specified, <paramref name="Paths"/> are relative to the base item.
/// </param>
/// <param name="Paths">The constrained JSONPaths.</param>
/// <param name="NestedConstraints">Nested uniqueness constraints scoped by their own base paths.</param>
public sealed record ArrayUniquenessConstraintInput(
    JsonPathExpression? BasePath,
    IReadOnlyList<JsonPathExpression> Paths,
    IReadOnlyList<ArrayUniquenessConstraintInput> NestedConstraints
);
