import { check } from 'k6';
import http from 'k6/http';

export const options = {
    vus: 1,
    duration: '10s',
};

// Hardcode a token for testing (this will expire)
const hardcodedToken = 'd714f03642514fe7a39c6b8dc8b5aa7d';

export default function () {
    const headers = {
        'Authorization': `Bearer ${hardcodedToken}`,
        'Content-Type': 'application/json',
    };

    // Test listing resources
    const response = http.get('https://api.ed-fi.org/v7.3/api/data/v3/ed-fi/academicWeeks?limit=5', { headers });
    
    check(response, {
        'status is 200': (r) => r.status === 200,
        'has results': (r) => r.body.length > 0,
    });

    if (response.status === 401) {
        console.log('Token expired or invalid. Need a fresh token.');
    }
}