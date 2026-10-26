// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

public static class ValidationConstants
{
    public const int ClaimSetNameMaxLength = 256;

    /// <summary>
    /// An application's first API client takes the application's name, and an API client name holds
    /// 50 characters, so an application name is held to 50 as well, as the Ed-Fi Admin API does.
    /// </summary>
    public const int ApplicationNameMaxLength = 50;
    public const string ClaimSetNameNoWhiteSpaceMessage = "Claim set name must not contain white spaces.";
    public const string ClaimSetNameNoWhiteSpaceRegex = @"^[^\s]*$";
}
