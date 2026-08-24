# Agent Notes

## Versioning (SemVer via conventional commits)

This repo uses **python-semantic-release**. It reads the git commit history since the last tag
(`vX.Y.Z`) and derives the next version from the commit message prefixes. A GitHub Actions workflow
(`.github/workflows/release.yml`) runs it automatically on every push to `main`, then tags the
version and uploads a zip of the source to the GitHub release. So:

- `feat:`  -> minor bump (1.0.0 -> 1.1.0)
- `fix:`   -> patch bump (1.0.0 -> 1.0.1)
- `feat!:` or a `BREAKING CHANGE:` footer -> major bump (1.0.0 -> 2.0.0)
- `chore:`, `docs:`, `refactor:`, `test:`, `build:`, `ci:` -> no bump

### Every commit message MUST follow conventional commits

Because the version is derived from commit prefixes, **every commit message must use the
conventional format** or the version silently stops advancing (the tool skips it with no error).

Rules:
- Start with a type: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `build`, `ci`, `perf`, `style`.
- An optional scope in parens is allowed: `feat(sockets): ...`.
- Use the imperative mood ("Add", "Fix", not "Adds"/"Added"/"Adding").
- One logical change per commit.
- For a breaking change, append `!` to the type (`feat!`) or add a `BREAKING CHANGE:` line in the body.

Examples:
```
feat(sockets): add distributed sandbox window title sync
fix: harden lidar GPU readback cancellation
chore(release): 1.0.1 [skip ci]
```

Do NOT create a new tag or bump the version yourself — the workflow does that automatically.
Just write conventional commits.

---

- Most project code is in `Assets/Scripts`; check there first when code is mentioned.
- Do not run Unity playtests. The user will handle Unity validation.
- Python playtests/scripts are okay when useful.

## `Assets/Scripts` Structure

- Core runtime: agent controllers, UI handlers, barcode/price/expiration systems, room and interaction helpers.
- `StoreBuilder/`: editor/runtime store layout tools, selection, markers, props, and Store Builder UI partials.
- `ShelfBuilder/` and `ShelfItemHandlers/`: shelf geometry, fridge/item placement, item data, and spawning.
- `ItemPhysics/`: item pooling, basket/hand collisions, shelf stacks, and physics proxies.
- `SocketServers/`: Socket.IO/WebSocket server behavior for Sari agent and multiplayer commands.
- `GPUOptimizations/`, `Lidar/`, `Utility/`: rendering optimization, lidar capture/sensors, screenshots, outlines, doors, and helper tools.
