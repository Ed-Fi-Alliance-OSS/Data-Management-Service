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

    public Task<string> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        // You can add custom logic here if needed
        return Task.FromResult(FakeToken);
    }
}

public class TokenRequest
{
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }
}
