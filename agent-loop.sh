#!/bin/bash

# Check if iterations parameter is provided
if [ -z "$1" ]; then
    echo "Usage: $0 <number_of_iterations>"
    exit 1
fi

# Validate that the parameter is a number
if ! [[ "$1" =~ ^[0-9]+$ ]]; then
    echo "Error: Iterations must be a positive integer"
    exit 1
fi

iterations=$1

echo "Running command $iterations times..."

# Run the command for the specified number of iterations
for ((i=1; i<=iterations; i++)); do
    echo "=== Iteration $i/$iterations ==="
    codex exec --dangerously-bypass-approvals-and-sandbox --model gpt-5.4 @agent-loop-prompt.md
    echo ""
done

echo "Completed $iterations iterations"
