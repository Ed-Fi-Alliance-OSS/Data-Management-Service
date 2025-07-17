# DMS Load Test Quick Reference

## Essential Commands

```bash
# Basic Tests
./run-load-test.sh --profile smoke     # 2 min quick test
./run-load-test.sh --profile dev       # 10 min dev test
./run-load-test.sh --profile staging   # 25 min staging test
./run-load-test.sh --profile prod      # 50 min full test

# Specific Phases
./run-load-test.sh --phase load        # Create resources only
./run-load-test.sh --phase readwrite   # CRUD operations only

# Help
./run-load-test.sh --help             # Show all options
```

## Profiles at a Glance

| Profile | Schools | Students | Staff | Duration | Use Case |
|---------|---------|----------|-------|----------|----------|
| smoke | 2 | 2 | 2 | ~2 min | Quick validation |
| dev | 10 | 100 | 20 | ~10 min | Development |
| staging | 50 | 5,000 | 800 | ~25 min | Pre-production |
| prod | 130 | 75,000 | 12,000 | ~50 min | Full scale |

## Test Phases

- **Full** (default): Smoke → Load → Read-Write
- **Load**: POST operations, creates test data
- **Read-Write**: 50% GET, 20% POST, 20% PUT, 10% DELETE

## Common Workflows

### First Time Setup
```bash
./run-load-test.sh --profile smoke
```

### Daily Testing
```bash
./run-load-test.sh --profile dev
```

### Performance Testing
```bash
./run-load-test.sh --profile prod
```

### Debugging Issues
```bash
# Test only creation
./run-load-test.sh --phase load --profile dev

# Test only CRUD
./run-load-test.sh --phase readwrite --profile dev
```

## Results Location

```
results/
├── smoke-{timestamp}.json
├── load-{profile}-{timestamp}.json
└── readwrite-{profile}-{timestamp}.json
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| k6 not found | Install k6: `brew install k6` |
| 401 errors | Remove `.env.load-test` and retry |
| Test stuck | Try smaller profile |
| No results | Check `results/` directory |

## Environment Overrides

```bash
# More users
VUS_LOAD_PHASE=50 ./run-load-test.sh

# Longer duration
DURATION_LOAD_PHASE=10m ./run-load-test.sh

# Different API
API_BASE_URL=https://staging.api.com ./run-load-test.sh
```

## Key Files

- `run-load-test.sh` - Main test runner
- `.env.load-test` - Auto-generated credentials
- `.env.profiles/` - Test scale configurations
- `results/` - Test results (JSON)
- `docs/` - Full documentation

## Need Help?

1. [How to Use Guide](how-to-use-load-test-runner.md)
2. [Troubleshooting Guide](troubleshooting.md)
3. [Architecture Details](architecture.md)