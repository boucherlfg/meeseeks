# Test: Wait for Multiple Agents

This test demonstrates the new ability to wait for multiple agents concurrently.

## Usage Examples

### Single agent wait (existing functionality)
```json
{
  "action": "wait",
  "agentId": "root.1"
}
```

### Multiple agents wait (new functionality)
```json
{
  "action": "wait",
  "agentIds": ["root.1", "root.2", "root.3"]
}
```

## What Changed

1. **New parameter**: Added `agentIds` array parameter to the `wait` action
2. **Concurrent waiting**: When `agentIds` is provided, all agents are awaited concurrently using `Task.WhenAll`
3. **Formatted output**: Results from multiple agents are separated by `---` for clarity
4. **Backward compatible**: The existing single-agent `agentId` parameter still works

## Implementation Details

- If both `agentId` and `agentIds` are provided, `agentIds` takes precedence
- All agents in the array must exist (have been started with `agent start`)
- The wait is truly concurrent - all agents run in parallel
- Results are returned in the order they complete (via Task.WhenAll)
