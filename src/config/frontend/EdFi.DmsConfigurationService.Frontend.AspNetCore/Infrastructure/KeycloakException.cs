// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public class KeycloakException : Exception
{
    public KeycloakFailureType FailureType { get; }

    public KeycloakException(string message, KeycloakFailureType failureType)
        : base(message)
    {
        FailureType = failureType;
    }

    public KeycloakException(string message, Exception innerException, KeycloakFailureType failureType)
        : base(message, innerException)
    {
        FailureType = failureType;
    }
}

public enum KeycloakFailureType
{
    Unreachable,
    BadCredentials,
    InsufficientPermissions,
    InvalidRealm,
    Unknown
}
