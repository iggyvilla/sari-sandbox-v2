# Store spec

A compact format for agents (or people) to author Sari stores, plus a validator that compiles it to the sandbox's store file.

| File | What |
|---|---|
| `schema.json` | The format. Its descriptions are the docs: coordinates, shelf types, defaults. Usable as a structured-output schema. |
| `catalog.json` | Categories, product names and sizes, prop footprints. Regenerate in Unity: **Tools > Store Spec > Export Catalog**. |
| `example_store.json` | A real store written as a spec. |
| `store_spec.py` | Validator + compiler. Needs `jsonschema`. |

## Usage

```sh
python3 store_spec.py validate my_store.json          # add --json for machine-readable issues
python3 store_spec.py compile my_store.json -o "<persistentDataPath>/My Store.json"
```

Without `jsonschema` installed: `uv run --with jsonschema store_spec.py ...`.

The validator checks the schema, product names (with suggestions), that listed products fit their level, shelf heights against the walls, footprints against the floor and each other, and that the agent can walk from its spawn point to every stocked shelf side and checkout (`--agent-radius`, default 0.3 m). Errors fail validation and compilation; warnings don't.

Load the compiled store by setting `DataHandler.storeName` to its file name, or pick it from the Store Builder's load menu.

## Agent workflow

1. Read `schema.json` and `catalog.json`.
2. Write a spec.
3. Run `validate --json`, fix every error, repeat until `"ok": true`.
4. `compile`.
