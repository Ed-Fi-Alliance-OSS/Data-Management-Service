﻿// ------------------------------------------------------------------------------
//  <auto-generated>
//      This code was generated by Reqnroll (https://www.reqnroll.net/).
//      Reqnroll Version:1.0.0.0
//      Reqnroll Generator Version:1.0.0.0
// 
//      Changes to this file may cause incorrect behavior and will be lost if
//      the code is regenerated.
//  </auto-generated>
// ------------------------------------------------------------------------------
#region Designer generated code
#pragma warning disable
namespace EdFi.DataManagementService.Api.Tests.E2E.Features
{
    using Reqnroll;
    using System;
    using System.Linq;
    
    
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Reqnroll", "1.0.0.0")]
    [System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    [NUnit.Framework.TestFixtureAttribute()]
    [NUnit.Framework.DescriptionAttribute("Equality Constraint Validation")]
    public partial class EqualityConstraintValidationFeature
    {
        
        private Reqnroll.ITestRunner testRunner;
        
        private static string[] featureTags = ((string[])(null));
        
#line 1 "EqualityConstraintValidation.feature"
#line hidden
        
        [NUnit.Framework.OneTimeSetUpAttribute()]
        public virtual async System.Threading.Tasks.Task FeatureSetupAsync()
        {
            testRunner = Reqnroll.TestRunnerManager.GetTestRunnerForAssembly(null, NUnit.Framework.TestContext.CurrentContext.WorkerId);
            Reqnroll.FeatureInfo featureInfo = new Reqnroll.FeatureInfo(new System.Globalization.CultureInfo("en-US"), "Features", "Equality Constraint Validation", @"    Equality constraints on the resource describe values that must be equal when posting a resource. An example of an equalityConstraint on bellSchedule:
    ""equalityConstraints"": [
        {
            ""sourceJsonPath"": ""$.classPeriods[*].classPeriodReference.schoolId"",
            ""targetJsonPath"": ""$.schoolReference.schoolId""
        }
    ]", ProgrammingLanguage.CSharp, featureTags);
            await testRunner.OnFeatureStartAsync(featureInfo);
        }
        
        [NUnit.Framework.OneTimeTearDownAttribute()]
        public virtual async System.Threading.Tasks.Task FeatureTearDownAsync()
        {
            await testRunner.OnFeatureEndAsync();
            testRunner = null;
        }
        
        [NUnit.Framework.SetUpAttribute()]
        public async System.Threading.Tasks.Task TestInitializeAsync()
        {
        }
        
        [NUnit.Framework.TearDownAttribute()]
        public async System.Threading.Tasks.Task TestTearDownAsync()
        {
            await testRunner.OnScenarioEndAsync();
        }
        
        public void ScenarioInitialize(Reqnroll.ScenarioInfo scenarioInfo)
        {
            testRunner.OnScenarioInitialize(scenarioInfo);
            testRunner.ScenarioContext.ScenarioContainer.RegisterInstanceAs<NUnit.Framework.TestContext>(NUnit.Framework.TestContext.CurrentContext);
        }
        
        public async System.Threading.Tasks.Task ScenarioStartAsync()
        {
            await testRunner.OnScenarioStartAsync();
        }
        
        public async System.Threading.Tasks.Task ScenarioCleanupAsync()
        {
            await testRunner.CollectScenarioErrorsAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Post a valid bell schedule no equality constraint violations.")]
        public async System.Threading.Tasks.Task PostAValidBellScheduleNoEqualityConstraintViolations_()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Post a valid bell schedule no equality constraint violations.", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 10
this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 11
    await testRunner.WhenAsync("sending a POST request to \"data/ed-fi/bellschedules\" with body", @"{
    ""schoolReference"": {
        ""schoolId"": 255901001
    },
    ""bellScheduleName"": ""Test Schedule"",
    ""totalInstructionalTime"": 325,
    ""classPeriods"": [
        {
        ""classPeriodReference"": {
            ""classPeriodName"": ""01 - Traditional"",
            ""schoolId"": 255901001
        }
        },
        {
        ""classPeriodReference"": {
            ""classPeriodName"": ""02 - Traditional"",
            ""schoolId"": 255901001
        }
        }
    ],
    ""dates"": [],
    ""gradeLevels"": []
    }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 37
    await testRunner.ThenAsync("the response code is 201", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Post an invalid bell schedule with equality constraint violations.")]
        public async System.Threading.Tasks.Task PostAnInvalidBellScheduleWithEqualityConstraintViolations_()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Post an invalid bell schedule with equality constraint violations.", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 39
this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 40
    await testRunner.WhenAsync("sending a POST request to \"data/ed-fi/bellschedules\" with body", @"{
    ""schoolReference"": {
        ""schoolId"": 255901001
    },
    ""bellScheduleName"": ""Test Schedule"",
    ""totalInstructionalTime"": 325,
    ""classPeriods"": [
        {
        ""classPeriodReference"": {
            ""classPeriodName"": ""01 - Traditional"",
            ""schoolId"": 1
        }
        },
        {
        ""classPeriodReference"": {
            ""classPeriodName"": ""02 - Traditional"",
            ""schoolId"": 1
        }
        }
    ],
    ""dates"": [],
    ""gradeLevels"": []
    }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 66
    await testRunner.ThenAsync("the response code is 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 67
        await testRunner.AndAsync("the response body is", @"{""detail"":""The request could not be processed. See 'errors' for details."",""type"":""urn:ed-fi:api:bad-request"",""title"":""Bad Request"",""status"":400,""correlationId"":null,""validationErrors"":null,""errors"":[""Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.schoolReference.schoolId must have the same values""]}", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
    }
}
#pragma warning restore
#endregion
