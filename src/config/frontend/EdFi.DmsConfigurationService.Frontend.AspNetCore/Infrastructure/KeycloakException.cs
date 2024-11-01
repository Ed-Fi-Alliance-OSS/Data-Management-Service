// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public class KeycloakException : Exception
{
    public KeycloakException(string message) : base(message) { }

    public KeycloakException(Exception innerException)
    {
    }

    public KeycloakException(string message, Exception innerException) : base(message, innerException) { }
}
