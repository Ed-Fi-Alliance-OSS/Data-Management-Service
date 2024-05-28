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
    [NUnit.Framework.DescriptionAttribute("Resources \"Update\" Operation validations")]
    public partial class ResourcesUpdateOperationValidationsFeature
    {
        
        private Reqnroll.ITestRunner testRunner;
        
        private static string[] featureTags = ((string[])(null));
        
#line 1 "UpdateResourcesValidation.feature"
#line hidden
        
        [NUnit.Framework.OneTimeSetUpAttribute()]
        public virtual async System.Threading.Tasks.Task FeatureSetupAsync()
        {
            testRunner = Reqnroll.TestRunnerManager.GetTestRunnerForAssembly(null, NUnit.Framework.TestContext.CurrentContext.WorkerId);
            Reqnroll.FeatureInfo featureInfo = new Reqnroll.FeatureInfo(new System.Globalization.CultureInfo("en-US"), "Features/Resources", "Resources \"Update\" Operation validations", null, ProgrammingLanguage.CSharp, featureTags);
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
#line 7
              await testRunner.AndAsync("request made to \"data/ed-fi/absenceEventCategoryDescriptors\" with", @"  {
      ""codeValue"": ""Sick Leave"",
      ""description"": ""Sick Leave"",
      ""effectiveBeginDate"": ""2024-05-14"",
      ""effectiveEndDate"": ""2024-05-14"",
      ""namespace"": ""uri://ed-fi.org/AbsenceEventCategoryDescriptor"",
      ""shortDescription"": ""Sick Leave""
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
#line 18
             await testRunner.ThenAsync("it should respond with 201", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify that existing resources can be updated successfully")]
        public async System.Threading.Tasks.Task VerifyThatExistingResourcesCanBeUpdatedSuccessfully()
        {
            string[] tagsOfScenario = ((string[])(null));
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify that existing resources can be updated successfully", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 20
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
#line 22
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"id\": \"{id}\",\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": " +
                        "\"Sick Leave Edited\",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDe" +
                        "scriptor\",\r\n      \"shortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 32
             await testRunner.ThenAsync("it should respond with 204", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
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
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify updating a resource with valid data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 34
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
#line 36
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"id\": \"{id}\",\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": " +
                        "\"Sick Leave Edited\",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDe" +
                        "scriptor\",\r\n      \"shortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 46
             await testRunner.ThenAsync("it should respond with 204", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 47
             await testRunner.WhenAsync("a GET request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\"", ((string)(null)), ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 48
             await testRunner.ThenAsync("it should respond with 200", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 49
              await testRunner.AndAsync("the response body is", "  {\r\n      \"id\": \"{id}\",\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": " +
                        "\"Sick Leave Edited\",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDe" +
                        "scriptor\",\r\n      \"shortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify updating a non existing resource with valid data")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyUpdatingANonExistingResourceWithValidData()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify updating a non existing resource with valid data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 60
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
#line 62
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"id\": {id},\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": \"S" +
                        "ick Leave Edited\",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDesc" +
                        "riptor\",\r\n      \"shortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 72
             await testRunner.ThenAsync("it should respond with 404", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 73
              await testRunner.AndAsync("the response body is", "  {\r\n      \"detail\": \"Resource to update was not found.\",\r\n      \"type\": \"urn:ed-" +
                        "fi:api:not-found\",\r\n      \"title\": \"Not Found\",\r\n      \"status\": 404,\r\n      \"co" +
                        "rrelationId\": null\r\n  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling updating a resource with invalid data")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingUpdatingAResourceWithInvalidData()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling updating a resource with invalid data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 84
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
#line 86
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": \"Sick Leave Edited\",\r" +
                        "\n      \"namespace\": \"AbsenceEventCategoryDescriptor\",\r\n      \"shortDescription\":" +
                        " \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 95
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 96
              await testRunner.AndAsync("the response body is", @"  {
      ""detail"": ""Identifying values for the AbsenceEventCategoryDescriptor resource cannot be changed. Delete and recreate the resource item instead."",
      ""type"": ""urn:ed-fi:api:bad-request:data"",
      ""title"": ""Data Validation Failed"",
      ""status"": 400,
      ""correlationId"": null
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify that response contains the updated resource ID and data")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyThatResponseContainsTheUpdatedResourceIDAndData()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify that response contains the updated resource ID and data", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 107
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
#line 109
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": \"Sick Leave Edited\",\r" +
                        "\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescriptor\",\r\n      \"sh" +
                        "ortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 118
             await testRunner.ThenAsync("it should respond with 204", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 119
              await testRunner.AndAsync("the response headers includes", "  {\r\n      \"location\": \"data/ed-fi/absenceEventCategoryDescriptors/{id}\",\r\n  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
        
        [NUnit.Framework.TestAttribute()]
        [NUnit.Framework.DescriptionAttribute("Verify error handling when updating a resource with empty body")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWhenUpdatingAResourceWithEmptyBody()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling when updating a resource with empty body", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 127
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
#line 129
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 134
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 135
              await testRunner.AndAsync("the response message body is", @"  {
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
        [NUnit.Framework.DescriptionAttribute("Verify error handling when resource ID is different in body on PUT")]
        [NUnit.Framework.IgnoreAttribute("Ignored scenario")]
        public async System.Threading.Tasks.Task VerifyErrorHandlingWhenResourceIDIsDifferentInBodyOnPUT()
        {
            string[] tagsOfScenario = new string[] {
                    "ignore"};
            System.Collections.Specialized.OrderedDictionary argumentsOfScenario = new System.Collections.Specialized.OrderedDictionary();
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling when resource ID is different in body on PUT", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 157
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
             await testRunner.WhenAsync("a PUT request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"id\": <id_different_from_original_resource>,\r\n      \"codeValue\": \"Sick" +
                        " Leave\",\r\n      \"description\": \"Sick Leave Edited\",\r\n      \"namespace\": \"uri://e" +
                        "d-fi.org/AbsenceEventCategoryDescriptor\",\r\n      \"shortDescription\": \"Sick Leave" +
                        "\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 169
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 170
              await testRunner.AndAsync("the response message body is", @"  {
      ""detail"": ""Data validation failed. See 'validationErrors' for details."",
      ""type"": ""urn:ed-fi:api:bad-request:data"",
      ""title"": ""Data Validation Failed"",
      ""status"": 400,
      ""correlationId"": null,
      ""validationErrors"": {
          ""$.id"": [
          ""Input string '<id_different_from_original_resource>' is not a valid number. Path 'id', line 2, position 62.""
          ]
      }
  }", ((Reqnroll.Table)(null)), "And ");
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
            Reqnroll.ScenarioInfo scenarioInfo = new Reqnroll.ScenarioInfo("Verify error handling when resource ID is not included in body on PUT", null, tagsOfScenario, argumentsOfScenario, featureTags);
#line 187
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
#line 189
             await testRunner.WhenAsync("a POST request is made to \"data/ed-fi/absenceEventCategoryDescriptors/{id}\" with", "  {\r\n      \"id\": \"\",\r\n      \"codeValue\": \"Sick Leave\",\r\n      \"description\": \"Sic" +
                        "k Leave Edited\",\r\n      \"namespace\": \"uri://ed-fi.org/AbsenceEventCategoryDescri" +
                        "ptor\",\r\n      \"shortDescription\": \"Sick Leave\"\r\n  }", ((Reqnroll.Table)(null)), "When ");
#line hidden
#line 199
             await testRunner.ThenAsync("it should respond with 400", ((string)(null)), ((Reqnroll.Table)(null)), "Then ");
#line hidden
#line 200
              await testRunner.AndAsync("the response message body is", @"  {
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
  }", ((Reqnroll.Table)(null)), "And ");
#line hidden
            }
            await this.ScenarioCleanupAsync();
        }
    }
}
#pragma warning restore
#endregion
