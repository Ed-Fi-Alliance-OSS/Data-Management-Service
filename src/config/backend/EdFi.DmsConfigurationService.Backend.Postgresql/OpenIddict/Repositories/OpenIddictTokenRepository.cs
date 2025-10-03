// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories
{
    public class OpenIddictTokenRepository(IOpenIddictDataRepository dataRepository)
        : IOpenIddictTokenRepository
    {
        public async Task<TokenInfo?> GetTokenByIdAsync(Guid tokenId)
        {
            return await dataRepository.GetTokenByIdAsync(tokenId);
        }

        public async Task StoreTokenAsync(
            Guid tokenId,
            Guid applicationId,
            string subject,
            string payload,
            DateTimeOffset expiration
        )
        {
            await dataRepository.StoreTokenAsync(tokenId, applicationId, subject, payload, expiration);
        }

        public async Task<string?> GetTokenStatusAsync(Guid tokenId)
        {
            return await dataRepository.GetTokenStatusAsync(tokenId);
        }

        public async Task<bool> RevokeTokenAsync(Guid tokenId)
        {
            return await dataRepository.RevokeTokenAsync(tokenId);
        }

        public async Task<PrivateKeyInfo?> GetActivePrivateKeyAsync(string encryptionKey)
        {
            var result = await dataRepository.GetActivePrivateKeyInternalAsync(encryptionKey);
            return result.HasValue
                ? new PrivateKeyInfo { PrivateKey = result.Value.PrivateKey, KeyId = result.Value.KeyId }
                : null;
        }

        public async Task<IEnumerable<PublicKeyInfo>> GetActivePublicKeysAsync()
        {
            var results = await dataRepository.GetActivePublicKeysInternalAsync();
            return results.Select(r => new PublicKeyInfo { KeyId = r.KeyId, PublicKey = r.PublicKey });
        }

        public async Task<ApplicationInfo?> GetApplicationByClientIdAsync(string clientId)
        {
            return await dataRepository.GetApplicationByClientIdAsync(clientId);
        }

        public async Task<IEnumerable<string>> GetClientRolesAsync(Guid clientId)
        {
            return await dataRepository.GetClientRolesAsync(clientId);
        }
    }
}
