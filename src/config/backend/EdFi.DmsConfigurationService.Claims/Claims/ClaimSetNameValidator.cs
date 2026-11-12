// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;

namespace EdFi.DmsConfigurationService.Claims;

/// <summary>
/// Applies the Management API claim set name rules without normalizing the supplied name.
/// </summary>
public static class ClaimSetNameValidator
{
    // Preserve the existing CMS regex semantics, including acceptance of a final newline.
    public static bool IsValid(string claimSetName) =>
        claimSetName.Length is > 0 and <= ValidationConstants.ClaimSetNameMaxLength
        && Regex.IsMatch(claimSetName, ValidationConstants.ClaimSetNameNoWhiteSpaceRegex);
}
