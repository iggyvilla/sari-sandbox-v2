"""Debug websocket primitives for the SariAgent runtime.

The website/debug UI should treat this package as the backend contract.  The
agent publishes structured events into a DebugHub, and any browser-side UI can
subscribe to the embedded websocket server to render the same stream.
"""

from sari_agent.debug.events import DebugEvent
from sari_agent.debug.hub import DebugHub
from sari_agent.debug.server import DebugWebSocketServer
from sari_agent.debug.trace import ToolTraceContext, get_tool_trace, tool_trace

__all__ = [
    "DebugEvent",
    "DebugHub",
    "DebugWebSocketServer",
    "ToolTraceContext",
    "get_tool_trace",
    "tool_trace",
]
