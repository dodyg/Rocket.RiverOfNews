# Copilot instructions — Rocket.RiverOfNews

This file is meant to help future Copilot sessions (and other AI assistants) quickly understand repository intent, where to look for build/test/lint commands, and any repo-specific conventions.

Repository snapshot
- Short description: "A website that allows river-of-news style RSS aggregation." (from README.md)
- Current contents: README.md, LICENSE, .gitignore. No source code, CI config, or package manifests were detected at the time this file was created.

Build / test / lint commands
- No build, test, or lint scripts were detected in the repository root.
- Copilot should look for common manifests to discover commands:
  - Node: package.json -> npm install; run a single test with: npm test -- -t "<test name>" or node --test <file>
  - Python: pyproject.toml / setup.cfg / tox.ini -> pytest; run a single test with: pytest path/to/test_file.py::test_name
  - Go: go.mod -> go test ./...; run a single test with: go test ./pkg/name -run TestName
  - Rust: Cargo.toml -> cargo test; run a single test with: cargo test test_name
  - Makefile: common targets are `make build`, `make test`, `make lint`; a single test target varies per Makefile
- If adding CI, prefer standard files (GitHub Actions: .github/workflows/) and document any custom test flags in README.md.

High-level architecture (what to expect)
- Intent: an RSS aggregation web app (feed ingestion, normalization, storage, API, UI). The repo currently lacks implementation files, so Copilot should:
  1. Search top-level directories: src/, server/, cmd/, web/, frontend/, api/, services/, workers/, migrations/
  2. Check for Dockerfile, docker-compose.yml, Makefile, package.json, pyproject.toml, go.mod to infer language and runtime
  3. Expect these logical components in a complete project:
     - Ingestor/collector: scheduled job or worker that fetches feeds, parses XML/Atom
     - Normalizer/parser: converts feeds into canonical article records
     - Deduper/merger: merges duplicate items and reduces noise
     - Storage: database schema and migrations (migrations/ or sql/), and data-access layer
     - API: REST/GraphQL endpoints that serve the UI
     - Frontend: static site or SPA in `frontend/` or `web/`
     - Background workers: for polling feeds, reprocessing, and cleanup

Key conventions and patterns
- No repository-specific code conventions were found (no source files to analyze).
- If adding code, follow these minimal placement conventions so Copilot suggestions remain consistent:
  - Put application code under `src/` or language-idiomatic root (pkg/, cmd/, app/)
  - Keep tests next to code (e.g., file_test.go) or in a top-level `tests/` folder depending on language ecosystem
  - Use `migrations/` or `db/migrations/` for DB migrations, and document how to run them in README
  - Document any non-standard commands or required environment variables in README.md or a CONTRIBUTING.md

AI assistant and other configs
- No assistant-specific config files detected (CLAUDE.md, .cursorrules, AGENTS.md, .windsurfrules, CONVENTIONS.md, etc.).
- If such files are added later, merge their short, actionable instructions into this file so Copilot can surface them quickly.

When editing or generating code
- Prefer small, focused changes and add tests alongside new behavior.
- When adding new top-level services or languages, update this file with the relevant build/test commands and any non-standard folder layout.

If you (the maintainer) want further automation
- Consider committing a minimal CI workflow and package manifest so Copilot can infer commands and run tests locally.

