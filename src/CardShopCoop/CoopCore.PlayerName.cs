using System;
using CardShopCoop.Net;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        /// <summary>
        /// The name shown and sent on the wire. A name the player typed in the co-op window
        /// (NameOverride) wins outright; otherwise the platform's own name is the default, and the
        /// configured/machine name is the last resort. Read lazily by PresenceApi, so there is no
        /// polling: it runs at session start and when the window draws.
        /// </summary>
        private string ResolvePlayerName()
        {
            var configured = CoopPlugin.PlayerName.Value;
            if (CoopPlugin.NameOverride.Value && !string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var platform = PlatformPlayerName();
            if (!string.IsNullOrWhiteSpace(platform))
            {
                return platform;
            }

            return string.IsNullOrWhiteSpace(configured)
                ? (Environment.UserName ?? "")
                : configured;
        }

        /// <summary>Holds the platform name once it has been observed; see <see cref="PlatformPlayerName"/>.</summary>
        private string _platformName;

        /// <summary>
        /// The platform-provided name, or empty when this build has none. The Xbox gamertag comes
        /// first (Game Pass only). Failing that, any backend that knows its own platform persona
        /// supplies it - the Steam transport, even before a session exists, which is what lets the
        /// Steam name be the default in the window and not only on the wire.
        ///
        /// Once a name is seen it is cached, so the co-op window's draw loop does not re-read the
        /// Steam persona (a Steamworks call) every frame. Reading the Steam name fresh while no
        /// session exists also needs the Steam backend to have registered, so a miss is never
        /// cached: the empty string simply means "try again next read", which resolves as soon as
        /// the transport appears or the player signs in.
        /// </summary>
        private string PlatformPlayerName()
        {
            if (!string.IsNullOrEmpty(_platformName))
            {
                return _platformName;
            }

            var gamertag = PlatformProbe.XboxGamertag;
            if (!string.IsNullOrWhiteSpace(gamertag))
            {
                return _platformName = gamertag;
            }

            var backends = Backends; // refreshes the list if the Steam transport registered late
            for (var i = 0; i < backends.Count; i++)
            {
                var name = backends[i]?.LocalUserName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return _platformName = name;
                }
            }

            return "";
        }
    }
}
