// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

public class ConfigurationServiceDmsInstanceProviderTests
{
    [TestFixture]
    public class Given_Valid_DmsInstances_From_ConfigService
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private IList<DmsInstance>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dmsInstancesResponse = new[]
            {
                new
                {
                    Id = 1L,
                    InstanceType = "Production",
                    InstanceName = "Main Instance",
                    ConnectionString = "host=localhost;port=5432;database=edfi;",
                    DmsInstanceRouteContexts = new object[]
                    {
                        new
                        {
                            Id = 1L,
                            InstanceId = 1L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                        new
                        {
                            Id = 2L,
                            InstanceId = 1L,
                            ContextKey = "schoolYear",
                            ContextValue = "2024",
                        },
                    },
                },
                new
                {
                    Id = 2L,
                    InstanceType = "Development",
                    InstanceName = "Dev Instance",
                    ConnectionString = "host=devhost;port=5432;database=edfi_dev;",
                    DmsInstanceRouteContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v2/dmsInstances/", dmsInstancesResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            _loadedInstances = await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_load_all_instances()
        {
            _loadedInstances.Should().NotBeNull();
            _loadedInstances!.Count.Should().Be(2);
        }

        [Test]
        public void It_should_set_is_loaded_flag()
        {
            _provider!.IsLoaded().Should().BeTrue();
        }

        [Test]
        public void It_should_return_all_instances_via_get_all()
        {
            var allInstances = _provider!.GetAll();
            allInstances.Should().HaveCount(2);
        }

        [Test]
        public void It_should_return_instance_by_id()
        {
            var instance = _provider!.GetById(1);
            instance.Should().NotBeNull();
            instance!.InstanceName.Should().Be("Main Instance");
            instance.ConnectionString.Should().Be("host=localhost;port=5432;database=edfi;");
        }

        [Test]
        public void It_should_return_null_for_nonexistent_id()
        {
            var instance = _provider!.GetById(999);
            instance.Should().BeNull();
        }

        [Test]
        public void It_should_load_route_contexts_for_instances()
        {
            var instance = _provider!.GetById(1);
            instance.Should().NotBeNull();
            instance!.RouteContext.Should().NotBeNull();
            instance.RouteContext.Should().HaveCount(2);
        }

        [Test]
        public void It_should_map_context_keys_to_context_values()
        {
            var instance = _provider!.GetById(1);
            instance!
                .RouteContext[new RouteQualifierName("district")]
                .Should()
                .Be(new RouteQualifierValue("255901"));
            instance
                .RouteContext[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2024"));
        }

        [Test]
        public void It_should_have_empty_route_context_for_instance_without_contexts()
        {
            var instance = _provider!.GetById(2);
            instance.Should().NotBeNull();
            instance!.RouteContext.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Empty_DmsInstances_From_ConfigService
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private IList<DmsInstance>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "[]");
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            _loadedInstances = await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_return_empty_list()
        {
            _loadedInstances.Should().NotBeNull();
            _loadedInstances!.Count.Should().Be(0);
        }

        [Test]
        public void It_should_set_is_loaded_flag()
        {
            _provider!.IsLoaded().Should().BeTrue();
        }

        [Test]
        public void It_should_return_empty_list_via_get_all()
        {
            var allInstances = _provider!.GetAll();
            allInstances.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_ConfigService_Returns_Error
    {
        [Test]
        public void It_should_throw_exception_on_unauthorized()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("invalid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.Unauthorized, "");
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );

            Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.LoadDmsInstances());
        }

        [Test]
        public void It_should_throw_exception_on_server_error()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.InternalServerError, "");
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );

            Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.LoadDmsInstances());
        }
    }

    [TestFixture]
    public class Given_Multiple_LoadDmsInstances_Calls
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");

            // First call returns one instance
            var firstResponse = new[]
            {
                new
                {
                    Id = 1L,
                    InstanceType = "Production",
                    InstanceName = "First Instance",
                    ConnectionString = "host=first;database=db1;",
                    DmsInstanceRouteContexts = Array.Empty<object>(),
                },
            };

            // Second call returns different instances
            var secondResponse = new[]
            {
                new
                {
                    Id = 2L,
                    InstanceType = "Development",
                    InstanceName = "Second Instance",
                    ConnectionString = "host=second;database=db2;",
                    DmsInstanceRouteContexts = Array.Empty<object>(),
                },
                new
                {
                    Id = 3L,
                    InstanceType = "Staging",
                    InstanceName = "Third Instance",
                    ConnectionString = "host=third;database=db3;",
                    DmsInstanceRouteContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v2/dmsInstances/", firstResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );

            // First load
            await _provider.LoadDmsInstances();

            // Change response for second call
            handler.SetResponse("v2/dmsInstances/", secondResponse);

            // Second load
            await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_replace_instances_with_new_data()
        {
            var allInstances = _provider!.GetAll();
            allInstances.Should().HaveCount(2);
            allInstances.Should().Contain(i => i.InstanceName == "Second Instance");
            allInstances.Should().Contain(i => i.InstanceName == "Third Instance");
            allInstances.Should().NotContain(i => i.InstanceName == "First Instance");
        }

        [Test]
        public void It_should_not_have_old_instance_by_id()
        {
            var instance = _provider!.GetById(1);
            instance.Should().BeNull();
        }

        [Test]
        public void It_should_have_new_instances_by_id()
        {
            var instance2 = _provider!.GetById(2);
            instance2.Should().NotBeNull();

            var instance3 = _provider!.GetById(3);
            instance3.Should().NotBeNull();
        }
    }

    [TestFixture]
    public class Given_DmsInstances_With_Null_ConnectionStrings
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private IList<DmsInstance>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");

            // Use JSON string directly to handle null values properly
            var dmsInstancesJson = """
                [
                    {
                        "id": 1,
                        "instanceType": "Production",
                        "instanceName": "Valid Instance",
                        "connectionString": "host=localhost;database=edfi;",
                        "dmsInstanceRouteContexts": []
                    },
                    {
                        "id": 2,
                        "instanceType": "Development",
                        "instanceName": "Instance With Null Connection",
                        "connectionString": null,
                        "dmsInstanceRouteContexts": []
                    }
                ]
                """;

            handler.SetJsonResponse("v2/dmsInstances/", dmsInstancesJson);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            _loadedInstances = await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_load_instances_including_null_connection_strings()
        {
            _loadedInstances.Should().HaveCount(2);
        }

        [Test]
        public void It_should_store_null_connection_string_correctly()
        {
            var instance = _provider!.GetById(2);
            instance.Should().NotBeNull();
            instance!.ConnectionString.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Instances_With_Multiple_Route_Contexts
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private IList<DmsInstance>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dmsInstancesResponse = new[]
            {
                new
                {
                    Id = 1L,
                    InstanceType = "Production",
                    InstanceName = "District 255901 - 2024",
                    ConnectionString = "host=localhost;database=edfi_255901_2024;",
                    DmsInstanceRouteContexts = new object[]
                    {
                        new
                        {
                            Id = 1L,
                            InstanceId = 1L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                        new
                        {
                            Id = 2L,
                            InstanceId = 1L,
                            ContextKey = "schoolYear",
                            ContextValue = "2024",
                        },
                    },
                },
                new
                {
                    Id = 2L,
                    InstanceType = "Production",
                    InstanceName = "District 255901 - 2025",
                    ConnectionString = "host=localhost;database=edfi_255901_2025;",
                    DmsInstanceRouteContexts = new object[]
                    {
                        new
                        {
                            Id = 3L,
                            InstanceId = 2L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                        new
                        {
                            Id = 4L,
                            InstanceId = 2L,
                            ContextKey = "schoolYear",
                            ContextValue = "2025",
                        },
                    },
                },
                new
                {
                    Id = 3L,
                    InstanceType = "Production",
                    InstanceName = "District 255902 - 2024",
                    ConnectionString = "host=localhost;database=edfi_255902_2024;",
                    DmsInstanceRouteContexts = new object[]
                    {
                        new
                        {
                            Id = 5L,
                            InstanceId = 3L,
                            ContextKey = "district",
                            ContextValue = "255902",
                        },
                        new
                        {
                            Id = 6L,
                            InstanceId = 3L,
                            ContextKey = "schoolYear",
                            ContextValue = "2024",
                        },
                    },
                },
            };

            handler.SetResponse("v2/dmsInstances/", dmsInstancesResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            _loadedInstances = await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_load_all_instances_with_route_contexts()
        {
            _loadedInstances.Should().HaveCount(3);
            _loadedInstances!.Should().AllSatisfy(i => i.RouteContext.Should().HaveCount(2));
        }

        [Test]
        public void It_should_correctly_map_route_contexts_to_first_instance()
        {
            var instance = _provider!.GetById(1);
            instance!
                .RouteContext[new RouteQualifierName("district")]
                .Should()
                .Be(new RouteQualifierValue("255901"));
            instance
                .RouteContext[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2024"));
        }

        [Test]
        public void It_should_correctly_map_route_contexts_to_second_instance()
        {
            var instance = _provider!.GetById(2);
            instance!
                .RouteContext[new RouteQualifierName("district")]
                .Should()
                .Be(new RouteQualifierValue("255901"));
            instance
                .RouteContext[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2025"));
        }

        [Test]
        public void It_should_correctly_map_route_contexts_to_third_instance()
        {
            var instance = _provider!.GetById(3);
            instance!
                .RouteContext[new RouteQualifierName("district")]
                .Should()
                .Be(new RouteQualifierValue("255902"));
            instance
                .RouteContext[new RouteQualifierName("schoolYear")]
                .Should()
                .Be(new RouteQualifierValue("2024"));
        }
    }

    [TestFixture]
    public class Given_Route_Contexts_Without_Matching_Instances
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dmsInstancesResponse = new[]
            {
                new
                {
                    Id = 1L,
                    InstanceType = "Production",
                    InstanceName = "Valid Instance",
                    ConnectionString = "host=localhost;database=edfi;",
                    DmsInstanceRouteContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v2/dmsInstances/", dmsInstancesResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_load_instance_with_empty_route_context()
        {
            var instance = _provider!.GetById(1);
            instance.Should().NotBeNull();
            instance!.RouteContext.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Route_Contexts_Endpoint_Returns_Empty
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private IList<DmsInstance>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dmsInstancesResponse = new[]
            {
                new
                {
                    Id = 1L,
                    InstanceType = "Production",
                    InstanceName = "Instance Without Route Context",
                    ConnectionString = "host=localhost;database=edfi;",
                    DmsInstanceRouteContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v2/dmsInstances/", dmsInstancesResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            _loadedInstances = await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_load_instances_successfully()
        {
            _loadedInstances.Should().HaveCount(1);
        }

        [Test]
        public void It_should_have_empty_route_contexts()
        {
            var instance = _provider!.GetById(1);
            instance.Should().NotBeNull();
            instance!.RouteContext.Should().NotBeNull();
            instance.RouteContext.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_Single_Route_Context_Per_Instance
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dmsInstancesResponse = new[]
            {
                new
                {
                    Id = 1L,
                    InstanceType = "Production",
                    InstanceName = "District Only Instance",
                    ConnectionString = "host=localhost;database=edfi;",
                    DmsInstanceRouteContexts = new object[]
                    {
                        new
                        {
                            Id = 1L,
                            InstanceId = 1L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                    },
                },
            };

            handler.SetResponse("v2/dmsInstances/", dmsInstancesResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_have_single_route_context_entry()
        {
            var instance = _provider!.GetById(1);
            instance!.RouteContext.Should().HaveCount(1);
            instance
                .RouteContext[new RouteQualifierName("district")]
                .Should()
                .Be(new RouteQualifierValue("255901"));
        }
    }

    [TestFixture]
    public class Given_Tenant_Parameter_Provided
    {
        private TestHttpMessageHandler? _handler;
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "[]");

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            await _provider.LoadDmsInstances("TenantA");
        }

        [Test]
        public void It_should_set_tenant_header_on_request()
        {
            _handler!.LastTenantHeader.Should().Be("TenantA");
        }
    }

    [TestFixture]
    public class Given_No_Tenant_Parameter
    {
        private TestHttpMessageHandler? _handler;
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "[]");

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
            await _provider.LoadDmsInstances();
        }

        [Test]
        public void It_should_not_set_tenant_header_on_request()
        {
            _handler!.LastTenantHeader.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Different_Tenants_Called_Sequentially
    {
        [Test]
        public async Task It_should_update_tenant_header_for_each_call()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "[]");

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );

            // First call with TenantA
            await provider.LoadDmsInstances("TenantA");
            handler.LastTenantHeader.Should().Be("TenantA");

            // Second call with TenantB
            await provider.LoadDmsInstances("TenantB");
            handler.LastTenantHeader.Should().Be("TenantB");

            // Third call with no tenant
            await provider.LoadDmsInstances();
            handler.LastTenantHeader.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Multiple_Tenants_Loaded
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "[]");

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );

            // Load instances for multiple tenants
            await _provider.LoadDmsInstances(); // Default tenant (empty string key)
            await _provider.LoadDmsInstances("TenantA");
            await _provider.LoadDmsInstances("TenantB");
        }

        [Test]
        public void It_should_return_all_loaded_tenant_keys()
        {
            var tenantKeys = _provider!.GetLoadedTenantKeys();
            tenantKeys.Should().HaveCount(3);
            tenantKeys.Should().Contain("");
            tenantKeys.Should().Contain("TenantA");
            tenantKeys.Should().Contain("TenantB");
        }
    }

    [TestFixture]
    public class Given_No_Tenants_Loaded
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;

        [SetUp]
        public void Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "[]");

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance
            );
        }

        [Test]
        public void It_should_return_empty_list()
        {
            var tenantKeys = _provider!.GetLoadedTenantKeys();
            tenantKeys.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_DmsInstanceCache_Refresh_Disabled
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private TestHttpMessageHandler? _handler;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse(
                "v2/dmsInstances/",
                new[]
                {
                    new
                    {
                        Id = 1L,
                        InstanceType = "Production",
                        InstanceName = "Initial Instance",
                        ConnectionString = "host=first;database=db1;",
                        DmsInstanceRouteContexts = Array.Empty<object>(),
                    },
                }
            );

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var cacheSettings = new CacheSettings
            {
                DmsInstanceCacheRefreshEnabled = false,
                DmsInstanceCacheExpirationSeconds = 1,
            };

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance,
                cacheSettings
            );

            await _provider.LoadDmsInstances();

            _handler.SetResponse(
                "v2/dmsInstances/",
                new[]
                {
                    new
                    {
                        Id = 2L,
                        InstanceType = "Development",
                        InstanceName = "Updated Instance",
                        ConnectionString = "host=second;database=db2;",
                        DmsInstanceRouteContexts = Array.Empty<object>(),
                    },
                }
            );
        }

        [Test]
        public async Task It_should_not_refresh_when_disabled()
        {
            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v2/dmsInstances/").Should().Be(1);
            _provider.GetAll().Should().ContainSingle(i => i.InstanceName == "Initial Instance");
        }
    }

    [TestFixture]
    public class Given_DmsInstanceCache_Refresh_Not_Expired
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private TestHttpMessageHandler? _handler;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse(
                "v2/dmsInstances/",
                new[]
                {
                    new
                    {
                        Id = 1L,
                        InstanceType = "Production",
                        InstanceName = "Initial Instance",
                        ConnectionString = "host=first;database=db1;",
                        DmsInstanceRouteContexts = Array.Empty<object>(),
                    },
                }
            );

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var cacheSettings = new CacheSettings
            {
                DmsInstanceCacheRefreshEnabled = true,
                DmsInstanceCacheExpirationSeconds = 600,
            };

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance,
                cacheSettings
            );

            await _provider.LoadDmsInstances();

            _handler.SetResponse(
                "v2/dmsInstances/",
                new[]
                {
                    new
                    {
                        Id = 2L,
                        InstanceType = "Development",
                        InstanceName = "Updated Instance",
                        ConnectionString = "host=second;database=db2;",
                        DmsInstanceRouteContexts = Array.Empty<object>(),
                    },
                }
            );
        }

        [Test]
        public async Task It_should_not_refresh_before_expiration()
        {
            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v2/dmsInstances/").Should().Be(1);
            _provider.GetAll().Should().ContainSingle(i => i.InstanceName == "Initial Instance");
        }
    }

    [TestFixture]
    public class Given_DmsInstanceCache_Refresh_Expired
    {
        private ConfigurationServiceDmsInstanceProvider? _provider;
        private TestHttpMessageHandler? _handler;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse(
                "v2/dmsInstances/",
                new[]
                {
                    new
                    {
                        Id = 1L,
                        InstanceType = "Production",
                        InstanceName = "Initial Instance",
                        ConnectionString = "host=first;database=db1;",
                        DmsInstanceRouteContexts = Array.Empty<object>(),
                    },
                }
            );

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var cacheSettings = new CacheSettings
            {
                DmsInstanceCacheRefreshEnabled = true,
                DmsInstanceCacheExpirationSeconds = 1,
            };

            _provider = new ConfigurationServiceDmsInstanceProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDmsInstanceProvider>.Instance,
                cacheSettings
            );

            await _provider.LoadDmsInstances();

            _handler.SetResponse(
                "v2/dmsInstances/",
                new[]
                {
                    new
                    {
                        Id = 2L,
                        InstanceType = "Development",
                        InstanceName = "Updated Instance",
                        ConnectionString = "host=second;database=db2;",
                        DmsInstanceRouteContexts = Array.Empty<object>(),
                    },
                }
            );
        }

        [Test]
        public async Task It_should_refresh_after_expiration()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1100));

            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v2/dmsInstances/").Should().Be(2);
            _provider.GetAll().Should().ContainSingle(i => i.InstanceName == "Updated Instance");
        }
    }

    /// <summary>
    /// Test HTTP message handler that returns predefined responses
    /// </summary>
    private class TestHttpMessageHandler(HttpStatusCode statusCode, string defaultContent = "")
        : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _responses = new();
        private readonly Dictionary<string, string> _jsonResponses = new();
        private readonly Dictionary<string, int> _requestCounts = new();

        /// <summary>
        /// Gets the value of the Tenant header from the last request, or null if not present
        /// </summary>
        public string? LastTenantHeader { get; private set; }

        public void SetResponse(string path, object response)
        {
            _responses[path] = response;
        }

        public void SetJsonResponse(string path, string jsonContent)
        {
            _jsonResponses[path] = jsonContent;
        }

        public int GetRequestCount(string path) =>
            _requestCounts.TryGetValue(path, out var count) ? count : 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            // Capture tenant header from request
            LastTenantHeader = request.Headers.TryGetValues("Tenant", out var values)
                ? values.FirstOrDefault()
                : null;

            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";

            _requestCounts[path] = _requestCounts.TryGetValue(path, out var count) ? count + 1 : 1;

            string content = defaultContent;

            if (_jsonResponses.TryGetValue(path, out var jsonResponse))
            {
                content = jsonResponse;
            }
            else if (_responses.TryGetValue(path, out var response))
            {
                content = JsonSerializer.Serialize(response);
            }

            var httpResponse = new HttpResponseMessage(statusCode) { Content = new StringContent(content) };

            return Task.FromResult(httpResponse);
        }
    }
}
