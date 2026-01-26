// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

public readonly record struct DescriptorPathInfo(
    JsonPathExpression DescriptorValuePath,
    QualifiedResourceName DescriptorResource
);

public readonly record struct DecimalPropertyValidationInfo(
    JsonPathExpression Path,
    short? TotalDigits,
    short? DecimalPlaces
);
