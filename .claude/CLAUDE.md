## vexp — Context-Aware AI Coding <!-- vexp v1.2.9 -->

### MANDATORY: use vexp MCP tools before ANYTHING else
For every task — questions, bug fixes, features, exploration:
**call the `get_context_capsule` MCP tool first**.

Do NOT use grep, glob, Bash, or Read to search/explore the codebase.
Use vexp MCP tools instead — they return pre-indexed, relevant context.

### Available MCP tools (invoke via tool interface, NOT via HTTP or bash)
- `get_context_capsule` — most relevant code for your task or question (ALWAYS FIRST)
- `get_impact_graph` — what breaks if you change a symbol
- `search_logic_flow` — execution paths between functions
- `get_skeleton` — token-efficient file structure
- `index_status` — indexing status and health check
- `workspace_setup` — bootstrap vexp config for a new project
- `submit_lsp_edges` — submit type-resolved call edges (used by VS Code extension)
- `get_session_context` — recall observations from current/previous sessions
- `search_memory` — cross-session search for past decisions, insights, and explored code
- `save_observation` — persist important insights with optional code symbol linking

### Smart Features (automatic — no action needed)
- **Intent Detection**: vexp auto-detects query intent from your prompt keywords.
  "fix bug" → Debug mode (follows error paths), "refactor" → blast-radius mode,
  "add feature" → Modify mode, default → Read mode. Each mode optimizes result ranking.
- **Hybrid Search**: combines keyword matching (FTS) + semantic similarity (TF-IDF) + graph
  centrality for better results. Finds `validateCredentials` when searching "authentication".
- **Session Memory**: every tool call is auto-captured as an observation. Relevant memories
  from previous sessions are auto-surfaced inside `get_context_capsule` results. Observations
  linked to code symbols are auto-flagged stale when that code changes.
- **Feedback Loop**: repeated queries with similar terms auto-expand the result budget.
- **LSP Bridge**: VS Code captures type-resolved call edges for high-confidence call graphs.
- **Change Coupling**: files modified together in git history are included as related context.
- **Context Lineage**: frequently modified code gets a boost in relevance ranking.

### Workflow
1. `get_context_capsule("your task or question")` — ALWAYS FIRST (includes relevant memories)
2. Read the pivot files returned (full content)
3. Review supporting skeletons for broader context
4. Make targeted changes based on the context
5. `get_impact_graph` before refactoring to verify no regressions
6. `save_observation` to persist important decisions with code symbol links
7. `search_memory` or `get_session_context` to recall past work

### Advanced Parameters
- `include_tests: true` — include test files (useful for debugging)
- `max_tokens: 12000` — increase token budget for complex tasks
- `pivot_depth: 3` — deeper graph traversal for broad exploration
- `skeleton_detail: "detailed"` — more verbose skeletons

### Multi-Repo Workspaces
When multiple repos are indexed (monorepos, frontend+backend, microservices):
- `get_context_capsule` queries **all repos** automatically — results show repo alias per file
- Use `repos: ["alias"]` to restrict queries to specific repos
- Use `cross_repo: true` on `get_impact_graph` and `search_logic_flow` to trace across repo boundaries
- Use `index_status` to discover available repo aliases
- `get_skeleton` accepts `repo: "alias"` to target a specific repo's files
<!-- /vexp -->