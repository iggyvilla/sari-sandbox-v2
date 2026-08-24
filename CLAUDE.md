# CLAUDE.md

Guidance for Claude Code working in this repository.

## Versioning (SemVer via conventional commits)

This repo derives its version (`x.y.z`) from commit prefixes via python-semantic-release (see the
root `AGENTS.md`). **Every commit message must use the conventional format** — the version silently
stops advancing on non-conforming commits.

- `feat:`  -> minor bump (1.0.0 -> 1.1.0)
- `fix:`   -> patch bump (1.0.0 -> 1.0.1)
- `feat!:` or a `BREAKING CHANGE:` footer -> major bump (1.0.0 -> 2.0.0)
- `chore:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:` -> no bump

Rules:
- Start with a type: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`, `ci`, `perf`, `style`.
- Optional scope in parens allowed: `feat(sockets): ...`.
- Imperative mood ("Add", "Fix", not "Adds"/"Added").
- One logical change per commit.

Examples:
```
feat(sockets): add distributed sandbox window title sync
fix: harden lidar GPU readback cancellation
```

Do NOT create tags or bump the version yourself — the workflow does it. Just write conventional commits.
