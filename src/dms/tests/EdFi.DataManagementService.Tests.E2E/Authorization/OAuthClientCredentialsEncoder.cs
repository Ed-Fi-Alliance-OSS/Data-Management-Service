// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

internal static class OAuthClientCredentialsEncoder
{
    public static string CreateBasicSchemeParameter(string clientId, string clientSecret)
    {
        string encodedClientId = Uri.EscapeDataString(clientId);
        string encodedClientSecret = Uri.EscapeDataString(clientSecret);
        byte[] credentialsBytes = Encoding.UTF8.GetBytes($"{encodedClientId}:{encodedClientSecret}");
        return Convert.ToBase64String(credentialsBytes);
    }
}
