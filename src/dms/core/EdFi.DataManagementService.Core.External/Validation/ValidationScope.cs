// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Validation;

/// <summary>
/// The slice of the deployment a write belongs to, so a registered validator can scope its rule to
/// less than the entire host process.
/// A registration is a property of a DMS deployment rather than of a district, so every registered
/// validator runs for every write the host serves unless it inspects this scope itself.
/// This is a carrier, not a value to compare: being a record it has value equality, but that
/// equality compares <see cref="RouteQualifiers"/> with the default comparer for the declared
/// interface type. The BCL dictionary types do not override equality, so in practice two scopes
/// holding equal qualifier entries in different dictionary instances are not equal.
/// </summary>
/// <param name="Tenant">
/// The tenant the write belongs to, or null in every single-tenant deployment.
/// </param>
/// <param name="RouteQualifiers">
/// The route qualifiers (for example district and school year) the write was routed through.
/// Empty in a deployment with no route qualifiers, never null in a scope DMS builds; the type is
/// non-nullable but nothing in the constructor enforces that, so a validator reading this defensively
/// is not being paranoid.
/// The declared <see cref="IReadOnlyDictionary{TKey, TValue}"/> type documents the contract's intent
/// that a validator must not mutate this collection; it is not itself a defensive copy, and nothing in
/// the type system stops a caller from passing a downcastable mutable instance.
/// </param>
public sealed record ValidationScope(
    string? Tenant,
    IReadOnlyDictionary<RouteQualifierName, RouteQualifierValue> RouteQualifiers
);
