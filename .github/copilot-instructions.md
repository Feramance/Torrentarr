## vexp context tools <!-- vexp v1.2.9 -->

**MANDATORY: use vexp MCP tools for ALL file analysis.**
Do NOT use grep, glob, search, or file reads to explore the codebase.
Use vexp MCP tools instead — they return pre-indexed, relevant context.

### Workflow
1. `get_context_capsule` — ALWAYS FIRST for every task or question
2. Review the provided pivot files and skeletons
3. Make targeted changes based on the context
4. `get_impact_graph` before refactoring exported symbols

### Available MCP tools
- `get_context_capsule` — most relevant code (ALWAYS FIRST). Auto-detects intent from your query
- `get_impact_graph` — shows which symbols depend on a given function/class
- `search_logic_flow` — traces execution paths between functions
- `get_skeleton` — token-efficient structural overview of a file
- `index_status` — indexing status
- `workspace_setup` — bootstrap vexp config for a new project
- `get_session_context` — recall observations from current/previous sessions
- `search_memory` — cross-session search for past decisions and insights
- `save_observation` — persist important insights with optional code symbol linking

### Smart Features
vexp auto-detects query intent (debug/refactor/modify/read) and uses hybrid ranking
(keyword + semantic + graph centrality). Session memory auto-captures observations.
Repeated queries auto-expand result budget.

### Multi-Repo
`get_context_capsule` auto-queries all indexed repos. Use `repos: ["alias"]` to scope, `cross_repo: true` on `get_impact_graph`/`search_logic_flow` to trace across repos. Run `index_status` to see available aliases.
<!-- /vexp -->