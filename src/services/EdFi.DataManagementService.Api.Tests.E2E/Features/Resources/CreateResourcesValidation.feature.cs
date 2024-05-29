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
namespace EdFi.DataManagementService.Api.Tests.E2E.Features.Resources
{
    using Reqnroll;
    using System;
    using System.Linq;
    
    
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Reqnroll", "1.0.0.0")]
    [System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    [NUnit.Framework.TestFixtureAttribute()]
    [NUnit.Framework.DescriptionAttribute("Resources \"Create\" Operation validations")]
    public partial class ResourcesCreateOperationValidationsFeature
    {
        
        private Reqnroll.ITestRunner testRunner;
        
        private static string[] featureTags = ((string[])(null));
        
#line 1 "CreateResourcesValidation.feature"
#line hidden
        
        [NUnit.Framework.OneTimeSetUpAttribute()]
        public virtual async System.Threading.Tasks.Task FeatureSetupAsync()
        {
            testRunner = Reqnroll.TestRunnerManager.GetTestRunnerForAssembly(null, NUnit.Framework.TestContext.CurrentContext.WorkerId);
            Reqnroll.FeatureInfo featureInfo = new Reqnroll.FeatureInfo(new System.Globalization.CultureInfo("en-US"), "Features/Resources", "Resources \"Create\" Operation validations", null, ProgrammingLanguage.CSharp, featureTags);
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
        
        public virtual async System.Threading.Tasks.Task FeatureBackgroundAsync()
        {
#line 4
        #line hidden
#line 5
            await testRunner.GivenAsync("the Data Management Service must receive a token issued by \"http://localhost\"", ((string)(null)), ((Reqnroll.Table)(null)), "Given ");
#line hidden
#line 6
              await testRunner.AndAsync("user is already authorized", ((string)(null)), ((Reqnroll.Table)(null)), "And ");
#line hidden
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify new resource can be created successfully")]
        public async System.Threading.Tasks.Task VerifyNewResourceCanBeCreatedSuccessfully()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify new resource can be created successfully", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 8
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 9
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", @"{
    ""codeValue"": ""Sick Leave"",
    ""description"": ""Sick Leave"",
    ""effectiveBeginDate"": ""2024-05-14"",
    ""effectiveEndDate"": ""2024-05-14"",
    ""namespace"": ""uri://ed-fi.org/AbsenceEventCategoryDescriptor"",
    ""shortDescription"": ""Sick Leave""
}", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 20
             await testRunner.ThenAsync("it should respond with 201", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 21
              await testRunner.AndAsync("the response headers includes", "  {\r\n      \"location\": \"ed-fi/absenceEventCategoryDescriptors/{id}\"\r\n  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
#line 27
              await testRunner.AndAsync("the record can be retrieved with a GET request", @"{
    ""codeValue"": ""Sick Leave"",
    ""description"": ""Sick Leave"",
    ""effectiveBeginDate"": ""2024-05-14"",
    ""effectiveEndDate"": ""2024-05-14"",
    ""namespace"": ""uri://ed-fi.org/AbsenceEventCategoryDescriptor"",
    ""shortDescription"": ""Sick Leave""
}", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling with POST using invalid data")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWithPOSTUsingInvalidData()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling with POST using invalid data", null, tagsOfScenario, argumentsOfScenario, featureTags);
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
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 40
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n      \"description\": \"Wrong Value\",\r\n      \"effectiveBeginDate\": \"2024-05-14" +
                        "\",\r\n      \"effectiveEndDate\": \"2024-05-14\",\r\n      \"namespace\": \"uri://ed-fi.org" +
                        "/AbsenceEventCategoryDescriptor\",\r\n      \"shortDescription\": \"Wrong Value\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 50
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 51
              await testRunner.AndAsync("the response body is", @"{
    ""detail"": ""Data validation failed. See 'validationErrors' for details."",
    ""type"": ""urn:ed-fi:api:bad-request:data"",
    ""title"": ""Data Validation Failed"",
    ""status"": 400,
    ""correlationId"": null,
    ""validationErrors"": {
      ""$.codeValue"": [
        ""codeValue is required.""
      ]
    },
    ""errors"": []
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling with POST using invalid data Forbidden")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWithPOSTUsingInvalidDataForbidden()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling with POST using invalid data Forbidden", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 69
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 70
             await testRunner.WhenAsync("a POST request is made to ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n      \"codeValue\": \"xxxx\",\r\n      \"description\": \"Wrong Value\",\r\n      \"name" +
                        "space\": \"uri://.org/wrong\",\r\n      \"shortDescription\": \"Wrong Value\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 79
             await testRunner.ThenAsync("it should respond with 403", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 80
              await testRunner.AndAsync("the response body is", @"  {
      ""detail"": ""Access to the resource could not be authorized. The 'Namespace' value of the resource does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org', 'uri://gbisd.org', 'uri://tpdm.ed-fi.org')."",
      ""type"": ""urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch"",
      ""title"": ""Authorization Denied"",
      ""status"": 403,
      ""correlationId"": null
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling with POST using empty body")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWithPOSTUsingEmptyBody()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling with POST using empty body", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 91
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 92
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 97
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 98
              await testRunner.AndAsync("the response body is", @"  {
    ""detail"": ""Data validation failed. See 'validationErrors' for details."",
    ""type"": ""urn:ed-fi:api:bad-request:data"",
    ""title"": ""Data Validation Failed"",
    ""status"": 400,
    ""correlationId"": null,
    ""validationErrors"": {
      ""$.namespace"": [
        ""namespace is required.""
      ],
      ""$.codeValue"": [
        ""codeValue is required.""
      ],
      ""$.shortDescription"": [
        ""shortDescription is required.""
      ]
    },
    ""errors"": []
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling with POST using blank spaces in the required fields")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWithPOSTUsingBlankSpacesInTheRequiredFields()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling with POST using blank spaces in the required fields", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 122
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 123
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n      \"codeValue\": \"                      \",\r\n      \"description\": \"        " +
                        "            \",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescript" +
                        "or\",\r\n      \"shortDescription\": \"                    \"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 132
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 135
              await testRunner.AndAsync("the response body is", @"  {
      ""detail"": ""Data validation failed. See 'validationErrors' for details."",
      ""type"": ""urn:ed-fi:api:bad-request:data"",
      ""title"": ""Data Validation Failed"",
      ""status"": 400,
      ""correlationId"": null,
      ""validationErrors"": {
          ""$.codeValue"": [
              ""CodeValue is required.""
              ],
          ""$.namespace"": [
              ""Namespace is required.""
              ],
          ""$.shortDescription"": [
              ""ShortDescription is required.""
              ]
      }
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify POST of existing record without changes")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyPOSTOfExistingRecordWithoutChanges()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify POST of existing record without changes", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 158
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 159
            await testRunner.GivenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n      \"codeValue\": \"Sick Lave\",\r\n      \"description\": \"Sick Leave\",\r\n      \"" +
                        "namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n      \"shortDescr" +
                        "iption\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "Given ");
#line hidden
#line 168
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n      \"codeValue\": \"Sick Lave\",\r\n      \"description\": \"Sick Leave\",\r\n      \"" +
                        "namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n      \"shortDescr" +
                        "iption\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 177
             await testRunner.ThenAsync("it should respond with 200", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 178
              await testRunner.AndAsync("the record can be retrieved with a GET request", "      {\r\n          \"codeValue\": \"Sick Lave\",\r\n          \"description\": \"Sick Leav" +
                        "e\",\r\n          \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n " +
                        "         \"shortDescription\": \"Sick Leave\"\r\n      }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify POST of existing record (change non-key field) works")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyPOSTOfExistingRecordChangeNon_KeyFieldWorks()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify POST of existing record (change non-key field) works", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 189
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 190
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "  {\r\n      \"codeValue\": \"Sick Lave\",\r\n      \"description\": \"Sick Leave Edit\",\r\n  " +
                        "    \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n      \"short" +
                        "Description\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 199
             await testRunner.ThenAsync("it should respond with 200", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling when resource ID is included in body on POST")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWhenResourceIDIsIncludedInBodyOnPOST()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling when resource ID is included in body on POST", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 202
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((TagHelper.ContainsIgnoreTag(tagsOfScenario) || TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 204
             await testRunner.WhenAsync("a POST request is made to \"/ed-fi/absenceEventCategoryDescriptors/\" with", "  {\r\n      \"id\": \"{id}\",\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": " +
                        "\"Sick Leave Edited\",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDe" +
                        "scriptor\",\r\n      \"shortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 214
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 215
              await testRunner.AndAsync("the response body is", @"  {
      ""detail"": ""The request data was constructed incorrectly."",
      ""type"": ""urn:ed-fi:api:bad-request:data"",
      ""title"": ""Data Validation Failed"",
      ""status"": 400,
      ""correlationId"": null,
      ""errors"": [
          ""Resource identifiers cannot be assigned by the client. The 'id' property should not be included in the request body.""
      ]
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
    }
}
#pragma warning restore
#endregion
