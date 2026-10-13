using System.Runtime.CompilerServices;

// The runtime (CardShopCoop.dll) is the only writer of the binding seam. External mods read the
// public surface only; they can never install, replace, or bypass the binding. The Steam transport
// assembly is a first-party sibling, so it may also allocate ConnectionId handles.
[assembly: InternalsVisibleTo("CardShopCoop")]
[assembly: InternalsVisibleTo("CardShopCoop.Steam")]
