// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Repositories;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Repository
{
    public class OpenIddictClientRepository : IClientRepository
    {
        private readonly IDbConnection _db;
        private readonly IClientSqlProvider _sqlProvider;

        public OpenIddictClientRepository(IDbConnection db, IClientSqlProvider sqlProvider)
        {
            _db = db;
            _sqlProvider = sqlProvider;
        }

        public async Task<Client?> FindByIdAsync(string id, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetFindByIdSql();
            return await _db.QuerySingleOrDefaultAsync<Client?>(sql, new { Id = id });
        }

        public async Task<IEnumerable<Client>> ListAsync(CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetListSql();
            return await _db.QueryAsync<Client>(sql);
        }

        public async Task CreateAsync(Client client, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetCreateSql();
            await _db.ExecuteAsync(sql, client);
        }

        public async Task UpdateAsync(Client client, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetUpdateSql();
            await _db.ExecuteAsync(sql, client);
        }

        public async Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetDeleteSql();
            await _db.ExecuteAsync(sql, new { Id = id });
        }

        public async Task<ClientCreateResult> CreateClientAsync(string clientId, string clientSecret, string role, string displayName, string scope, string namespacePrefixes, string educationOrganizationIds)
        {
            var sql = _sqlProvider.GetCreateClientSql();
            var clientUuId = Guid.NewGuid();
            var parameters = new
            {
                Id = clientUuId,
                ClientId = clientId,
                ClientSecret = clientSecret,
                Role = role,
                DisplayName = displayName,
                Scope = scope,
                NamespacePrefixes = namespacePrefixes,
                EducationOrganizationIds = educationOrganizationIds
            };
            await _db.ExecuteAsync(sql, parameters);
            return new ClientCreateResult.Success(clientUuId);
        }

        public async Task<ClientUpdateResult> UpdateClientAsync(string clientUuid, string displayName, string scope, string educationOrganizationIds)
        {
            var sql = _sqlProvider.GetUpdateClientSql();
            var parameters = new
            {
                ClientUuid = clientUuid,
                DisplayName = displayName,
                Scope = scope,
                EducationOrganizationIds = educationOrganizationIds
            };
            await _db.ExecuteAsync(sql, parameters);
            return new ClientUpdateResult.Success(Guid.Parse(clientUuid));
        }

        public async Task<ClientUpdateResult> UpdateClientNamespaceClaimAsync(string clientUuid, string namespacePrefixes)
        {
            var sql = _sqlProvider.GetUpdateNamespaceClaimSql();
            var parameters = new
            {
                ClientUuid = clientUuid,
                NamespacePrefixes = namespacePrefixes
            };
            await _db.ExecuteAsync(sql, parameters);
            return new ClientUpdateResult.Success(Guid.Parse(clientUuid));
        }

        public async Task<ClientClientsResult> GetAllClientsAsync()
        {
            try
            {
                var sql = _sqlProvider.GetAllClientsSql();
                var clients = await _db.QueryAsync<Client>(sql);
                var clientList = clients.Select(client => client.ClientId).Where(id => id != null).Cast<string>().ToList();
                return new ClientClientsResult.Success(clientList);
            }
            catch
            {
                throw;
            }
        }

        public async Task<ClientDeleteResult> DeleteClientAsync(string clientUuid)
        {
            var sql = _sqlProvider.GetDeleteClientSql();
            await _db.ExecuteAsync(sql, new { ClientUuid = clientUuid });
            return new ClientDeleteResult.Success();
        }

        public async Task<ClientResetResult> ResetCredentialsAsync(string clientUuid)
        {
            var sql = _sqlProvider.GetResetCredentialsSql();
            await _db.ExecuteAsync(sql, new { ClientUuid = clientUuid });
            return new ClientResetResult.Success(clientUuid);
        }
    }
}
