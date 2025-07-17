import { SharedAuthManager } from './config/sharedAuth.js';
import { ApiDataClient } from './utils/apiDataClient.js';
import exec from 'k6/execution';

// Test configuration
export const options = {
    vus: 1,
    iterations: 1,
    thresholds: {
        // We expect this test to abort, so no thresholds
    }
};

const apiBaseUrl = __ENV.API_BASE_URL || 'http://localhost:8080/api';
const authManager = new SharedAuthManager({
    tokenUrl: __ENV.OAUTH_TOKEN_URL,
    clientId: __ENV.CLIENT_ID,
    clientSecret: __ENV.CLIENT_SECRET
});

export default function() {
    console.log('🧪 Testing ABORT_ON_4XX functionality...');
    console.log(`ABORT_ON_4XX is set to: ${__ENV.ABORT_ON_4XX}`);
    
    const apiClient = new ApiDataClient(apiBaseUrl, authManager);
    
    // Test 1: Valid request (should succeed)
    console.log('\n✅ Test 1: Valid descriptor (should succeed)');
    const validDescriptor = {
        codeValue: `TEST_ABORT_${Date.now()}`,
        shortDescription: 'Test Abort Feature',
        description: 'Testing abort on 4xx functionality',
        namespace: 'uri://ed-fi.org/TestDescriptor'
    };
    
    const validResult = apiClient.post('/ed-fi/gradeLevelDescriptors', validDescriptor, 'gradeLevelDescriptors');
    if (validResult.success) {
        console.log('✅ Valid request succeeded as expected');
    } else {
        console.log('❌ Valid request failed unexpectedly');
    }
    
    // Test 2: Invalid data - missing required field (should trigger 400)
    console.log('\n❌ Test 2: Invalid data - missing required field');
    const invalidDescriptor1 = {
        // Missing codeValue (required)
        shortDescription: 'Invalid Descriptor',
        description: 'This should fail with 400',
        namespace: 'uri://ed-fi.org/TestDescriptor'
    };
    
    console.log('Sending invalid data (missing codeValue)...');
    const invalidResult1 = apiClient.post('/ed-fi/gradeLevelDescriptors', invalidDescriptor1, 'gradeLevelDescriptors');
    
    // Test 3: Invalid data - wrong data type (should trigger 400)
    console.log('\n❌ Test 3: Invalid data - wrong data type');
    const invalidDescriptor2 = {
        codeValue: 12345, // Should be string, not number
        shortDescription: 'Invalid Type Descriptor',
        description: 'This should fail with 400',
        namespace: 'uri://ed-fi.org/TestDescriptor'
    };
    
    console.log('Sending invalid data (wrong type for codeValue)...');
    const invalidResult2 = apiClient.post('/ed-fi/gradeLevelDescriptors', invalidDescriptor2, 'gradeLevelDescriptors');
    
    // Test 4: Invalid endpoint (should trigger 404)
    console.log('\n❌ Test 4: Invalid endpoint (404 error)');
    const invalidResult3 = apiClient.get('/ed-fi/nonexistentResource');
    
    // Test 5: Unauthorized request (should trigger 403 if we had a bad token)
    console.log('\n❌ Test 5: Testing with malformed data structure');
    const malformedData = {
        codeValue: 'TEST',
        shortDescription: 'Test',
        description: 'Test',
        namespace: 'uri://ed-fi.org/TestDescriptor',
        extraField: 'This field should not exist',
        anotherInvalidField: true,
        nestedInvalid: {
            should: 'not',
            be: 'here'
        }
    };
    
    console.log('Sending malformed data with extra fields...');
    const invalidResult4 = apiClient.post('/ed-fi/gradeLevelDescriptors', malformedData, 'gradeLevelDescriptors');
    
    // If we get here and ABORT_ON_4XX is true, something went wrong
    if (__ENV.ABORT_ON_4XX === 'true') {
        console.log('\n⚠️  WARNING: Test should have aborted by now if ABORT_ON_4XX is working!');
        exec.test.abort('Manual abort: ABORT_ON_4XX did not trigger as expected');
    } else {
        console.log('\n✅ All tests completed. ABORT_ON_4XX is disabled, so test continued despite 4xx errors.');
    }
}

export function teardown() {
    console.log('\n🧹 Test abort functionality test completed');
    console.log('Teardown executed successfully (this should run even after abort)');
}