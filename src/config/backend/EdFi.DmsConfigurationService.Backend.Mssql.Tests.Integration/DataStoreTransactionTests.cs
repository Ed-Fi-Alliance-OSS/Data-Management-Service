// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class DataStoreTransactionTests
{
    [TestFixture]
    public class Given_caller_owned_transactions : DatabaseTest
    {
        private const string PlainText = "Server=localhost;Database=transaction-test;Password=secret;";

        private static DataStoreInsertCommand Command(string name = "transaction-test") =>
            new()
            {
                DataStoreType = "Production",
                Name = name,
                Provider = "sqlserver",
                ConnectionString = PlainText,
            };

        private static DataStoreRepository Repository(
            IConnectionStringEncryptionService? encryption = null,
            IAuditContext? audit = null,
            TenantContext? tenant = null,
            IOptions<DatabaseOptions>? options = null
        ) =>
            new(
                options ?? MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreRepository>.Instance,
                encryption ?? new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions),
                A.Fake<IConnectionStringReader>(),
                A.Fake<IDataStoreContextRepository>(),
                A.Fake<IDataStoreDerivativeRepository>(),
                audit ?? new TestAuditContext(),
                new TenantContextProvider { Context = tenant ?? new TenantContext.NotMultitenant() }
            );

        private Task<int> Count(string table, DbTransaction? transaction = null) =>
            Connection!.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM dmscs.{table};",
                transaction: transaction
            );

        [TestCase(true)]
        [TestCase(false)]
        public async Task It_leaves_insert_commit_or_rollback_to_the_caller(bool commit)
        {
            await using var transaction = await Connection!.BeginTransactionAsync();
            int id = await Repository().InsertDataStore(Command(), transaction, CancellationToken.None);
            id.Should().BeGreaterThan(0);
            (await Count("[DataStore]", transaction)).Should().Be(1);

            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }

            (await Count("[DataStore]")).Should().Be(commit ? 1 : 0);
        }

        [Test]
        public async Task It_preserves_encryption_audit_type_provider_and_null_tenant_on_commit()
        {
            await using var transaction = await Connection!.BeginTransactionAsync();
            int id = await Repository().InsertDataStore(Command(), transaction, CancellationToken.None);
            await transaction.CommitAsync();

            var row = await Connection.QuerySingleAsync<(
                string Name,
                string DataStoreType,
                string Provider,
                byte[] ConnectionString,
                string CreatedBy,
                DateTime CreatedAt,
                long? TenantId
            )>(
                """
                SELECT [Name], [DataStoreType], [Provider], [ConnectionString], [CreatedBy], [CreatedAt], [TenantId]
                FROM dmscs.[DataStore] WHERE [Id] = @Id;
                """,
                new { Id = id }
            );
            row.Name.Should().Be(Command().Name);
            row.DataStoreType.Should().Be("Production");
            row.Provider.Should().Be("sqlserver");
            row.CreatedBy.Should().Be("test-user");
            row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            row.TenantId.Should().BeNull();
            new ConnectionStringEncryptionService(MssqlTestConfiguration.DatabaseOptions)
                .Decrypt(row.ConnectionString)
                .Should()
                .Be(PlainText);
            row.ConnectionString.Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(PlainText));
        }

        [Test]
        public async Task It_uses_the_supplied_connection_even_when_repository_connection_options_are_invalid()
        {
            var repository = Repository(
                options: Options.Create(
                    new DatabaseOptions
                    {
                        DatabaseConnection = "invalid connection options",
                        EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
                    }
                )
            );
            await using var transaction = await Connection!.BeginTransactionAsync();
            int id = await repository.InsertDataStore(Command(), transaction, CancellationToken.None);
            (await Count("[DataStore]", transaction)).Should().Be(1);
            await repository.DeleteManagedDataStore(id, transaction, CancellationToken.None);
            (await Count("[DataStore]", transaction)).Should().Be(0);
            await transaction.RollbackAsync();
        }

        [Test]
        public async Task It_rolls_back_prior_work_when_encryption_throws()
        {
            var encryption = A.Fake<IConnectionStringEncryptionService>();
            A.CallTo(() => encryption.Encrypt(A<string?>._))
                .Throws(new InvalidOperationException("encryption failed"));
            Func<Task> work = async () =>
            {
                await using var transaction = await Connection!.BeginTransactionAsync();
                await Repository()
                    .InsertDataStore(Command("prior work"), transaction, CancellationToken.None);
                await Repository(encryption).InsertDataStore(Command(), transaction, CancellationToken.None);
                await transaction.CommitAsync();
            };

            await work.Should().ThrowAsync<InvalidOperationException>().WithMessage("encryption failed");
            (await Count("[DataStore]")).Should().Be(0);
        }

        [Test]
        public async Task It_rolls_back_prior_work_when_audit_throws_after_encryption()
        {
            var encryption = A.Fake<IConnectionStringEncryptionService>();
            A.CallTo(() => encryption.Encrypt(A<string?>._)).Returns(new byte[] { 1, 2, 3 });
            var audit = A.Fake<IAuditContext>();
            A.CallTo(() => audit.GetCurrentUser()).Throws(new InvalidOperationException("audit failed"));
            Func<Task> work = async () =>
            {
                await using var transaction = await Connection!.BeginTransactionAsync();
                await Repository()
                    .InsertDataStore(Command("prior work"), transaction, CancellationToken.None);
                await Repository(encryption, audit)
                    .InsertDataStore(Command(), transaction, CancellationToken.None);
                await transaction.CommitAsync();
            };

            await work.Should().ThrowAsync<InvalidOperationException>().WithMessage("audit failed");
            A.CallTo(() => encryption.Encrypt(PlainText)).MustHaveHappenedOnceExactly();
            (await Count("[DataStore]")).Should().Be(0);
        }

        [Test]
        public async Task It_rolls_back_insert_when_later_work_throws()
        {
            Func<Task> work = async () =>
            {
                await using var transaction = await Connection!.BeginTransactionAsync();
                await Repository().InsertDataStore(Command(), transaction, CancellationToken.None);
                throw new InvalidOperationException("publication failed");
            };

            await work.Should().ThrowAsync<InvalidOperationException>().WithMessage("publication failed");
            (await Count("[DataStore]")).Should().Be(0);
        }

        [Test]
        public async Task It_propagates_duplicate_sql_failure_so_the_outer_unit_cannot_commit_normally()
        {
            Func<Task> work = async () =>
            {
                await using var transaction = await Connection!.BeginTransactionAsync();
                await Repository().InsertDataStore(Command(), transaction, CancellationToken.None);
                await Repository().InsertDataStore(Command(), transaction, CancellationToken.None);
                await transaction.CommitAsync();
            };

            await work.Should().ThrowAsync<DbException>();
            (await Count("[DataStore]")).Should().Be(0);
        }

        [TestCase("commit", false)]
        [TestCase("commit", true)]
        [TestCase("rollback", false)]
        [TestCase("rollback", true)]
        [TestCase("close", false)]
        [TestCase("close", true)]
        [TestCase("wrong-provider", false)]
        [TestCase("wrong-provider", true)]
        [TestCase("null", false)]
        [TestCase("null", true)]
        public async Task It_rejects_invalid_transactions_without_side_effects(string state, bool delete)
        {
            int id = (await Repository().InsertDataStore(Command("existing")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>()
                .Subject.Id;
            await using var transaction = await Connection!.BeginTransactionAsync();
            DbTransaction? supplied = transaction;
            switch (state)
            {
                case "commit":
                    await transaction.CommitAsync();
                    break;
                case "rollback":
                    await transaction.RollbackAsync();
                    break;
                case "close":
                    await Connection.CloseAsync();
                    break;
                case "wrong-provider":
                    supplied = A.Fake<DbTransaction>();
                    break;
                case "null":
                    supplied = null;
                    break;
            }
            var encryption = A.Fake<IConnectionStringEncryptionService>();
            var audit = A.Fake<IAuditContext>();
            var repository = Repository(encryption, audit);
            Func<Task> action = async () =>
            {
                if (delete)
                {
                    await repository.DeleteManagedDataStore(id, supplied!, CancellationToken.None);
                }
                else
                {
                    await repository.InsertDataStore(Command(), supplied!, CancellationToken.None);
                }
            };
            await action
                .Should()
                .ThrowAsync<Exception>()
                .Where(e => e is ArgumentException || e is InvalidOperationException);
            A.CallTo(() => encryption.Encrypt(A<string?>._)).MustNotHaveHappened();
            A.CallTo(() => audit.GetCurrentUser()).MustNotHaveHappened();
            if (state is "wrong-provider" or "null")
            {
                await transaction.RollbackAsync();
            }
            if (state == "close")
            {
                await Connection.OpenAsync();
            }
            (await Count("[DataStore]")).Should().Be(1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_rejects_disposed_transactions_without_side_effects(bool delete)
        {
            int id = (await Repository().InsertDataStore(Command("existing")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>()
                .Subject.Id;
            DbTransaction disposed;
            await using (var transaction = await Connection!.BeginTransactionAsync())
            {
                disposed = transaction;
            }
            var encryption = A.Fake<IConnectionStringEncryptionService>();
            var audit = A.Fake<IAuditContext>();
            var repository = Repository(encryption, audit);
            Func<Task> action = async () =>
            {
                if (delete)
                {
                    await repository.DeleteManagedDataStore(id, disposed, CancellationToken.None);
                }
                else
                {
                    await repository.InsertDataStore(Command(), disposed, CancellationToken.None);
                }
            };
            await action.Should().ThrowAsync<InvalidOperationException>();
            A.CallTo(() => encryption.Encrypt(A<string?>._)).MustNotHaveHappened();
            A.CallTo(() => audit.GetCurrentUser()).MustNotHaveHappened();
            (await Count("[DataStore]")).Should().Be(1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_propagates_cancellation_without_side_effects(bool delete)
        {
            int id = (await Repository().InsertDataStore(Command("existing")))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>()
                .Subject.Id;
            await using var transaction = await Connection!.BeginTransactionAsync();
            var encryption = A.Fake<IConnectionStringEncryptionService>();
            var audit = A.Fake<IAuditContext>();
            var repository = Repository(encryption, audit);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            Func<Task> action = async () =>
            {
                if (delete)
                {
                    await repository.DeleteManagedDataStore(id, transaction, cancellation.Token);
                }
                else
                {
                    await repository.InsertDataStore(Command(), transaction, cancellation.Token);
                }
            };
            await action.Should().ThrowAsync<OperationCanceledException>();
            A.CallTo(() => encryption.Encrypt(A<string?>._)).MustNotHaveHappened();
            A.CallTo(() => audit.GetCurrentUser()).MustNotHaveHappened();
            (await Count("[DataStore]", transaction)).Should().Be(1);
        }

        [Test]
        public async Task It_scopes_insert_and_managed_removal_to_the_selected_tenant()
        {
            await using var transaction = await Connection!.BeginTransactionAsync();
            long tenantId = await Connection.ExecuteScalarAsync<long>(
                """INSERT INTO dmscs.[Tenant] ([Name]) OUTPUT INSERTED.[Id] VALUES ('transaction-tenant');""",
                transaction: transaction
            );
            var tenant = new TenantContext.Multitenant(tenantId, "transaction-tenant");
            var repository = Repository(tenant: tenant);
            int id = await repository.InsertDataStore(Command(), transaction, CancellationToken.None);
            long storedTenant = await Connection.ExecuteScalarAsync<long>(
                """SELECT [TenantId] FROM dmscs.[DataStore] WHERE [Id] = @Id;""",
                new { Id = id },
                transaction
            );
            storedTenant.Should().Be(tenantId);

            Func<Task> wrongTenant = async () =>
                await Repository(tenant: new TenantContext.Multitenant(tenantId + 1, "other"))
                    .DeleteManagedDataStore(id, transaction, CancellationToken.None);
            Func<Task> noTenant = async () =>
                await Repository().DeleteManagedDataStore(id, transaction, CancellationToken.None);
            await wrongTenant.Should().ThrowAsync<InvalidOperationException>();
            await noTenant.Should().ThrowAsync<InvalidOperationException>();
            (await Count("[DataStore]", transaction)).Should().Be(1);
            await repository.DeleteManagedDataStore(id, transaction, CancellationToken.None);
            (await Count("[DataStore]", transaction)).Should().Be(0);
            await transaction.RollbackAsync();
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task It_commits_or_rolls_back_removal_and_dependent_cascades_together(bool commit)
        {
            var repository = Repository();
            int id = (await repository.InsertDataStore(Command()))
                .Should()
                .BeOfType<DataStoreInsertResult.Success>()
                .Subject.Id;
            await Connection!.ExecuteAsync(
                """
                INSERT INTO dmscs.[DataStoreContext] ([DataStoreId], [ContextKey], [ContextValue]) VALUES (@Id, 'year', '2026');
                INSERT INTO dmscs.[DataStoreDerivative] ([DataStoreId], [DerivativeType]) VALUES (@Id, 'ReadReplica');
                """,
                new { Id = id }
            );
            await using var transaction = await Connection!.BeginTransactionAsync();
            await repository.DeleteManagedDataStore(id, transaction, CancellationToken.None);
            (await Count("[DataStore]", transaction)).Should().Be(0);
            (await Count("[DataStoreContext]", transaction)).Should().Be(0);
            (await Count("[DataStoreDerivative]", transaction)).Should().Be(0);
            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
            (await Count("[DataStore]")).Should().Be(commit ? 0 : 1);
            (await Count("[DataStoreContext]")).Should().Be(commit ? 0 : 1);
            (await Count("[DataStoreDerivative]")).Should().Be(commit ? 0 : 1);
        }

        [Test]
        public async Task It_propagates_missing_removal_and_rolls_back_prior_work()
        {
            Func<Task> work = async () =>
            {
                await using var transaction = await Connection!.BeginTransactionAsync();
                await Repository().InsertDataStore(Command(), transaction, CancellationToken.None);
                await Repository().DeleteManagedDataStore(int.MaxValue, transaction, CancellationToken.None);
                await transaction.CommitAsync();
            };
            await work.Should().ThrowAsync<InvalidOperationException>();
            (await Count("[DataStore]")).Should().Be(0);
        }
    }
}
