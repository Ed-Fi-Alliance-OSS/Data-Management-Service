// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Deploy;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[TestFixture]
public class Given_the_embedded_resources_of_the_cms_mssql_backend
{
    private static readonly string[] _resourceNames = typeof(DatabaseDeploy)
        .Assembly.GetManifestResourceNames()
        .ToArray();

    [Test]
    public void It_deploys_only_the_numbered_upgrade_scripts()
    {
        string[] deployed = _resourceNames.Where(DatabaseDeploy.IsDeployScript).ToArray();

        deployed.Should().NotBeEmpty();
        deployed.Should().OnlyContain(name => name.Contains(".Deploy.Scripts.0", StringComparison.Ordinal));
    }

    [Test]
    public void It_never_deploys_the_shared_document_cache_catalog_query()
    {
        // The catalog query expects its caller to declare @require_native_document_cache, so running
        // it as a migration fails CMS startup with "Must declare the scalar variable".
        _resourceNames.Should().Contain("MssqlDocumentCacheCatalog.sql");
        DatabaseDeploy.IsDeployScript("MssqlDocumentCacheCatalog.sql").Should().BeFalse();
    }
}
