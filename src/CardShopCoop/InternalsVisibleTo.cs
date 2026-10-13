using System.Runtime.CompilerServices;

// The optional Steam transport lives in its own assembly
// (see CardShopCoop.Steam/Net/SteamBridgeContract.cs) so that this core assembly never carries
// Steamworks metadata - a build shipping a same-named Steamworks stub then cannot make any of
// our types unloadable. The Steam assembly needs the core's internal types and the CoopPlugin log.
[assembly: InternalsVisibleTo("CardShopCoop.Steam")]
