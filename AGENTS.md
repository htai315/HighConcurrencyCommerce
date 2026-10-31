## Graphify knowledge graph

This project has a Graphify knowledge graph in `graphify-out/`.

Rules:

- For questions about architecture, code relationships, callers, dependencies, or data flow, prefer a scoped Graphify query when the existing graph can answer it. Use `graphify-out/GRAPH_REPORT.md` or the wiki for broad orientation when those files exist.
- Treat the graph as an index and navigation aid. Verify critical implementation details in source code, especially when the task concerns whether the graph itself is correct.
- After modifying any code file, run `graphify update .` from `D:\Personal_Project` after code generation/formatting and before final verification or handoff. Run it again if code changes after the update.
- If `graphify` is unavailable on `PATH`, use the installed equivalent `python -m graphify update .` (Graphify 0.9.97 is installed editable from `D:\graphify\graphify`). A known executable is under the current user's Python 3.11 `LocalCache\local-packages\Python311\Scripts\graphify.exe`.
- A Graphify update does not replace tests, builds, linting, security checks, or reading the changed source.
- Do not hand-edit generated files under `graphify-out/`.
