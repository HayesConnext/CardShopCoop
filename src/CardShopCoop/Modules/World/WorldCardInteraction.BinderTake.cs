using System;
using System.Collections.Generic;

namespace CardShopCoop.Modules.World
{
    /// <summary>
    /// The hand half of a predicted binder pick. Taking a card out of the collection binder
    /// reduces the shared collection AND moves a physical card into the player's hand
    /// (CollectionBinderFlipAnimCtrl.OnRightMouseButtonUp -> ReduceCard/RemoveGradedCard, then
    /// AddHoldCard). The reduction travels as the prediction; if the host refuses it because the
    /// last copy went to another player, the prediction's undo restores the collection but the
    /// spawned hand card must be despawned too, or the player keeps a copy the shared album no
    /// longer owns - the duplication seen when two players take the same card at once.
    ///
    /// The card object cannot be captured where the prediction is registered: the game reduces
    /// the collection BEFORE it moves the card into the hand. So the two binder methods that can
    /// end in a hand card open a scope; the observer-side prediction binds itself while the scope
    /// runs, and the scope's finalizer pairs it with whatever the hold list actually gained. A
    /// reduce that only fed a binder screen (grading selection, bulk donation, deck edit) adds
    /// nothing to the hand and leaves its binding empty, so its refusal keeps the collection-only
    /// behavior.
    /// </summary>
    internal sealed partial class WorldCardInteraction
    {
        /// <summary>One in-flight binder pick waiting for the card object the game moves into the
        /// hand. Null Card3d means this pick never produced one (a binder-screen reduction).</summary>
        private sealed class BinderTakeBinding
        {
            internal InteractableCard3d Card3d;
        }

        /// <summary>One invocation of a binder method that can put a card into the hand. The held
        /// cards at scope open are kept as references: AddHoldCard only ever appends, but the same
        /// invocation can also move an OLDER card out of the hand (HideCardAlbum's
        /// EnterHoldCardMode auto-places slot 0 onto a primed display), so the new card must be
        /// found by diffing, not by index. A null list means the controller was unavailable when
        /// the scope opened; nothing can be paired, which is safer than misbinding.</summary>
        private sealed class BinderTakeScope
        {
            internal BinderTakeScope Previous;
            internal List<InteractableCard3d> HeldBefore;
            internal readonly List<BinderTakeBinding> Bindings = new();
        }

        /// <summary>One in-flight client graded removal. <see cref="Superseded"/> means the shared
        /// album row is already accounted for - the local removal changed nothing (a stale slot
        /// whose certificate left first) or another player's accepted removal consumed the same
        /// row - so neither the rollback nor a follower replay may touch the row again.</summary>
        private sealed class GradedRemovalGuard
        {
            internal Guid PredictionId;
            internal bool Superseded;
        }

        private static BinderTakeScope _binderTakeScope;

        // Keyed by CardPriceKey (certificate included when Grading Overhaul encodes it). Only
        // client predictions register here; the host never predicts.
        private static readonly Dictionary<string, List<GradedRemovalGuard>> _gradedRemovalGuards =
            new(StringComparer.Ordinal);

        /// <summary>Opens one binder interaction that can end with a card in the hand. Called from
        /// a prefix so the held cards are known before vanilla runs; nesting is saved and restored
        /// by the scope.</summary>
        internal static void BeginBinderTake()
        {
            _binderTakeScope = new BinderTakeScope
            {
                Previous = _binderTakeScope,
                HeldBefore = WorldCardDisplay.HeldCards() is { } held
                    ? new List<InteractableCard3d>(held) : null,
            };
        }

        /// <summary>Closes the scope and pairs each waiting binding with one held card that was not
        /// in the hand when the scope opened, in order. Runs from a finalizer, so an exception in
        /// vanilla cannot leave the scope latched (an interrupted take simply leaves its bindings
        /// empty).</summary>
        internal static void EndBinderTake()
        {
            var scope = _binderTakeScope;
            if (scope == null)
            {
                return;
            }

            _binderTakeScope = scope.Previous;
            if (scope.Bindings.Count == 0 || scope.HeldBefore == null)
            {
                return;
            }

            var held = WorldCardDisplay.HeldCards();
            if (held == null)
            {
                return;
            }

            // The new card(s) are the held cards that were not there before. Scanning forward
            // keeps the append order; skipping pre-existing references keeps the pairing correct
            // when the same invocation also moved an older card out of the hand.
            var candidate = 0;
            for (var i = 0; i < scope.Bindings.Count; i++)
            {
                while (candidate < held.Count && scope.HeldBefore.Contains(held[candidate]))
                {
                    candidate++;
                }

                if (candidate >= held.Count)
                {
                    break;
                }

                scope.Bindings[i].Card3d = held[candidate];
                candidate++;
            }
        }

