using System;
using CardShopCoop.Modules.Presence;
using UnityEngine;

namespace CardShopCoop
{
    public partial class CoopCore
    {
        internal static bool TryEnqueueMainThread(Action action)
        {
            if (action == null)
            {
                return false;
            }

            var core = Instance;
            if (core == null)
            {
                return false;
            }

            return core._dispatcher.TryEnqueue("external-main-thread", action);
        }

        /// <summary>The local player's world position, or false when there is no local player yet.</summary>
        public static bool TryGetLocalPlayerPosition(out Vector3 position)
        {
            position = default(Vector3);
            return PresenceApi.TryGetLocalPlayerPosition(out position);
        }

        // True while the game's own text input has focus, so our key handling must not steal it.
        internal static bool NativeTextInputFocused()
        {
            var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
            if (sel == null)
            {
                return false;
            }

            var tmp = sel.GetComponent<TMPro.TMP_InputField>();
            return tmp != null && tmp.isFocused;
        }

    }
}


