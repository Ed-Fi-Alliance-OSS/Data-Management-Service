// ------------------------------------------------------------------------------
//  <auto-generated>
//      This code was generated by Reqnroll (https://www.reqnroll.net/).
//      Reqnroll Version:2.0.0.0
//      Reqnroll Generator Version:2.0.0.0
// 
//      Changes to this file may cause incorrect behavior and will be lost if
//      the code is regenerated.
//  </auto-generated>
// ------------------------------------------------------------------------------
#region Designer generated code
#pragma warning disable
namespace EdFi.DataManagementService.Tests.E2E.Features
{
    using Reqnroll;
    using System;
    using System.Linq;
    
    
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Reqnroll", "2.0.0.0")]
    [System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    [NUnit.Framework.TestFixtureAttribute()]
    [NUnit.Framework.DescriptionAttribute("Equality Constraint Validation")]
    public partial class EqualityConstraintValidationFeature
    {
        
        private global::Reqnroll.ITestRunner testRunner;
        
        private static string[] featureTags = ((string[])(null));
        
#line 1 "EqualityConstraintValidation.feature"
#line hidden
        
        [NUnit.Framework.OneTimeSetUpAttribute()]
        public virtual async System.Threading.Tasks.Task FeatureSetupAsync()
        {
            testRunner = global::Reqnroll.TestRunnerManager.GetTestRunnerForAssembly(null, NUnit.Framework.TestContext.CurrentContext.WorkerId);
            global::Reqnroll.FeatureInfo featureInfo = new global::Reqnroll.FeatureInfo(new System.Globalization.CultureInfo("en-US"), "Features", "Equality Constraint Validation", @"              Equality constraints on the resource describe values that must be equal when posting a resource. An example of an equalityConstraint on bellSchedule:
    ""equalityConstraints"": [
        {
            ""sourceJsonPath"": ""$.classPeriods[*].classPeriodReference.schoolId"",
            ""targetJsonPath"": ""$.schoolReference.schoolId""
        }
    ]", global::Reqnroll.ProgrammingLanguage.CSharp, featureTags);
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
        
        public void ScenarioInitialize(global::Reqnroll.ScenarioInfo scenarioInfo)
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
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Post a valid bell schedule no equality constraint violations.", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 10
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 11
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/bellschedules\" with", @"{
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
    }", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 37
             await testRunner.ThenAsync("it should respond with 201", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
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
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Post an invalid bell schedule with equality constraint violations.", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 39
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 40
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/bellschedules\" with", @"{
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
    }", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 66
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 67
              await testRunner.AndAsync("the response body is", @"{""detail"":""The request could not be processed. See 'errors' for details."",""type"":""urn:ed-fi:api:bad-request"",""title"":""Bad Request"",""status"":400,""correlationId"":null,""validationErrors"":null,""errors"":[""Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.schoolReference.schoolId must have the same values""]}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Making a Post request when value does not match the same value in an array")]
        public async System.Threading.Tasks.Task MakingAPostRequestWhenValueDoesNotMatchTheSameValueInAnArray()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Making a Post request when value does not match the same value in an array", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 72
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 73
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/sections\" with", @"{
    ""sectionIdentifier"": ""25590100102Trad220ALG112011Test"",
    ""courseOfferingReference"": {
        ""localCourseCode"": ""ALG-1"",
        ""schoolId"": 255901001,
        ""schoolYear"": 2022,
        ""sessionName"": ""2021-2022 Fall Semester""
    },
    ""classPeriods"": [
        {
            ""classPeriodReference"": {
                ""classPeriodName"": ""02 - Traditional"",
                ""schoolId"": 255901107
            }
        }
    ]
}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 93
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 94
              await testRunner.AndAsync("the response body is", @"{
  ""detail"": ""The request could not be processed. See 'errors' for details."",
  ""type"": ""urn:ed-fi:api:bad-request"",
  ""title"": ""Bad Request"",
  ""status"": 400,
  ""correlationId"": null,
  ""validationErrors"": null,
  ""errors"": [
      ""Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.courseOfferingReference.schoolId must have the same values""
  ]
}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Making a Post request when a value matches the first scenario in an array but not" +
            " the second")]
        public async System.Threading.Tasks.Task MakingAPostRequestWhenAValueMatchesTheFirstScenarioInAnArrayButNotTheSecond()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Making a Post request when a value matches the first scenario in an array but not" +
                    " the second", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 109
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 110
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/sections\" with", @"{
   ""sectionIdentifier"": ""25590100102Trad220ALG112011Test"",
   ""courseOfferingReference"": {
       ""localCourseCode"": ""ALG-1"",
       ""schoolId"": 255901001,
       ""schoolYear"": 2022,
       ""sessionName"": ""2021-2022 Fall Semester""
   },
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
   ]
}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 136
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 137
              await testRunner.AndAsync("the response body is", @"{
  ""detail"": ""The request could not be processed. See 'errors' for details."",
  ""type"": ""urn:ed-fi:api:bad-request"",
  ""title"": ""Bad Request"",
  ""status"": 400,
  ""correlationId"": null,
  ""validationErrors"": null,
  ""errors"": [
      ""Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.courseOfferingReference.schoolId must have the same values""
  ]
}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Making a Post request when value does not match the same value in a single other " +
            "reference")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task MakingAPostRequestWhenValueDoesNotMatchTheSameValueInASingleOtherReference()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Making a Post request when value does not match the same value in a single other " +
                    "reference", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 153
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 154
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/sections\" with", @"{
   ""sectionIdentifier"": ""25590100102Trad220ALG112011Test"",
   ""courseOfferingReference"": {
       ""localCourseCode"": ""ALG-1"",
       ""schoolId"": 255901001,
       ""schoolYear"": 2022,
       ""sessionName"": ""2021-2022 Fall Semester""
   },
   ""locationReference"": {
       ""classroomIdentificationCode"": ""106"",
       ""schoolId"": 1
   }
}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 170
             await testRunner.ThenAsync("it should respond with 409", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 171
              await testRunner.AndAsync("the response body is", "{\r\n    \"detail\": \"The referenced \'Location\' resource does not exist.\",\r\n    \"type" +
                        "\": \"urn:ed-fi:api:conflict:invalid-reference\",\r\n    \"title\": \"Resource Not Uniqu" +
                        "e Conflict due to invalid-reference\",\r\n    \"status\": 409,\r\n    \"correlationId\": " +
                        "null\r\n}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
    }
}
#pragma warning restore
#endregion