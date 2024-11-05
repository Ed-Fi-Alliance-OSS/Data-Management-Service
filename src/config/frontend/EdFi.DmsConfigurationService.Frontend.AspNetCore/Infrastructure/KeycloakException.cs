// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public class KeycloakException : Exception
{
    public KeycloakError KeycloakError { get; }

    public KeycloakException(KeycloakError keycloakError)
        : base(keycloakError.FailureMessage)
    {
        KeycloakError = keycloakError;
    }

    public KeycloakException(Exception innerException, KeycloakError keycloakError)
        : base(keycloakError.FailureMessage, innerException)
    {
        KeycloakError = keycloakError;
    }
}
