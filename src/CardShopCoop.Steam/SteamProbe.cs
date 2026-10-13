using System;

namespace CardShopCoop.Steam
{
    /// <summary>
    /// String-only test for the REAL Steamworks wrapper. It must live in its own type, and every
    /// name it uses must be a STRING, because this whole assembly may be loaded on a build that
    /// ships only a same-named stub - and naming a Steamworks type in a field or base would make
    /// THIS type unloadable too.
    /// </summary>
    internal static class SteamProbe
    {
        private const string Assembly = "com.rlabrecque.steamworks.net";

        public static bool IsUsable()
        {
            return Has("Steamworks.SteamAPI") && Has("Steamworks.CallResult`1")
                && Has("Steamworks.Callback`1");
        }

        private static bool Has(string typeName)
        {
            try
            {
                return Type.GetType(typeName + ", " + Assembly, false) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
