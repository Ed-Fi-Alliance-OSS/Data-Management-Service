// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema.Model;

/// <summary>
/// Handling of possible values and types for query fields.
/// </summary>
/// <param name="JsonPathString">The document path the query field applies to.</param>
/// <param name="Type">The query field type from the queryFieldMapping.</param>
/// <param name="MaxLength">
/// The <c>maxLength</c> that <c>jsonSchemaForInsert</c> declares for the string at
/// <paramref name="JsonPathString"/>, or null when the schema declares none there. It is read from the
/// resource schema rather than the queryFieldMapping, so a filter is bounded by the same declared
/// maxLength as the request-body property.
/// </param>
public record struct JsonPathAndType(string JsonPathString, string Type, int? MaxLength = null);
