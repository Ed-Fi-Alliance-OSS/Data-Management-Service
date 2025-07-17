import http from 'k6/http';
import exec from 'k6/execution';

export const options = {
    vus: 1,
    iterations: 1
};

export default function() {
    console.log('🧪 Testing ABORT_ON_4XX with simple 400 error...');
    console.log(`ABORT_ON_4XX = ${__ENV.ABORT_ON_4XX}`);
    
    // Make a request that will definitely return 400
    const response = http.get('http://localhost:8080/api/data/ed-fi/invalid-endpoint-with-bad-params?bad=true');
    
    console.log(`Response status: ${response.status}`);
    
    // Check if it's a 4xx error
    if (response.status >= 400 && response.status < 500 && __ENV.ABORT_ON_4XX === 'true') {
        console.error(`CRITICAL: ${response.status} error detected. Test will be aborted.`);
        console.error(`Response: ${response.body}`);
        exec.test.abort(`Test aborted due to ${response.status} error`);
    }
    
    console.log('Test completed without abort.');
}

export function teardown() {
    console.log('Teardown executed - this proves teardown runs after abort');
}