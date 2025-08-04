// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using System.Collections.Immutable;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using OpenIddict.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Token
{
    public class OpenIddictDapperTokenStore<T> : IOpenIddictTokenStore<T> where T : class
    {
        protected readonly IDbConnection _db;
        protected readonly ITokenSqlProvider _sqlProvider;

        public OpenIddictDapperTokenStore(IDbConnection db, ITokenSqlProvider sqlProvider)
        {
            _db = db;
            _sqlProvider = sqlProvider;
        }

        public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetListSql();
            var result = await _db.QueryAsync<T>(sql);
            return result.LongCount();
        }

        public ValueTask<long> CountAsync<TResult>(Func<IQueryable<T>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async ValueTask CreateAsync(T token, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetCreateSql();
            await _db.ExecuteAsync(sql, token);
        }

        public async ValueTask DeleteAsync(T token, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetDeleteSql();
            var id = await GetIdAsync(token, cancellationToken);
            if (id != null)
            {
                await _db.ExecuteAsync(sql, new { Id = id });
            }
        }

        public IAsyncEnumerable<T> FindAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<T> FindByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<T> FindByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async ValueTask<T?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetFindByIdSql();
            return await _db.QuerySingleOrDefaultAsync<T>(sql, new { Id = identifier });
        }

        public async ValueTask<T?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetFindByReferenceIdSql();
            return await _db.QuerySingleOrDefaultAsync<T>(sql, new { ReferenceId = identifier });
        }

        public async IAsyncEnumerable<T> FindBySubjectAsync(string subject, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetFindBySubjectSql();
            var results = await _db.QueryAsync<T>(sql, new { Subject = subject });

            foreach (var item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }


        public ValueTask<string?> GetApplicationIdAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<T>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetAuthorizationIdAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<DateTimeOffset?> GetCreationDateAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<DateTimeOffset?> GetExpirationDateAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetIdAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetPayloadAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetReferenceIdAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetStatusAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetSubjectAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<string?> GetTypeAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<T> InstantiateAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<T> ListAsync(int? count, int? offset, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<T>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetApplicationIdAsync(T token, string? identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetAuthorizationIdAsync(T token, string? identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetCreationDateAsync(T token, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetExpirationDateAsync(T token, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetPayloadAsync(T token, string? payload, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetPropertiesAsync(T token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetRedemptionDateAsync(T token, DateTimeOffset? date, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetReferenceIdAsync(T token, string? identifier, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetStatusAsync(T token, string? status, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetSubjectAsync(T token, string? subject, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask SetTypeAsync(T token, string? type, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public async ValueTask UpdateAsync(T token, CancellationToken cancellationToken)
        {
            var sql = _sqlProvider.GetUpdateSql();
            await _db.ExecuteAsync(sql, token);
        }

        ValueTask<ImmutableDictionary<string, JsonElement>> IOpenIddictTokenStore<T>.GetPropertiesAsync(T token, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        ValueTask IOpenIddictTokenStore<T>.SetPropertiesAsync(T token, ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
