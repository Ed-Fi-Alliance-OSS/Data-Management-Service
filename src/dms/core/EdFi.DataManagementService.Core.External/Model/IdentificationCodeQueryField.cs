// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// A query field a resource offers over one scalar property of its identification-code collection.
/// </summary>
/// <param name="QueryFieldName">The query parameter name, which is the item property name.</param>
/// <param name="CollectionPath">The collection's items, for example <c>$.identificationCodes[*]</c>.</param>
/// <param name="ItemPath">
/// The property within an item, for example <c>$.identificationCodes[*].identificationCode</c>.
/// </param>
/// <param name="Type">
/// The query field type, spelled as <c>queryFieldMapping</c> spells it: <c>string</c>, <c>number</c>,
/// <c>boolean</c>, <c>date</c>, <c>date-time</c> or <c>time</c>.
/// </param>
/// <param name="MaxLength">The <c>maxLength</c> the insert schema declares for a string, if any.</param>
public sealed record IdentificationCodeQueryField(
    string QueryFieldName,
    JsonPath CollectionPath,
    JsonPath ItemPath,
    string Type,
    int? MaxLength
);
