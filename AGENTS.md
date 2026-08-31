# Agent Instructions

This project was built with the microsoft-foundry skill. Before working on or answering
questions about Foundry agents, read the microsoft-foundry skill first.

Keep the mock backend runnable without Azure resources. Never commit Copilot Studio client
secrets, delegated tokens, tunnel credentials, or environment-specific identifiers.

Use `src/FoundryCopilotA2A.Cli` for operational workflows. Do not reintroduce shell scripts
for app registration, consent, adapter startup, tunnels, smoke tests, or cleanup.
