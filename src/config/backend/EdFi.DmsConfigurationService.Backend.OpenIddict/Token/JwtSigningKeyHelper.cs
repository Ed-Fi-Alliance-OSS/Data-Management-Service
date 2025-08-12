// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public static class JwtSigningKeyHelper
    {
        public static SecurityKey GenerateSigningKey(string key)
        {
            var keyBytes = Convert.FromBase64String(key);
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(keyBytes, out _);
            return new RsaSecurityKey(rsa) { CryptoProviderFactory = { CacheSignatureProviders = false } };
        }
    }
}