        /// <summary>Client: remembers an in-flight binder pick so its refusal can despawn the exact
        /// card the game moved into the hand. Null outside a binder method, so unrelated removals
        /// (donation and deck screens, trades, bulk actions) never touch the hand.</summary>
        private static BinderTakeBinding CreateBinderTakeBinding()
        {
            var scope = _binderTakeScope;
            if (scope == null)
            {
                return null;
            }

            var binding = new BinderTakeBinding();
            scope.Bindings.Add(binding);
            return binding;
        }

        /// <summary>Client: the host refused a binder pick - remove the card the game spawned
        /// into the hand through the game's own teardown. A card the player already moved out of
        /// the hand is left alone: its destination's own sync owns it now.</summary>
        private static void DespawnRejectedBinderTake(BinderTakeBinding binding, CardData card)
        {
            if (binding == null)
            {
                return;
            }

            var card3d = binding.Card3d;
            binding.Card3d = null;
            if (card3d == null || !card3d || !WorldCardDisplay.IsHeld(card3d))
            {
                return;
            }

            if (!WorldCardDisplay.DespawnHeldCard(card3d))
            {
                CoopPlugin.Log.LogWarning("binder pick refused, but its card could not be removed "
                    + "from the hand through the game's hold teardown; the player may keep a copy "
                    + "the album no longer owns.");
                return;
            }

            CoopPlugin.Log.LogInfo("binder pick refused: removed " + CardIdent(card)
                + " from the hand.");
        }

        /// <summary>Client: tracks one in-flight graded removal so a competing accepted removal of
        /// the same certificate can mark its rollback. The prediction id only exists after the
        /// send succeeded, so the guard is registered here.</summary>
        private static void RegisterGradedRemovalGuard(Guid predictionId, GradedRemovalGuard guard,
            CardData card)
        {
            var key = CardPriceKey(card);
            if (key == null)
            {
                return;
            }

            guard.PredictionId = predictionId;
            if (!_gradedRemovalGuards.TryGetValue(key, out var guards))
            {
                guards = new List<GradedRemovalGuard>();
                _gradedRemovalGuards.Add(key, guards);
            }

            guards.Add(guard);
        }

        /// <summary>Client: another player's accepted removal of this card was applied. When the
        /// apply removed nothing locally, the row it consumed was one this client had already
        /// taken itself: mark exactly ONE still-open guard, so that pick's rollback does not put
        /// the row back. Marking one (not all) keeps the row count right when several same-key
        /// picks are in flight; a vanilla album can carry several same-grade rows of one card.
        /// When the apply did remove a local row, it consumed a different copy and no open take
        /// needs marking.</summary>
        internal static void MarkRemoteGradedRemoval(CardData card)
        {
            var key = CardPriceKey(card);
            if (key == null || !_gradedRemovalGuards.TryGetValue(key, out var guards))
            {
                return;
            }

            for (var i = 0; i < guards.Count; i++)
            {
                if (guards[i].Superseded)
                {
                    continue;
                }

                guards[i].Superseded = true;
                return;
            }
        }

        /// <summary>Drops the guards of a prediction that left the tracker (accepted echo,
        /// refusal, or teardown), keeping the registry bounded by in-flight removals.</summary>
        internal static void OnPredictionRetired(Guid predictionId)
        {
            if (predictionId == Guid.Empty || _gradedRemovalGuards.Count == 0)
            {
                return;
            }

            List<string> emptiedKeys = null;
            foreach (var entry in _gradedRemovalGuards)
            {
                var guards = entry.Value;
                for (var i = guards.Count - 1; i >= 0; i--)
                {
                    if (guards[i].PredictionId == predictionId)
                    {
                        guards.RemoveAt(i);
                    }
                }

                if (guards.Count == 0)
                {
                    if (emptiedKeys == null)
                    {
                        emptiedKeys = new List<string>();
                    }

                    emptiedKeys.Add(entry.Key);
                }
            }

            if (emptiedKeys == null)
            {
                return;
            }

            for (var i = 0; i < emptiedKeys.Count; i++)
            {
                _gradedRemovalGuards.Remove(emptiedKeys[i]);
            }
        }

        /// <summary>World teardown: no prediction can survive the reset, so neither can its
        /// binding or guard.</summary>
        private static void ClearBinderTakeState()
        {
            _binderTakeScope = null;
            _gradedRemovalGuards.Clear();
        }
    }
}
