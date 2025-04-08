// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.AuthorizationValidation;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

[TestFixture]
public class ResourceAuthorizationHandlerTests
{
    private readonly IAuthorizationServiceFactory _authorizationServiceFactory =
        A.Fake<IAuthorizationServiceFactory>();

    public class Given_An_EdFi_Doc_With_Matching_ClientAuthorization_Namespace
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public async Task Setup()
        {
            var documentSecurityElements = new DocumentSecurityElements(["uri://ed-fi.org"], [], []);

            var evaluator = new AuthorizationStrategyEvaluator(
                "NamespaceBased",
                [new AuthorizationFilter("Namespace", "uri://ed-fi.org")],
                FilterOperator.Or
            );

            var validator = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        validator.ValidateAuthorization(
                            documentSecurityElements,
                            evaluator.Filters,
                            OperationType.Get,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(new ResourceAuthorizationResult.Authorized());

            A.CallTo(() => _authorizationServiceFactory.GetByName<IAuthorizationValidator>("NamespaceBased"))
                .Returns(validator);

            var handler = new ResourceAuthorizationHandler(
                [evaluator],
                _authorizationServiceFactory,
                NullLogger.Instance
            );
            _resourceAuthorizationResult = await handler.Authorize(
                documentSecurityElements,
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    public class Given_An_EdFi_Doc_With_No_Matching_ClientAuthorization_Namespace
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public async Task Setup()
        {
            var documentSecurityElements = new DocumentSecurityElements(["uri://not-matching.org"], [], []);

            var evaluator = new AuthorizationStrategyEvaluator(
                "NamespaceBased",
                [new AuthorizationFilter("Namespace", "uri://ed-fi.org")],
                FilterOperator.Or
            );

            var validator = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        validator.ValidateAuthorization(
                            documentSecurityElements,
                            evaluator.Filters,
                            OperationType.Upsert,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(new ResourceAuthorizationResult.NotAuthorized(["Not authorized"]));

            A.CallTo(() => _authorizationServiceFactory.GetByName<IAuthorizationValidator>("NamespaceBased"))
                .Returns(validator);

            var handler = new ResourceAuthorizationHandler(
                [evaluator],
                _authorizationServiceFactory,
                NullLogger.Instance
            );

            _resourceAuthorizationResult = await handler.Authorize(
                documentSecurityElements,
                OperationType.Upsert,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
            if (_resourceAuthorizationResult is ResourceAuthorizationResult.NotAuthorized notAuthorized)
            {
                notAuthorized!.ErrorMessages.Should().HaveCount(1);
                notAuthorized!.ErrorMessages[0].Should().Be("Not authorized");
            }
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_No_Authorization_Evaluators() : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public async Task Setup()
        {
            var documentSecurityElements = new DocumentSecurityElements(["uri://not-matching.org"], [], []);

            var handler = new ResourceAuthorizationHandler(
                [],
                _authorizationServiceFactory,
                NullLogger.Instance
            );
            _resourceAuthorizationResult = await handler.Authorize(
                documentSecurityElements,
                OperationType.Get,
                new TraceId("trace")
            );
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_Matching_ClientAuthorization_EdOrg : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public async Task Setup()
        {
            var documentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(6001)
                    ),
                ],
                []
            );

            var authStrategyEvaluators = new AuthorizationStrategyEvaluator(
                "RelationshipsWithEdOrgsOnly",
                [new AuthorizationFilter("EducationOrganization", "6001")],
                FilterOperator.Or
            );

            var validator = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        validator.ValidateAuthorization(
                            documentSecurityElements,
                            authStrategyEvaluators.Filters,
                            OperationType.Get,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(new ResourceAuthorizationResult.Authorized());
            A.CallTo(
                    () =>
                        _authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                            "RelationshipsWithEdOrgsOnly"
                        )
                )
                .Returns(validator);

            var handler = new ResourceAuthorizationHandler(
                [authStrategyEvaluators],
                _authorizationServiceFactory,
                NullLogger.Instance
            );
            _resourceAuthorizationResult = await handler.Authorize(
                documentSecurityElements,
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Result_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }

    [TestFixture]
    public class Given_An_EdFi_Doc_With_No_Matching_ClientAuthorization_EdOrg
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public async Task Setup()
        {
            var documentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(9999)
                    ),
                ],
                []
            );
            var authStrategyEvaluators = new AuthorizationStrategyEvaluator(
                "RelationshipsWithEdOrgsOnly",
                [new AuthorizationFilter("EducationOrganization", "6001")],
                FilterOperator.Or
            );

            var validator = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        validator.ValidateAuthorization(
                            documentSecurityElements,
                            authStrategyEvaluators.Filters,
                            OperationType.Get,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(new ResourceAuthorizationResult.NotAuthorized(["Not authorized"]));
            A.CallTo(
                    () =>
                        _authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                            "RelationshipsWithEdOrgsOnly"
                        )
                )
                .Returns(validator);

            var handler = new ResourceAuthorizationHandler(
                [authStrategyEvaluators],
                _authorizationServiceFactory,
                NullLogger.Instance
            );
            _resourceAuthorizationResult = await handler.Authorize(
                documentSecurityElements,
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void Result_should_be_not_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.NotAuthorized>();
            if (_resourceAuthorizationResult is ResourceAuthorizationResult.NotAuthorized notAuthorized)
            {
                notAuthorized!.ErrorMessages.Should().HaveCount(1);
                notAuthorized!.ErrorMessages[0].Should().Be("Not authorized");
            }
        }
    }

    [TestFixture()]
    public class Given_And_Results_With_Both_Authorized_Should_Return_Authorized()
        : ResourceAuthorizationHandlerTests
    {
        private ResourceAuthorizationResult? _resourceAuthorizationResult;

        [SetUp]
        public async Task Setup()
        {
            var documentSecurityElements = new DocumentSecurityElements(
                [],
                [
                    new EducationOrganizationSecurityElement(
                        new ResourceName("School"),
                        new EducationOrganizationId(9999)
                    ),
                ],
                [new StudentId("9879898")]
            );

            var authStrategyEvaluators = new[]
            {
                new AuthorizationStrategyEvaluator(
                    "RelationshipsWithEdOrgsOnly",
                    [new AuthorizationFilter("EducationOrganization", "9999")],
                    FilterOperator.And
                ),
                new AuthorizationStrategyEvaluator(
                    "RelationshipsWithPeopleOnly",
                    [new AuthorizationFilter("Student", "9879898")],
                    FilterOperator.And
                ),
            };

            var validatorForEdOrg = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        validatorForEdOrg.ValidateAuthorization(
                            documentSecurityElements,
                            authStrategyEvaluators[0].Filters,
                            OperationType.Get,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(new ResourceAuthorizationResult.Authorized());
            A.CallTo(
                    () =>
                        _authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                            "RelationshipsWithEdOrgsOnly"
                        )
                )
                .Returns(validatorForEdOrg);

            var validatorForStudent = A.Fake<IAuthorizationValidator>();
            A.CallTo(
                    () =>
                        validatorForStudent.ValidateAuthorization(
                            documentSecurityElements,
                            authStrategyEvaluators[1].Filters,
                            OperationType.Get,
                            A<TraceId>.Ignored
                        )
                )
                .Returns(new ResourceAuthorizationResult.Authorized());
            A.CallTo(
                    () =>
                        _authorizationServiceFactory.GetByName<IAuthorizationValidator>(
                            "RelationshipsWithPeopleOnly"
                        )
                )
                .Returns(validatorForStudent);

            var handler = new ResourceAuthorizationHandler(
                authStrategyEvaluators,
                _authorizationServiceFactory,
                NullLogger.Instance
            );
            _resourceAuthorizationResult = await handler.Authorize(
                documentSecurityElements,
                OperationType.Get,
                new TraceId("trace-id")
            );
        }

        [Test]
        public void When_both_match_should_be_authorized()
        {
            _resourceAuthorizationResult.Should().BeOfType<ResourceAuthorizationResult.Authorized>();
        }
    }
}
