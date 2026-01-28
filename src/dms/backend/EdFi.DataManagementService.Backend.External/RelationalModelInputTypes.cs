// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Descriptor metadata for a specific JSONPath value in a resource document.
/// </summary>
/// <param name="DescriptorValuePath">The JSONPath where the descriptor URI/value appears in the document.</param>
/// <param name="DescriptorResource">The descriptor resource type expected at that path.</param>
public readonly record struct DescriptorPathInfo(
    JsonPathExpression DescriptorValuePath,
    QualifiedResourceName DescriptorResource
);

/// <summary>
/// Decimal validation metadata used to deterministically map a JSON Schema <c>number</c> to a relational
/// decimal type.
/// </summary>
/// <param name="Path">The JSONPath of the decimal-valued property.</param>
/// <param name="TotalDigits">The total number of digits (precision).</param>
/// <param name="DecimalPlaces">The number of digits after the decimal point (scale).</param>
public readonly record struct DecimalPropertyValidationInfo(
    JsonPathExpression Path,
    short? TotalDigits,
    short? DecimalPlaces
);
