// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("RuntimeSchema")]
public sealed class Given_DocumentCacheAdminRuntimeSchemaBootstrap
{
    private const string MinimalRuntimeSchemaJson = """
        {
          "apiSchemaVersion": "1.0.0",
          "projectSchema": {
            "projectName": "Ed-Fi",
            "projectVersion": "5.0.0",
            "projectEndpointName": "ed-fi",
            "isExtensionProject": false,
            "description": "Test schema",
            "resourceNameMapping": {},
            "caseInsensitiveEndpointNameMapping": {},
            "educationOrganizationHierarchy": {},
            "educationOrganizationTypes": [],
            "resourceSchemas": {
              "students": {
                "resourceName": "Student",
                "isDescriptor": false,
                "isSchoolYearEnumeration": false,
                "isResourceExtension": false,
                "allowIdentityUpdates": false,
                "isSubclass": false,
                "identityJsonPaths": [
                  "$.studentUniqueId"
                ],
                "booleanJsonPaths": [],
                "numericJsonPaths": [],
                "dateJsonPaths": [],
                "dateTimeJsonPaths": [],
                "equalityConstraints": [],
                "arrayUniquenessConstraints": [],
                "documentPathsMapping": {
                  "StudentUniqueId": {
                    "isReference": false,
                    "isPartOfIdentity": true,
                    "isRequired": true,
                    "path": "$.studentUniqueId"
                  },
                  "FirstName": {
                    "isReference": false,
                    "isPartOfIdentity": false,
                    "isRequired": true,
                    "path": "$.firstName"
                  }
                },
                "queryFieldMapping": {},
                "securableElements": {
                  "Namespace": [],
                  "EducationOrganization": [],
                  "Student": [],
                  "Contact": [],
                  "Staff": []
                },
                "authorizationPathways": [],
                "decimalPropertyValidationInfos": [],
                "jsonSchemaForInsert": {
                  "type": "object",
                  "properties": {
                    "studentUniqueId": {
                      "type": "string",
                      "maxLength": 32
                    },
                    "firstName": {
                      "type": "string",
                      "maxLength": 75
                    }
                  },
                  "required": [
                    "studentUniqueId",
                    "firstName"
                  ]
                }
              }
            },
            "abstractResources": {}
          }
        }
        """;

    [TestCase("postgresql")]
    [TestCase("mssql")]
    public async Task It_initializes_both_effective_schema_providers_without_compiling_the_current_runtime_mapping_set(
        string datastore
    )
    {
        ThrowingMappingSetProvider mappingSetProvider = new(
            "Common CLI runtime initialization must not compile a mapping set."
        );
        await using ServiceProvider serviceProvider = CreateServiceProvider(
            datastore,
            services =>
            {
                services.RemoveAll<IRuntimeMappingSetCompiler>();
                services.Replace(ServiceDescriptor.Singleton<IMappingSetProvider>(mappingSetProvider));
            }
        );

        await DocumentCacheAdminRuntimeInitializer.InitializeAsync(serviceProvider);

        var effectiveSchemaSetProvider = serviceProvider.GetRequiredService<IEffectiveSchemaSetProvider>();
        effectiveSchemaSetProvider.IsInitialized.Should().BeTrue();
        effectiveSchemaSetProvider
            .EffectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Should()
            .ContainSingle()
            .Which.Resource.ResourceName.Should()
            .Be("Student");

        var effectiveApiSchemaProvider = serviceProvider.GetRequiredService<IEffectiveApiSchemaProvider>();
        effectiveApiSchemaProvider.IsInitialized.Should().BeTrue();
        effectiveApiSchemaProvider.Documents.GetAllProjectSchemas().Should().ContainSingle();

        serviceProvider.GetServices<IRuntimeMappingSetCompiler>().Should().BeEmpty();
        mappingSetProvider.GetOrCreateCount.Should().Be(0);
    }

    private static ServiceProvider CreateServiceProvider(
        string datastore,
        Action<IServiceCollection>? configureServices = null
    )
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDocumentCacheAdminRuntimeServices(
            CreateConfiguration(datastore),
            new LoggerConfiguration().CreateLogger(),
            DocumentCacheTargetKey.Create(string.Empty, 1)
        );
        services.Replace(ServiceDescriptor.Singleton(CreateApiSchemaProvider()));
        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static IApiSchemaProvider CreateApiSchemaProvider()
    {
        ApiSchemaDocumentNodes schemaNodes = new(JsonNode.Parse(MinimalRuntimeSchemaJson)!, []);
        var apiSchemaProvider = A.Fake<IApiSchemaProvider>();

        A.CallTo(() => apiSchemaProvider.GetApiSchemaNodes()).Returns(schemaNodes);
        A.CallTo(() => apiSchemaProvider.SchemaLoadId)
            .Returns(Guid.Parse("7f7cad98-0694-4071-a4a0-b970f272f6d2"));
        A.CallTo(() => apiSchemaProvider.IsSchemaValid).Returns(true);
        A.CallTo(() => apiSchemaProvider.ApiSchemaFailures).Returns([]);

        return apiSchemaProvider;
    }

    private static IConfiguration CreateConfiguration(string datastore) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Datastore"] = datastore,
                    ["AppSettings:AllowIdentityUpdateOverrides"] = string.Empty,
                    ["AppSettings:DefaultPartitionCount"] = "10",
                    ["ConfigurationServiceSettings:BaseUrl"] = "https://cms.example.org",
                    ["ConfigurationServiceSettings:ClientId"] = "client-id",
                    ["ConfigurationServiceSettings:ClientSecret"] = "client-secret",
                    ["ConfigurationServiceSettings:Scope"] = "scope",
                    ["ConfigurationServiceSettings:EncryptionKey"] =
                        "TestEncryptionKey123456789012345678901234567890",
                }
            )
            .Build();
}
