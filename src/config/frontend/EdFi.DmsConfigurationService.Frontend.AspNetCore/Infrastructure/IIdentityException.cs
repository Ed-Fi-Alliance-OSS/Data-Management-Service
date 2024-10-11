// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

public class IdentityException : Exception
{
    public IdentityException(string message) : base(message) { }
    public IdentityException(string message, Exception innerException) : base(message, innerException) { }
    public HttpStatusCode? StatusCode { get; set; }
}
