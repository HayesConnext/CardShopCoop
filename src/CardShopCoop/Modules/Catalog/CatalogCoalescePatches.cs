using System;
using System.Reflection;
using CardShopCoop.Modules.Hud;
using HarmonyLib;

namespace CardShopCoop.Modules.Catalog
{
    /// <summary>
    /// The pre-send gate for furniture purchases across game builds. A CLIENT's local checkout of a
    /// furniture object type the host cannot represent BY NAME is refused BEFORE the vanilla
    /// checkout queues its money event and spawns its package, so no charge is taken and no object
    /// is created that the host would only ever store as its vanilla equivalent (which the echo
    /// would then reconcile the client's own object down to). The HOST is never refused: its ids
    /// are the wire identity, and a peer coalesces what it ingests.
    ///
    /// Resolved reflectively: this assembly never names a game type in a patch signature or field,
    /// so the DLL still loads on a build where <c>FurnitureShopUIScreen</c> or
    /// <c>InventoryBase</c> is absent. The same vanilla method is reached both by the game's own
    /// confirmation screen and by Furniture Overhaul's page (which calls the checkout engine
    /// directly), so ONE hook covers both and nothing patches Furniture Overhaul itself.
    /// </summary>
    internal static class CatalogCoalescePatches
    {
        private static readonly Type InventoryBaseType = AccessTools.TypeByName("InventoryBase");

        private static readonly MethodInfo GetFurniturePurchaseData = InventoryBaseType == null
            ? null
            : AccessTools.Method(InventoryBaseType, "GetFurniturePurchaseData",
                new[] { typeof(int) });

        private static readonly FieldInfo ObjectTypeField = GetFurniturePurchaseData?.ReturnType == null
            ? null
            : AccessTools.Field(GetFurniturePurchaseData.ReturnType, "objectType");

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            try
            {
                var screenType = AccessTools.TypeByName("FurnitureShopUIScreen");
                if (screenType == null)
                {
                    CoopPlugin.Log.LogInfo("Catalog coalesce: this build has no FurnitureShopUIScreen, "
                        + "so the cross-build furniture gate has nothing to hook");
                    return;
                }

                var original = AccessTools.Method(screenType, "EvaluateCartCheckout");
                if (original == null)
                {
                    CoopPlugin.Log.LogWarning("Catalog coalesce: FurnitureShopUIScreen.EvaluateCartCheckout "
                        + "was not found, so the cross-build furniture gate is off");
                    return;
                }

                harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(CatalogCoalescePatches), nameof(Prefix)));
                CoopPlugin.Log.LogInfo("Catalog coalesce: reflected furniture checkout gate installed");
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Catalog coalesce: furniture gate patch failed: "
                    + error.Message);
            }
        }

        /// <summary>Refuses the checkout when a peer is connected whose build cannot represent the
        /// purchase's object type. No session, no known peer, an unreadable object type, or a
        /// representable type all let vanilla run unchanged; only the genuine cross-build case
        /// returns false, and it returns a plain refusal (there is nothing to predict for a local
        /// shop action). The original is skipped, so no money event is queued and no object spawns.</summary>
        internal static bool Prefix(int index)
        {
            if (!CatalogIdMap.HasPeerIdentity)
            {
                return true;
            }

            // Only a JOINER refuses. The host's own ids ARE the wire identity: a host may keep and
            // place furniture its peer cannot name (the peer coalesces it on ingest), so a host
            // purchase is never refused. It is the CLIENT whose local object would be rewritten down
            // to the peer's equivalent, so the refusal is the client's.
            if (CoopCore.Role != CoopRole.Client)
            {
                return true;
            }

            var objectType = ResolveObjectType(index);
            if (objectType == null
                || !CatalogIdMap.IsDefined(EnumKind.ObjectType, objectType.Value)
                || CatalogIdMap.CanPeerRepresent(EnumKind.ObjectType, objectType.Value))
            {
                return true;
            }

            CoopPlugin.Log.LogInfo("Catalog coalesce: refused a furniture checkout for object type "
                + objectType.Value + " because the connected peer's build cannot represent it");
            try
            {
                HudApi.SetStatusLine("The other player's game version doesn't have this item, "
                    + "so it can't be bought in this session.", 8f);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Catalog coalesce: could not show the refusal notice: "
                    + error.Message);
            }

            return false;
        }

        /// <summary>The object type of the furniture at <paramref name="index"/>, read through the
        /// game's own lookup, or null when the lookup is unavailable on this build. The returned
        /// game object is held only as <see cref="object"/> and its enum field is read as an int, so
        /// no game type appears in this assembly's metadata.</summary>
        private static int? ResolveObjectType(int index)
        {
            if (GetFurniturePurchaseData == null || index < 0)
            {
                return null;
            }

            try
            {
                var data = GetFurniturePurchaseData.Invoke(null, new object[] { index });
                if (data == null)
                {
                    return null;
                }

                var field = ObjectTypeField ?? AccessTools.Field(data.GetType(), "objectType");
                var value = field?.GetValue(data);
                return value == null ? (int?)null : Convert.ToInt32(value);
            }
            catch (Exception error)
            {
                CoopPlugin.Log.LogWarning("Catalog coalesce: could not resolve the furniture "
                    + "object type at index " + index + ": " + error.Message);
                return null;
            }
        }
    }
}
