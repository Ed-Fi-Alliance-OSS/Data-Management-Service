// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public interface ITokenManager
{
    public Task<TokenResult> GetAccessTokenAsync(IEnumerable<KeyValuePair<string, string>> credentials);
}

public record TokenResult
{
    public record Success(string Token) : TokenResult;

    public record FailureIdentityProvider(IdentityProviderError IdentityProviderError) : TokenResult;

    public record FailureUnknown(string FailureMessage) : TokenResult;
}
