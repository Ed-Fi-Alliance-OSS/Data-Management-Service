﻿// ------------------------------------------------------------------------------
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
namespace EdFi.DataManagementService.Tests.E2E.Features.Resources
{
    using Reqnroll;
    using System;
    using System.Linq;
    
    
    [System.CodeDom.Compiler.GeneratedCodeAttribute("Reqnroll", "2.0.0.0")]
    [System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    [NUnit.Framework.TestFixtureAttribute()]
    [NUnit.Framework.DescriptionAttribute("Resources \"Update\" Operation validations")]
    public partial class ResourcesUpdateOperationValidationsFeature
    {
        
        private global::Reqnroll.ITestRunner testRunner;
        
        private static string[] featureTags = ((string[])(null));
        
#line 1 "UpdateResourcesValidation.feature"
#line hidden
        
        [NUnit.Framework.OneTimeSetUpAttribute()]
        public virtual async System.Threading.Tasks.Task FeatureSetupAsync()
        {
            testRunner = global::Reqnroll.TestRunnerManager.GetTestRunnerForAssembly(null, NUnit.Framework.TestContext.CurrentContext.WorkerId);
            global::Reqnroll.FeatureInfo featureInfo = new global::Reqnroll.FeatureInfo(new System.Globalization.CultureInfo("en-US"), "Features/Resources", "Resources \"Update\" Operation validations", null, global::Reqnroll.ProgrammingLanguage.CSharp, featureTags);
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
        
        public virtual async System.Threading.Tasks.Task FeatureBackgroundAsync()
        {
#line 4
        #line hidden
#line 5
            await testRunner.GivenAsync("the Data Management Service must receive a token issued by \"http://localhost\"", ((string)(null)), ((global::Reqnroll.Table)(null)), "Given ");
#line hidden
#line 6
              await testRunner.AndAsync("user is already authorized", ((string)(null)), ((global::Reqnroll.Table)(null)), "And ");
#line hidden
#line 7
              await testRunner.AndAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors\" with", "{\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave\",\r\n  \"effectiveBegi" +
                    "nDate\": \"2024-05-14\",\r\n  \"effectiveEndDate\": \"2024-05-14\",\r\n  \"namespace\": \"uri:" +
                    "//ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n  \"shortDescription\": \"Sick Leave\"" +
                    "\r\n}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
#line 18
             await testRunner.ThenAsync("it should respond with 201 or 200", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify that existing resources can be updated successfully")]
        public async System.Threading.Tasks.Task VerifyThatExistingResourcesCanBeUpdatedSuccessfully()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify that existing resources can be updated successfully", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 20
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 22
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n  \"id\": \"{id}\",\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave Ed" +
                        "ited\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n  \"sho" +
                        "rtDescription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 32
             await testRunner.ThenAsync("it should respond with 204", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify updating a resource with valid data")]
        public async System.Threading.Tasks.Task VerifyUpdatingAResourceWithValidData()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify updating a resource with valid data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 34
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 36
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n  \"id\": \"{id}\",\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave Ed" +
                        "ited\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n  \"sho" +
                        "rtDescription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 46
             await testRunner.ThenAsync("it should respond with 204", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 47
             await testRunner.WhenAsync("a GET request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\"", ((string)(null)), ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 48
             await testRunner.ThenAsync("it should respond with 200", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 49
              await testRunner.AndAsync("the response body is", "{\r\n  \"id\": \"{id}\",\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave Ed" +
                        "ited\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n  \"sho" +
                        "rtDescription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify updating a non existing resource with valid data")]
        public async System.Threading.Tasks.Task VerifyUpdatingANonExistingResourceWithValidData()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify updating a non existing resource with valid data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 60
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 62
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/00000000-0000-400" +
                        "0-a000-000000000000\" with", "{\r\n  \"id\": \"00000000-0000-4000-a000-000000000000\",\r\n  \"codeValue\": \"Sick Leave\",\r" +
                        "\n  \"description\": \"Sick Leave Edited\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceE" +
                        "ventCategoryDescriptor\",\r\n  \"shortDescription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 72
             await testRunner.ThenAsync("it should respond with 404", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 73
              await testRunner.AndAsync("the response body is", "{\r\n    \"detail\": \"Resource to update was not found\",\r\n    \"type\": \"urn:ed-fi:api:" +
                        "not-found\",\r\n    \"title\": \"Not Found\",\r\n    \"status\": 404,\r\n    \"correlationId\":" +
                        " null,\r\n    \"validationErrors\": null,\r\n    \"errors\": null\r\n}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling updating the document identity")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingUpdatingTheDocumentIdentity()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify error handling updating the document identity", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 85
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 87
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n  \"id\": \"{id}\",\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave Ed" +
                        "ited\",\r\n  \"namespace\": \"AbsenceEventCategoryDescriptor\",\r\n  \"shortDescription\": " +
                        "\"Sick Leave Edited\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 97
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 98
              await testRunner.AndAsync("the response body is", @"{
  ""detail"": ""The request could not be processed. See 'errors' for details."",
  ""type"": ""urn:ed-fi:api:bad-request"",
  ""title"": ""Bad Request"",
  ""status"": 400,
  ""correlationId"": null,
  ""validationErrors"": null,
  ""errors"": [
      ""Identifying values for the AbsenceEventCategoryDescriptor resource cannot be changed. Delete and recreate the resource item instead.""
  ]
  }", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify that response contains the updated resource ID and data")]
        public async System.Threading.Tasks.Task VerifyThatResponseContainsTheUpdatedResourceIDAndData()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify that response contains the updated resource ID and data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 113
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 115
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n  \"id\": \"{id}\",\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave Ed" +
                        "ited\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n  \"sho" +
                        "rtDescription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 125
             await testRunner.ThenAsync("it should respond with 204", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 126
              await testRunner.AndAsync("the response headers includes", "{\r\n    \"location\": \"/ed-fi/absenceEventCategoryDescriptors/{id}\"\r\n}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling when updating a resource with empty body")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWhenUpdatingAResourceWithEmptyBody()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify error handling when updating a resource with empty body", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 134
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 136
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 141
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 142
              await testRunner.AndAsync("the response body is", @"{
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
      ],
      ""$.id"": [
      ""id is required.""
      ]
  },
  ""errors"": []
}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling when resource ID is different in body on PUT")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWhenResourceIDIsDifferentInBodyOnPUT()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify error handling when resource ID is different in body on PUT", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 168
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 170
             await testRunner.WhenAsync("a PUT request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n  \"id\": \"00000000-0000-0000-0000-000000000000\",\r\n  \"codeValue\": \"Sick Leave\",\r" +
                        "\n  \"description\": \"Sick Leave Edited\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceE" +
                        "ventCategoryDescriptor\",\r\n  \"shortDescription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 180
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 181
              await testRunner.AndAsync("the response body is", @"{
  ""detail"": ""The request could not be processed. See 'errors' for details."",
  ""type"": ""urn:ed-fi:api:bad-request"",
  ""title"": ""Bad Request"",
  ""status"": 400,
  ""correlationId"": null,
  ""validationErrors"": null,
  ""errors"": [
      ""Request body id must match the id in the url.""
  ]
}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling when resource ID is not included in body on PUT")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWhenResourceIDIsNotIncludedInBodyOnPUT()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            global::Reqnroll.ScenarioInfo scenarioInfo = new global::Reqnroll.ScenarioInfo("Verify error handling when resource ID is not included in body on PUT", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 196
        this.ScenarioInitialize(scenarioInfo);
#line hidden
            if ((global::Reqnroll.TagHelper.ContainsIgnoreTag(scenarioInfo.CombinedTags) || global::Reqnroll.TagHelper.ContainsIgnoreTag(featureTags)))
            {
                testRunner.SkipScenario();
            }
            else
            {
                await this.ScenarioStartAsync();
#line 4
        await this.FeatureBackgroundAsync();
#line hidden
#line 198
             await testRunner.WhenAsync("a POST request is made to \"ed-fi/absenceEventCategoryDescriptors/{id}\" with", "{\r\n  \"id\": \"\",\r\n  \"codeValue\": \"Sick Leave\",\r\n  \"description\": \"Sick Leave Edited" +
                        "\",\r\n  \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n  \"shortDe" +
                        "scription\": \"Sick Leave\"\r\n}", ((global::Reqnroll.Table)(null)), "When ");
#line hidden
#line 208
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((global::Reqnroll.Table)(null)), "Then ");
#line hidden
#line 209
              await testRunner.AndAsync("the response body is", @"{
  ""detail"": ""Data validation failed. See 'validationErrors' for details."",
  ""type"": ""urn:ed-fi:api:bad-request:data"",
  ""title"": ""Data Validation Failed"",
  ""status"": 400,
  ""correlationId"": null,
  ""validationErrors"": {
      ""$.id"": [
      ""Error converting value \\""\\"" to type 'System.Guid'. Path 'id', line 2, position 32.""
      ]
  }
}", ((global::Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
    }
}
#pragma warning restore
#endregion
