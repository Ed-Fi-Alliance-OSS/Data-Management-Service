#!/bin/bash

# Check if iterations parameter is provided
if [ -z "$1" ]; then
    echo "Usage: $0 <number_of_iterations>"
    exit 1
fi

# Validate that the parameter is a positive integer
if ! [[ "$1" =~ ^[0-9]+$ ]] || [ "$1" -lt 1 ]; then
    echo "Error: Iterations must be a positive integer"
    exit 1
fi

iterations=$1

echo "Running task refinement loop $iterations times..."

# Run the refinement prompt for the specified number of iterations
for ((i=1; i<=iterations; i++)); do
    echo "=== Task refinement iteration $i/$iterations ==="
    codex exec --dangerously-bypass-approvals-and-sandbox @.orc/loop/prompts/task-refinement-loop-prompt.md
    echo ""
done

echo "Completed $iterations task refinement iterations"
