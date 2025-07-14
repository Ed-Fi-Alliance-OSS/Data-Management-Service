#!/bin/bash

echo "Waiting 60 seconds for rate limit to reset..."
sleep 60

echo "Running smoke test with token caching..."
npm run test:smoke