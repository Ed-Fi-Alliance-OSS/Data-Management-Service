// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict
{
    public class PostgresClientSqlProvider : IClientSqlProvider
    {
        public string GetFindByIdSql()
            => "SELECT * FROM dmscs.openiddict_tokens WHERE id = @Id";

        public string GetFindByReferenceIdSql()
            => "SELECT * FROM dmscs.openiddict_tokens WHERE reference_id = @ReferenceId";

        public string GetFindBySubjectSql()
            => "SELECT * FROM dmscs.openiddict_tokens WHERE subject = @Subject";

        public string GetCreateSql()
            => @"INSERT INTO dmscs.openiddict_tokens
                (id, application_id, subject, type, reference_id, expiration_date)
              VALUES
                (@Id, @ApplicationId, @Subject, @Type, @ReferenceId, @ExpirationDate)";

        public string GetUpdateSql()
            => @"UPDATE dmscs.openiddict_tokens
                SET application_id = @ApplicationId,
                    subject = @Subject,
                    type = @Type,
                    reference_id = @ReferenceId,
                    expiration_date = @ExpirationDate
              WHERE id = @Id";

        public string GetDeleteSql()
            => "DELETE FROM dmscs.openiddict_tokens WHERE id = @Id";

        public string GetListSql()
            => "SELECT * FROM dmscs.openiddict_tokens";

        string IClientSqlProvider.GetCreateClientSql()
        {
            return @"INSERT INTO dmscs.openiddict_applications
                (id, client_id, client_secret, display_name)
              VALUES
                (@Id, @ClientId, @ClientSecret, @DisplayName)";
        }

        string IClientSqlProvider.GetUpdateClientSql()
        {
            return @"UPDATE dmscs.openiddict_applications
                SET client_id = @ClientId,
                    client_secret = @ClientSecret,
                    display_name = @DisplayName,
                    redirect_uris = @RedirectUris,
                    post_logout_redirect_uris = @PostLogoutRedirectUris,
                    permissions = @Permissions,
                    requirements = @Requirements
              WHERE id = @Id";
        }

        string IClientSqlProvider.GetUpdateNamespaceClaimSql()
        {
            return @"UPDATE dmscs.openiddict_applications
                SET namespace_claim = @NamespaceClaim
              WHERE id = @Id";
        }

        string IClientSqlProvider.GetAllClientsSql()
        {
            return "SELECT * FROM dmscs.openiddict_applications";
        }

        string IClientSqlProvider.GetDeleteClientSql()
        {
            return "DELETE FROM dmscs.openiddict_applications WHERE id = @Id";
        }

        string IClientSqlProvider.GetResetCredentialsSql()
        {
            return @"UPDATE dmscs.openiddict_applications
                SET client_secret = @ClientSecret
              WHERE id = @Id";
        }
    }
}
