// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

public class ConfigurationServiceDataStoreProviderTests
{
    private const string TestEncryptionKey = "TestEncryptionKey123456789012345678901234567890";

    /// <summary>
    /// Mirrors the CMS ConnectionStringEncryptionService.Encrypt() method exactly.
    /// </summary>
    private static string EncryptToBase64(string plainText, string encryptionKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        byte[] result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    [TestFixture]
    public class Given_Valid_DataStores_From_ConfigService
    {
        private ConfigurationServiceDataStoreProvider? _provider;
        private IList<DataStore>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dataStoresResponse = new[]
            {
                new
                {
                    Id = 1L,
                    DataStoreType = "Production",
                    Name = "Main Instance",
                    ConnectionString = EncryptToBase64(
                        "host=localhost;port=5432;database=edfi;",
                        TestEncryptionKey
                    ),
                    DataStoreContexts = new object[]
                    {
                        new
                        {
                            Id = 1L,
                            DataStoreId = 1L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                        new
                        {
                            Id = 2L,
                            DataStoreId = 1L,
                            ContextKey = "schoolYear",
                            ContextValue = "2024",
                        },
                    },
                },
                new
                {
                    Id = 2L,
                    DataStoreType = "Development",
                    Name = "Dev Instance",
                    ConnectionString = EncryptToBase64(
                        "host=devhost;port=5432;database=edfi_dev;",
                        TestEncryptionKey
                    ),
                    DataStoreContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v3/dataStores/", dataStoresResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            _loadedInstances = await _provider.LoadDataStores();
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
            instance!.Name.Should().Be("Main Instance");
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
    public class Given_Empty_DataStores_From_ConfigService
    {
        private ConfigurationServiceDataStoreProvider? _provider;
        private IList<DataStore>? _loadedInstances;

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

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            _loadedInstances = await _provider.LoadDataStores();
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

            var provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );

            Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.LoadDataStores());
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

            var provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );

            Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.LoadDataStores());
        }
    }

