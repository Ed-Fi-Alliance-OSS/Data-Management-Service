// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend;
using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
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
        public bool ShouldThrowException = false;

        public Task<string> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> parameters)
        {
            var clientIdPair = parameters.FirstOrDefault(p => p.Key == "client_id");
            var clientSecretPair = parameters.FirstOrDefault(p => p.Key == "client_secret");
            if (!ShouldThrowException)
            {
                return Task.FromResult(FakeToken);
            }
            /* if (clientIdPair.Value == "CSClient1" && clientSecretPair.Value == "test123@Puiu")
{
   return Task.FromResult(FakeToken);
} */

            throw new Exception("Error from Keycloak");
        }
    }

    public class TokenRequest
    {
        public required string ClientId { get; set; }
        public required string ClientSecret { get; set; }
        public class Validator : AbstractValidator<TokenRequest>
        {
            public Validator()
            {
                RuleFor(m => m.ClientId).NotEmpty();
                RuleFor(m => m.ClientSecret).NotEmpty();
            }
        }
    }
}
