using System;
using System.Collections.Generic;

namespace CardShopCoop.Runtime
{
    /// <summary>Delivery state shared by the coalesced event forms below: one delivery at a time,
    /// nested raises folded into it, and a bound so a subscriber that never stops re-raising cannot
    /// hold the frame the way the recursion used to.</summary>
    internal struct CoalescedDelivery
    {
        /// <summary>Deliveries allowed to settle one raise. A subscriber that re-raises without
        /// making progress is left pending for the next raise instead of spinning the frame.</summary>
        private const int SettleCap = 4;

        private bool _delivering;
        private bool _raised;
        private bool _warned;

        internal void Deliver(Action invoke)
        {
            if (_delivering)
            {
                _raised = true;
                return;
            }

            _delivering = true;
            try
            {
                for (var pass = 1; pass <= SettleCap; pass++)
                {
                    _raised = false;
                    invoke();
                    if (!_raised)
                    {
                        return;
                    }
                }

                WarnOnce();
            }
            finally
            {
                _delivering = false;
            }
        }

        private void WarnOnce()
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            CoopPlugin.Log.LogWarning("coalesced event did not settle after " + SettleCap
                + " deliveries; the remaining raise stays pending for the next one.");
        }
    }

    /// <summary>
    /// A notification whose publisher raises it from wherever the change actually happened, with
    /// nested raises coalesced. A subscriber that synchronously drives a game call which changes
    /// the same thing again would otherwise re-enter the publisher - and, when the subscriber is
    /// the module that caused the change, re-enter its own apply - and recurse until the main
    /// thread died. Here the nested raise is remembered and delivered once the current delivery
    /// finishes, so the chain settles in a few passes on one stack frame. Delivery stays
    /// synchronous for the outermost raise, so an ordinary subscriber keeps the ordering and
    /// timing it always had.
    /// </summary>
    internal sealed class CoalescedEvent
    {
        private readonly Action _invoke;
        private CoalescedDelivery _delivery;

        internal CoalescedEvent()
        {
            _invoke = () => Handlers?.Invoke();
        }

        internal event Action Handlers;

        internal void Raise() => _delivery.Deliver(_invoke);
    }

    /// <summary>Value-carrying <see cref="CoalescedEvent"/>. A coalesced raise delivers only the
    /// newest value, so subscribers must read the payload as "the latest state" rather than as a
    /// per-change record.</summary>
    internal sealed class CoalescedEvent<T>
    {
        private readonly Action _invoke;
        private CoalescedDelivery _delivery;
        private T _value;

        internal CoalescedEvent()
        {
            _invoke = () => Handlers?.Invoke(_value);
        }

        internal event Action<T> Handlers;

        internal void Raise(T value)
        {
            _value = value;
            _delivery.Deliver(_invoke);
        }
    }

    /// <summary>Queued delivery state for notifications that are a stream of one-shot facts rather
    /// than a level signal: every raise is preserved (nothing is folded away), and a raise made
    /// while delivering is queued and delivered in the next pass, in raise order.</summary>
    internal struct CoalescedQueuedDelivery<TValue>
    {
        /// <summary>Passes allowed to settle one raise; a cascade deeper than this is left queued
        /// for the next raise instead of looping the frame away.</summary>
        private const int SettleCap = 4;

        private Dictionary<int, TValue> _queued;
        private List<int> _order;
        private int _nextKey;
        private bool _delivering;
        private bool _warned;

        internal void Enqueue(int key, TValue value, Action<int, TValue> deliver)
        {
            Merge(key, value);
            if (_delivering)
            {
                return;
            }

            _delivering = true;
            try
            {
                for (var pass = 1; pass <= SettleCap; pass++)
                {
                    if (!DeliverPass(deliver))
                    {
                        return;
                    }
                }

                WarnOnce();
            }
            finally
            {
                _delivering = false;
            }
        }

        /// <summary>Enqueues under a key that is unique per raise, so nothing merges.</summary>
        internal void EnqueueNew(TValue value, Action<int, TValue> deliver)
            => Enqueue(_nextKey++, value, deliver);

        private void Merge(int key, TValue value)
        {
            _queued ??= new Dictionary<int, TValue>();
            _order ??= new List<int>();
            if (!_queued.ContainsKey(key))
            {
                _order.Add(key);
            }

            _queued[key] = value;
        }

        private bool DeliverPass(Action<int, TValue> deliver)
        {
            if (_queued == null || _queued.Count == 0)
            {
                return false;
            }

            var batch = new List<KeyValuePair<int, TValue>>(_queued.Count);
            for (var i = 0; i < _order.Count; i++)
            {
                if (_queued.TryGetValue(_order[i], out var value))
                {
                    batch.Add(new KeyValuePair<int, TValue>(_order[i], value));
                }
            }

            _queued.Clear();
            _order.Clear();
            for (var i = 0; i < batch.Count; i++)
            {
                deliver(batch[i].Key, batch[i].Value);
            }

            return true;
        }

        private void WarnOnce()
        {
            if (_warned)
            {
                return;
            }

            _warned = true;
            CoopPlugin.Log.LogWarning("coalesced queue did not settle after " + SettleCap
                + " deliveries; the rest stays queued for the next raise.");
        }
    }

    /// <summary>
    /// A notification that preserves every payload. Use it for a stream of one-shot facts (an id
    /// that retires, is consumed, must be cleaned up exactly once) where folding a raise away would
    /// strand the work it names; <see cref="CoalescedEvent"/> is for "something changed, re-check".
    /// Nested raises are delivered in the next pass in raise order instead of re-entering the
    /// publisher, which is what kept a retirement's cleanup handlers from recursing.
    /// </summary>
    internal sealed class CoalescedQueue<TValue>
    {
        private readonly Action<int, TValue> _deliver;
        private CoalescedQueuedDelivery<TValue> _delivery;

        internal CoalescedQueue()
        {
            _deliver = (_, value) => Handlers?.Invoke(value);
        }

        internal event Action<TValue> Handlers;

        internal void Raise(TValue value) => _delivery.EnqueueNew(value, _deliver);
    }
}