    [TestFixture]
    public class Given_Multiple_LoadDataStores_Calls
    {
        private ConfigurationServiceDataStoreProvider? _provider;

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
                    DataStoreType = "Production",
                    Name = "First Instance",
                    ConnectionString = EncryptToBase64("host=first;database=db1;", TestEncryptionKey),
                    DataStoreContexts = Array.Empty<object>(),
                },
            };

            // Second call returns different instances
            var secondResponse = new[]
            {
                new
                {
                    Id = 2L,
                    DataStoreType = "Development",
                    Name = "Second Instance",
                    ConnectionString = EncryptToBase64("host=second;database=db2;", TestEncryptionKey),
                    DataStoreContexts = Array.Empty<object>(),
                },
                new
                {
                    Id = 3L,
                    DataStoreType = "Staging",
                    Name = "Third Instance",
                    ConnectionString = EncryptToBase64("host=third;database=db3;", TestEncryptionKey),
                    DataStoreContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v3/dataStores/", firstResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );

            // First load
            await _provider.LoadDataStores();

            // Change response for second call
            handler.SetResponse("v3/dataStores/", secondResponse);

            // Second load
            await _provider.LoadDataStores();
        }

        [Test]
        public void It_should_replace_instances_with_new_data()
        {
            var allInstances = _provider!.GetAll();
            allInstances.Should().HaveCount(2);
            allInstances.Should().Contain(i => i.Name == "Second Instance");
            allInstances.Should().Contain(i => i.Name == "Third Instance");
            allInstances.Should().NotContain(i => i.Name == "First Instance");
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
    public class Given_DataStores_With_Null_ConnectionStrings
    {
        private ConfigurationServiceDataStoreProvider? _provider;
        private IList<DataStore>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");

            var dataStoresResponse = new object[]
            {
                new
                {
                    Id = 1L,
                    DataStoreType = "Production",
                    Name = "Valid Instance",
                    ConnectionString = (string?)EncryptToBase64(
                        "host=localhost;database=edfi;",
                        TestEncryptionKey
                    ),
                    DataStoreContexts = Array.Empty<object>(),
                },
                new
                {
                    Id = 2L,
                    DataStoreType = "Development",
                    Name = "Instance With Null Connection",
                    ConnectionString = (string?)null,
                    DataStoreContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v3/dataStores/", dataStoresResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            _loadedInstances = await _provider.LoadDataStores();
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
        private ConfigurationServiceDataStoreProvider? _provider;
        private IList<DataStore>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dataStoresResponse = new[]
            {
                new
                {
                    Id = 1L,
                    DataStoreType = "Production",
                    Name = "District 255901 - 2024",
                    ConnectionString = EncryptToBase64(
                        "host=localhost;database=edfi_255901_2024;",
                        TestEncryptionKey
                    ),
                    DataStoreContexts = new object[]
                    {
                        new
                        {
                            Id = 1L,
                            DataStoreId = 1L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                        new
                        {
                            Id = 2L,
                            DataStoreId = 1L,
                            ContextKey = "schoolYear",
                            ContextValue = "2024",
                        },
                    },
                },
                new
                {
                    Id = 2L,
                    DataStoreType = "Production",
                    Name = "District 255901 - 2025",
                    ConnectionString = EncryptToBase64(
                        "host=localhost;database=edfi_255901_2025;",
                        TestEncryptionKey
                    ),
                    DataStoreContexts = new object[]
                    {
                        new
                        {
                            Id = 3L,
                            DataStoreId = 2L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                        new
                        {
                            Id = 4L,
                            DataStoreId = 2L,
                            ContextKey = "schoolYear",
                            ContextValue = "2025",
                        },
                    },
                },
                new
                {
                    Id = 3L,
                    DataStoreType = "Production",
                    Name = "District 255902 - 2024",
                    ConnectionString = EncryptToBase64(
                        "host=localhost;database=edfi_255902_2024;",
                        TestEncryptionKey
                    ),
                    DataStoreContexts = new object[]
                    {
                        new
                        {
                            Id = 5L,
                            DataStoreId = 3L,
                            ContextKey = "district",
                            ContextValue = "255902",
                        },
                        new
                        {
                            Id = 6L,
                            DataStoreId = 3L,
                            ContextKey = "schoolYear",
                            ContextValue = "2024",
                        },
                    },
                },
            };

            handler.SetResponse("v3/dataStores/", dataStoresResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            _loadedInstances = await _provider.LoadDataStores();
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
        private ConfigurationServiceDataStoreProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dataStoresResponse = new[]
            {
                new
                {
                    Id = 1L,
                    DataStoreType = "Production",
                    Name = "Valid Instance",
                    ConnectionString = EncryptToBase64("host=localhost;database=edfi;", TestEncryptionKey),
                    DataStoreContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v3/dataStores/", dataStoresResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            await _provider.LoadDataStores();
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
        private ConfigurationServiceDataStoreProvider? _provider;
        private IList<DataStore>? _loadedInstances;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dataStoresResponse = new[]
            {
                new
                {
                    Id = 1L,
                    DataStoreType = "Production",
                    Name = "Instance Without Route Context",
                    ConnectionString = EncryptToBase64("host=localhost;database=edfi;", TestEncryptionKey),
                    DataStoreContexts = Array.Empty<object>(),
                },
            };

            handler.SetResponse("v3/dataStores/", dataStoresResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            _loadedInstances = await _provider.LoadDataStores();
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
        private ConfigurationServiceDataStoreProvider? _provider;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            var handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            var dataStoresResponse = new[]
            {
                new
                {
                    Id = 1L,
                    DataStoreType = "Production",
                    Name = "District Only Instance",
                    ConnectionString = EncryptToBase64("host=localhost;database=edfi;", TestEncryptionKey),
                    DataStoreContexts = new object[]
                    {
                        new
                        {
                            Id = 1L,
                            DataStoreId = 1L,
                            ContextKey = "district",
                            ContextValue = "255901",
                        },
                    },
                },
            };

            handler.SetResponse("v3/dataStores/", dataStoresResponse);

            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            await _provider.LoadDataStores();
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
        private ConfigurationServiceDataStoreProvider? _provider;

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

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            await _provider.LoadDataStores("TenantA");
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
        private ConfigurationServiceDataStoreProvider? _provider;

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

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );
            await _provider.LoadDataStores();
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

            var provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );

            // First call with TenantA
            await provider.LoadDataStores("TenantA");
            handler.LastTenantHeader.Should().Be("TenantA");

            // Second call with TenantB
            await provider.LoadDataStores("TenantB");
            handler.LastTenantHeader.Should().Be("TenantB");

            // Third call with no tenant
            await provider.LoadDataStores();
            handler.LastTenantHeader.Should().BeNull();
        }
    }

    [TestFixture]
    public class Given_Multiple_Tenants_Loaded
    {
        private ConfigurationServiceDataStoreProvider? _provider;

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

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
            );

            // Load instances for multiple tenants
            await _provider.LoadDataStores(); // Default tenant (empty string key)
            await _provider.LoadDataStores("TenantA");
            await _provider.LoadDataStores("TenantB");
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
        private ConfigurationServiceDataStoreProvider? _provider;

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

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey)
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
    public class Given_DataStoreCache_Refresh_Disabled
    {
        private ConfigurationServiceDataStoreProvider? _provider;
        private FakeTimeProvider? _fakeTimeProvider;
        private TestHttpMessageHandler? _handler;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse(
                "v3/dataStores/",
                new[]
                {
                    new
                    {
                        Id = 1L,
                        DataStoreType = "Production",
                        Name = "Initial Instance",
                        ConnectionString = EncryptToBase64("host=first;database=db1;", TestEncryptionKey),
                        DataStoreContexts = Array.Empty<object>(),
                    },
                }
            );

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var cacheSettings = new CacheSettings
            {
                DataStoreCacheRefreshEnabled = false,
                DataStoreCacheExpirationSeconds = 1,
            };

            _fakeTimeProvider = new FakeTimeProvider();
            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey),
                cacheSettings,
                _fakeTimeProvider
            );

            await _provider.LoadDataStores();

            _handler.SetResponse(
                "v3/dataStores/",
                new[]
                {
                    new
                    {
                        Id = 2L,
                        DataStoreType = "Development",
                        Name = "Updated Instance",
                        ConnectionString = EncryptToBase64("host=second;database=db2;", TestEncryptionKey),
                        DataStoreContexts = Array.Empty<object>(),
                    },
                }
            );
        }

        [Test]
        public async Task It_should_not_refresh_when_disabled()
        {
            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v3/dataStores/").Should().Be(1);
            _provider.GetAll().Should().ContainSingle(i => i.Name == "Initial Instance");
        }
    }

    [TestFixture]
    public class Given_DataStoreCache_Refresh_Not_Expired
    {
        private ConfigurationServiceDataStoreProvider? _provider;
        private TestHttpMessageHandler? _handler;

        [SetUp]
        public async Task Setup()
        {
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse(
                "v3/dataStores/",
                new[]
                {
                    new
                    {
                        Id = 1L,
                        DataStoreType = "Production",
                        Name = "Initial Instance",
                        ConnectionString = EncryptToBase64("host=first;database=db1;", TestEncryptionKey),
                        DataStoreContexts = Array.Empty<object>(),
                    },
                }
            );

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var cacheSettings = new CacheSettings
            {
                DataStoreCacheRefreshEnabled = true,
                DataStoreCacheExpirationSeconds = 600,
            };

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey),
                cacheSettings
            );

            await _provider.LoadDataStores();

            _handler.SetResponse(
                "v3/dataStores/",
                new[]
                {
                    new
                    {
                        Id = 2L,
                        DataStoreType = "Development",
                        Name = "Updated Instance",
                        ConnectionString = EncryptToBase64("host=second;database=db2;", TestEncryptionKey),
                        DataStoreContexts = Array.Empty<object>(),
                    },
                }
            );
        }

        [Test]
        public async Task It_should_not_refresh_before_expiration()
        {
            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v3/dataStores/").Should().Be(1);
            _provider.GetAll().Should().ContainSingle(i => i.Name == "Initial Instance");
        }
    }

    [TestFixture]
    public class Given_DataStoreCache_Refresh_Expired
    {
        private ConfigurationServiceDataStoreProvider? _provider;
        private FakeTimeProvider? _fakeTimeProvider;
        private TestHttpMessageHandler? _handler;

        [SetUp]
        public async Task Setup()
        {
            _fakeTimeProvider = new FakeTimeProvider();
            var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            A.CallTo(() => tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._))
                .Returns("valid-token");

            _handler = new TestHttpMessageHandler(HttpStatusCode.OK, "");
            _handler.SetResponse(
                "v3/dataStores/",
                new[]
                {
                    new
                    {
                        Id = 1L,
                        DataStoreType = "Production",
                        Name = "Initial Instance",
                        ConnectionString = EncryptToBase64("host=first;database=db1;", TestEncryptionKey),
                        DataStoreContexts = Array.Empty<object>(),
                    },
                }
            );

            var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.com/") };
            var apiClient = new ConfigurationServiceApiClient(httpClient);
            var context = new ConfigurationServiceContext("clientId", "secret", "scope");

            var cacheSettings = new CacheSettings
            {
                DataStoreCacheRefreshEnabled = true,
                DataStoreCacheExpirationSeconds = 10,
            };

            _provider = new ConfigurationServiceDataStoreProvider(
                apiClient,
                tokenHandler,
                context,
                NullLogger<ConfigurationServiceDataStoreProvider>.Instance,
                new ConnectionStringDecryptionService(TestEncryptionKey),
                cacheSettings,
                _fakeTimeProvider
            );

            await _provider.LoadDataStores();

            _handler.SetResponse(
                "v3/dataStores/",
                new[]
                {
                    new
                    {
                        Id = 2L,
                        DataStoreType = "Development",
                        Name = "Updated Instance",
                        ConnectionString = EncryptToBase64("host=second;database=db2;", TestEncryptionKey),
                        DataStoreContexts = Array.Empty<object>(),
                    },
                }
            );
        }

        [Test]
        public async Task It_should_refresh_after_expiration()
        {
            _fakeTimeProvider!.Advance(TimeSpan.FromSeconds(11));

            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v3/dataStores/").Should().Be(2);
            _provider.GetAll().Should().Contain(i => i.Name == "Updated Instance");
        }

        [Test]
        public async Task It_should_not_refresh_before_expiration()
        {
            _fakeTimeProvider!.Advance(TimeSpan.FromSeconds(2));

            await _provider!.RefreshInstancesIfExpiredAsync();

            _handler!.GetRequestCount("v3/dataStores/").Should().Be(1);
            _provider.GetAll().Should().NotContain(i => i.Name == "Updated Instance");
        }
    }

    /// <summary>
    /// Test HTTP message handler that returns predefined responses
    /// </summary>
    private class TestHttpMessageHandler(HttpStatusCode statusCode, string defaultContent = "")
        : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _responses = new();
        private readonly Dictionary<string, int> _requestCounts = new();

        /// <summary>
        /// Gets the value of the Tenant header from the last request, or null if not present
        /// </summary>
        public string? LastTenantHeader { get; private set; }

        public void SetResponse(string path, object response)
        {
            _responses[path] = response;
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

            if (_responses.TryGetValue(path, out var response))
            {
                content = JsonSerializer.Serialize(response);
            }

            var httpResponse = new HttpResponseMessage(statusCode) { Content = new StringContent(content) };

            return Task.FromResult(httpResponse);
        }
    }
}
