// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.DataModel;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class ApplicationModuleTests
{
    private readonly IRepository<Application> _repository = A.Fake<IRepository<Application>>();
    private readonly IRepository<Vendor> _vendorRepository = A.Fake<IRepository<Vendor>>();

    [TestFixture]
    public class SuccessTests : ApplicationModuleTests
    {
        [SetUp]
        public void Setup()
        {
            A.CallTo(() => _vendorRepository.AddAsync(A<Vendor>.Ignored))
                .Returns(new InsertResult.InsertSuccess(1));

            A.CallTo(() => _repository.AddAsync(A<Application>.Ignored))
                .Returns(new InsertResult.InsertSuccess(1));

            A.CallTo(() => _repository.GetAllAsync())
                .Returns(
                    new GetResult<Application>.GetSuccess(
                        [
                            new Application()
                            {
                                Id = 1,
                                ApplicationName = "Test Application",
                                ClaimSetName = "ClaimSet",
                                VendorId = 1,
                                ApplicationEducationOrganizations = [1]
                            }
                        ]
                    )
                );

            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored))
                .Returns(
                    new GetResult<Application>.GetByIdSuccess(
                        new Application()
                        {
                            Id = 1,
                            ApplicationName = "Test Application",
                            ClaimSetName = "ClaimSet",
                            VendorId = 1,
                            ApplicationEducationOrganizations = [1]
                        }
                    )
                );

            A.CallTo(() => _repository.UpdateAsync(A<Application>.Ignored))
                .Returns(new UpdateResult.UpdateSuccess(1));

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored))
                .Returns(new DeleteResult.DeleteSuccess(1));
        }

        [Test]
        public async Task Should_return_success_response()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => _repository!);
                    }
                );
            });
            using var client = factory.CreateClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                      "ApplicationName": "Application 11",
                      "ClaimSetName": "Test",
                      "ApplicationEducationOrganizations": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            var getResponse = await client.GetAsync("/v2/applications");
            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                       "ApplicationName": "Application 11",
                        "ClaimSetName": "Test",
                        "ApplicationEducationOrganizations": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [TestFixture]
    public class FailureNotFound : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored))
                .Returns(new GetResult<Application>.GetByIdFailureNotExists());

            A.CallTo(() => _repository.UpdateAsync(A<Application>.Ignored))
                .Returns(new UpdateResult.UpdateFailureNotExists());

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored))
                .Returns(new DeleteResult.DeleteFailureNotExists());
        }

        [Test]
        public async Task Should_return_proper_not_found_responses()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => _repository!);
                    }
                );
            });
            using var client = factory.CreateClient();

            //Act

            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                       "ApplicationName": "Application 101",
                        "ClaimSetName": "Test",
                        "ApplicationEducationOrganizations": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");

            //Assert
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestFixture]
    public class FailureUnknownTests : ApplicationModuleTests
    {
        [SetUp]
        public void SetUp()
        {
            A.CallTo(() => _repository.AddAsync(A<Application>.Ignored))
                .Returns(new InsertResult.UnknownFailure(""));

            A.CallTo(() => _repository.GetAllAsync()).Returns(new GetResult<Application>.UnknownFailure(""));

            A.CallTo(() => _repository.GetByIdAsync(A<long>.Ignored))
                .Returns(new GetResult<Application>.UnknownFailure(""));

            A.CallTo(() => _repository.UpdateAsync(A<Application>.Ignored))
                .Returns(new UpdateResult.UnknownFailure(""));

            A.CallTo(() => _repository.DeleteAsync(A<long>.Ignored))
                .Returns(new DeleteResult.UnknownFailure(""));
        }

        [Test]
        public async Task Should_return_proper_success_responses()
        {
            // Arrange
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(
                    (collection) =>
                    {
                        collection.AddTransient((x) => _repository!);
                    }
                );
            });
            using var client = factory.CreateClient();

            //Act
            var addResponse = await client.PostAsync(
                "/v2/applications",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "ApplicationEducationOrganizations": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var getResponse = await client.GetAsync("/v2/applications");
            var getByIdResponse = await client.GetAsync("/v2/applications/1");
            var updateResponse = await client.PutAsync(
                "/v2/applications/1",
                new StringContent(
                    """
                    {
                        "ApplicationName": "Application 102",
                        "ClaimSetName": "Test",
                        "ApplicationEducationOrganizations": [1]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"
                )
            );
            var deleteResponse = await client.DeleteAsync("/v2/applications/1");

            //Assert
            addResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            getByIdResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
    }
}
