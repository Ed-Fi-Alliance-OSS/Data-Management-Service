// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// DMS-1529: the constraint-name recognition behind <see cref="MssqlExceptionExtensions"/> reads
/// the name from the position SQL Server quotes it at, so a duplicate value that spells another
/// constraint's name cannot be mistaken for that constraint. These fixtures exercise the message
/// formats of errors 2627, 2601 and 547 without a database connection; they carry the lane category
/// so the SQL Server lane filter selects them.
/// </summary>
public class MssqlExceptionExtensionsTests
{
    private const string NameConstraint = "UX_ApiClient_ApplicationId_Name";
    private const string KeyConstraint = "UX_ApiClient_ClientId";

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_unique_constraint_violation_whose_duplicate_value_spells_another_constraint
        : MssqlExceptionExtensionsTests
    {
        private const string Message =
            $"Violation of UNIQUE KEY constraint '{KeyConstraint}'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is ({NameConstraint}).";

        private string? _violatedName;

        [SetUp]
        public void Setup() =>
            _violatedName = MssqlExceptionExtensions.UniqueViolationConstraintName(2627, Message);

        [Test]
        public void It_names_the_violated_constraint() => _violatedName.Should().Be(KeyConstraint);

        [Test]
        public void It_does_not_name_the_constraint_the_duplicate_value_spells() =>
            _violatedName.Should().NotBe(NameConstraint);
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_primary_key_violation : MssqlExceptionExtensionsTests
    {
        private const string Message =
            "Violation of PRIMARY KEY constraint 'PK_ApiClient'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is (42).";

        [Test]
        public void It_names_the_primary_key() =>
            MssqlExceptionExtensions.UniqueViolationConstraintName(2627, Message).Should().Be("PK_ApiClient");
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_unique_index_violation_whose_duplicate_value_spells_another_constraint
        : MssqlExceptionExtensionsTests
    {
        private const string Message =
            $"Cannot insert duplicate key row in object 'dmscs.ApiClient' with unique index '{KeyConstraint}'. The duplicate key value is ({NameConstraint}).";

        private string? _violatedName;

        [SetUp]
        public void Setup() =>
            _violatedName = MssqlExceptionExtensions.UniqueViolationConstraintName(2601, Message);

        [Test]
        public void It_names_the_violated_index_rather_than_the_object() =>
            _violatedName.Should().Be(KeyConstraint);

        [Test]
        public void It_does_not_name_the_constraint_the_duplicate_value_spells() =>
            _violatedName.Should().NotBe(NameConstraint);
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_a_unique_violation_whose_duplicate_value_contains_quotes
        : MssqlExceptionExtensionsTests
    {
        private const string Value = "it's 'quoted' (\"twice\")";

        [Test]
        public void It_names_the_constraint_of_error_2627() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    2627,
                    $"Violation of UNIQUE KEY constraint '{KeyConstraint}'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is ({Value})."
                )
                .Should()
                .Be(KeyConstraint);

        [Test]
        public void It_names_the_index_of_error_2601() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    2601,
                    $"Cannot insert duplicate key row in object 'dmscs.ApiClient' with unique index '{KeyConstraint}'. The duplicate key value is ({Value})."
                )
                .Should()
                .Be(KeyConstraint);
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_errors_that_are_not_unique_violations : MssqlExceptionExtensionsTests
    {
        [Test]
        public void It_names_nothing_for_a_foreign_key_error_number() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    547,
                    $"The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_ApiClient_Application\". The duplicate key value is ({KeyConstraint})."
                )
                .Should()
                .BeNull();

        [Test]
        public void It_names_nothing_for_a_unique_error_number_whose_message_carries_no_quoted_name() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(2627, $"Unique violation on {KeyConstraint}")
                .Should()
                .BeNull();

        [Test]
        public void It_names_nothing_for_a_unique_index_error_number_with_a_single_quoted_token() =>
            MssqlExceptionExtensions
                .UniqueViolationConstraintName(
                    2601,
                    $"Cannot insert duplicate key row in object '{KeyConstraint}'."
                )
                .Should()
                .BeNull();
    }

    [TestFixture]
    [Category("MssqlIntegration")]
    public class Given_foreign_key_violations : MssqlExceptionExtensionsTests
    {
        [Test]
        public void It_names_the_foreign_key_of_an_insert_conflict() =>
            MssqlExceptionExtensions
                .ForeignKeyViolationConstraintName(
                    547,
                    "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_ApiClient_Application\". The conflict occurred in database \"edfi\", table \"dmscs.Application\", column 'Id'."
                )
                .Should()
                .Be("FK_ApiClient_Application");

        [Test]
        public void It_names_the_foreign_key_of_a_delete_conflict() =>
            MssqlExceptionExtensions
                .ForeignKeyViolationConstraintName(
                    547,
                    "The DELETE statement conflicted with the REFERENCE constraint \"FK_ApplicationProfile_Profile\". The conflict occurred in database \"edfi\", table \"dmscs.ApplicationProfile\", column 'ProfileId'."
                )
                .Should()
                .Be("FK_ApplicationProfile_Profile");

        [Test]
        public void It_names_nothing_for_a_unique_error_number() =>
            MssqlExceptionExtensions
                .ForeignKeyViolationConstraintName(
                    2627,
                    "Violation of UNIQUE KEY constraint 'UX_ApiClient_ClientId'. Cannot insert duplicate key in object 'dmscs.ApiClient'. The duplicate key value is (\"FK_ApiClient_Application\")."
                )
                .Should()
                .BeNull();
    }

    /// <summary>
    /// DMS-1529 review round 2: SQL Server localizes these messages to the session language, and
    /// the positions read above are the ones the us_english messages quote the names at. Both SQL
    /// Server registrations pin the session language of the configured connection to us_english,
    /// so a connection string configured for German still opens us_english sessions, and a repeated
    /// key that spells another constraint's name is classified by the constraint SQL Server names.
    /// </summary>
    [TestFixture]
    public class Given_a_database_connection_configured_in_german : DatabaseTest
    {
        private string _configuredSessionLanguage = null!;
        private string _datastoreSessionLanguage = null!;
        private string _openIddictSessionLanguage = null!;
        private ApiClientInsertResult _repeatKeyWithDistinctName = null!;

        [SetUp]
        public async Task Setup()
        {
            string germanConnection = new SqlConnectionStringBuilder(
                MssqlTestConfiguration.DatabaseConnectionString
            )
            {
                CurrentLanguage = "Deutsch",
            }.ConnectionString;

            IOptions<DatabaseOptions> datastoreOptions = ResolveDatabaseOptions(
                germanConnection,
                services => services.AddMssqlDatastore()
            );
            IOptions<DatabaseOptions> openIddictOptions = ResolveDatabaseOptions(
                germanConnection,
                services => services.AddMssqlOpenIddictStores(IdentityConfiguration(), "http://localhost")
            );

            _configuredSessionLanguage = await SessionLanguageAsync(germanConnection);
            _datastoreSessionLanguage = await SessionLanguageAsync(datastoreOptions.Value.DatabaseConnection);
            _openIddictSessionLanguage = await SessionLanguageAsync(
                openIddictOptions.Value.DatabaseConnection
            );

            TenantContextProvider tenantContextProvider = new();
            IApiClientRepository apiClientRepository = new ApiClientRepository(
                datastoreOptions,
                NullLogger<ApiClientRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );
            IApplicationRepository applicationRepository = new ApplicationRepository(
                datastoreOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                tenantContextProvider
            );

            int vendorId =
                await ApiClientTests.Given_api_client_inserts_that_repeat_an_identifier.InsertVendor(
                    tenantContextProvider
                );
            int applicationId =
                await ApiClientTests.Given_api_client_inserts_that_repeat_an_identifier.InsertApplication(
                    applicationRepository,
                    vendorId,
                    "DMS1529 German Session Application"
                );

            (
                await apiClientRepository.InsertApiClient(
                    new ApiClientInsertCommand
                    {
                        ApplicationId = applicationId,
                        Name = "Holder Of The Spelled Key",
                        IsApproved = true,
                        DataStoreIds = [],
                    },
                    new ApiClientCommand { ClientId = NameConstraint, ClientUuid = Guid.NewGuid() }
                )
            ).Should().BeOfType<ApiClientInsertResult.Success>();

            // A distinct name, so the only constraint the repeat can violate is UX_ApiClient_ClientId,
            // while its duplicate value spells UX_ApiClient_ApplicationId_Name.
            _repeatKeyWithDistinctName = await apiClientRepository.InsertApiClient(
                new ApiClientInsertCommand
                {
                    ApplicationId = applicationId,
                    Name = "Distinct Name For The Spelled Key",
                    IsApproved = true,
                    DataStoreIds = [],
                },
                new ApiClientCommand { ClientId = NameConstraint, ClientUuid = Guid.NewGuid() }
            );
        }

        [Test]
        public void It_starts_from_a_german_session() => _configuredSessionLanguage.Should().Be("Deutsch");

        [Test]
        public void It_opens_us_english_sessions_through_the_datastore_registration() =>
            _datastoreSessionLanguage.Should().Be("us_english");

        [Test]
        public void It_opens_us_english_sessions_through_the_openiddict_registration() =>
            _openIddictSessionLanguage.Should().Be("us_english");

        [Test]
        public void It_classifies_the_repeat_as_a_duplicate_key_rather_than_a_duplicate_name() =>
            _repeatKeyWithDistinctName.Should().BeOfType<ApiClientInsertResult.FailureDuplicateClientId>();

        /// <summary>
        /// The database options a production container resolves after the given SQL Server
        /// registration, starting from the given connection string.
        /// </summary>
        private static IOptions<DatabaseOptions> ResolveDatabaseOptions(
            string databaseConnection,
            Action<IServiceCollection> registerSqlServer
        )
        {
            ServiceCollection services = new();
            services.Configure<DatabaseOptions>(options =>
            {
                options.DatabaseConnection = databaseConnection;
                options.EncryptionKey = "IntegrationTestEncryptionKey32Chars";
            });
            registerSqlServer(services);
            using ServiceProvider provider = services.BuildServiceProvider();
            return Options.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value);
        }

        /// <summary>The identity settings the OpenIddict registration reads while it registers.</summary>
        private static IConfiguration IdentityConfiguration() =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["IdentitySettings:Authority"] = "http://localhost",
                        ["IdentitySettings:Audience"] = "account",
                    }
                )
                .Build();

        private static async Task<string> SessionLanguageAsync(string connectionString)
        {
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<string>("SELECT @@LANGUAGE;") ?? string.Empty;
        }
    }
}
