// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;
using EdFi.DmsConfigurationService.Backend;
using FakeItEasy;

public class FakeTokenManager : ITokenManager
{
    public string FakeToken { get; set; } =
        """
        {
            "access_token": "input123token",
            "expires_in": 900,
            "token_type": "bearer"
        }
        """;
    public bool ShouldThrowException { get; set; } = false; // Property to control exception throwing

    public void SetShouldThrowExceptionToTrue(IDictionary<string, string> parameters)
    {
        // Set ShouldThrowException to true when this method is called
        ShouldThrowException = true;
    }

    public Task<string> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        try
        {
            var clientIdPair = parameters.FirstOrDefault(p => p.Key == "client_id");
            var clientSecretPair = parameters.FirstOrDefault(p => p.Key == "client_secret");

            var clientId = clientIdPair.Value ?? string.Empty;
            var clientSecret = clientSecretPair.Value ?? string.Empty;

            // Check if ShouldThrowException is true to throw an error
            if (ShouldThrowException)
            {
                throw new Exception("Error from Keycloak");
            }

            if (string.IsNullOrEmpty(clientId))
            {
                return Task.FromResult(
                """
            {
                "error": "'Client Id' must not be empty."
            }
            """);
            }

            if (string.IsNullOrEmpty(clientSecret))
            {
                return Task.FromResult(
                """
            {
                "error": "'Client Secret' must not be empty."
            }
            """);
            }

            // Return the fake token if the parameters are valid
            return Task.FromResult(FakeToken);

        }
        catch (Exception ex)
        {
            // Log the exception or perform any error handling as needed
            // Return a JSON-formatted error message with the exception details
            return Task.FromResult(
                $"{{\"error\": \"An unexpected error occurred: {ex.Message}\"}}");
        }
        // You can add custom logic here if needed
        //return Task.FromResult(FakeToken);
    }
}

public class TokenRequest
{
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
}
