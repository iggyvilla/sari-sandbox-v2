# Agent Notes

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
