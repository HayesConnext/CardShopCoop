using System;
using System.Runtime.CompilerServices;
using BepInEx;
using CardShopCoop.Api;
using CardShopCoop.Net;

namespace CardShopCoop.Steam
{
    /// <summary>
    /// The Steam transport, shipped as its own BepInEx plugin that HARD-depends on the core mod.
    /// BepInEx loads core first, so this plugin can simply register its backend through the public
    /// API; the core assembly has no reference to Steam at all, and a build carrying a same-named
    /// Steamworks stub just never registers this backend.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("dev.meepen.cardshopcoop", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class SteamCoopPlugin : BaseUnityPlugin
    {
        public const string Guid = "dev.meepen.cardshopcoop.steam";
        public const string Name = "CardShopCoop Steam Transport";
        public const string Version = "1.0.0";

        private IDisposable _registration;

        private void Awake()
        {
            if (!SteamProbe.IsUsable())
            {
                Logger.LogInfo("Steam transport disabled: the real Steamworks wrapper is not usable on this build.");
                return;
            }

            try
            {
                _registration = Register();
                Logger.LogInfo("Steam transport registered.");
            }
            catch (Exception error)
            {
                Logger.LogWarning("Steam transport unavailable: " + error.Message);
            }
        }

        /// <summary>
        /// Deliberately separate and NoInlining: this is the only method in this file that names a
        /// Steam-bearing type, so it is the only one that can fail to JIT. The try/catch that
        /// makes that recoverable therefore lives in Awake, whose own body is Steam-free (a
        /// try/catch in the SAME method would catch nothing, because the throw happens at JIT
        /// time, before the body and its handlers ever run).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private IDisposable Register()
        {
            var bridge = new SteamBridgeImpl();
            bridge.Init();
            return CoopApi.RegisterNetworkingBackend(new SteamNetworkingBackend(bridge));
        }

        private void OnDestroy()
        {
            _registration?.Dispose();
            _registration = null;
        }
    }
}
