// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

public static class ValidationConstants
{
    public const int ClaimSetNameMaxLength = 256;

    public const int ApiClientNameMaxLength = 50;

    /// <summary>
    /// An application's first API client takes the application's name, so an application name is held
    /// to an API client name's length, as the Ed-Fi Admin API holds it on create and update.
    /// </summary>
    public const int ApplicationNameMaxLength = ApiClientNameMaxLength;
    public const string ClaimSetNameNoWhiteSpaceMessage = "Claim set name must not contain white spaces.";
    public const string ClaimSetNameNoWhiteSpaceRegex = @"^[^\s]*$";
}
