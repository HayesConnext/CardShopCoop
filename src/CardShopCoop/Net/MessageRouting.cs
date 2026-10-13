using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Generic;
using CardShopCoop.Util;
using System.Reflection;
using CardShopCoop.Net.Protocol;
using CardShopCoop.Net.Connection;
using CardShopCoop.Api;

namespace CardShopCoop.Net
{
    public sealed class MessageContext
    {
        public PeerConnection Connection;
        public bool InGame;
        public ICoopTransport Transport;

        // Core supplies the bounded-dispatch work estimate. External callers that dispatch
        // directly use the safe unit default.
        internal int WorkCost = 1;

        public int ConnectionId
        {
            get
            {
                return Connection == null ? 0 : Connection.Id;
            }
        }

        public bool IsAuthenticated
        {
            get
            {
                return Connection != null
                    && (Connection.State == ConnectionState.Transferring
                        || Connection.State == ConnectionState.FullyJoined);
            }
        }
    }

    /// <summary>
    /// A reliable message handler threw. The router discards that one message and leaves the
    /// connection up, so this carries the failure out to the core instead of a session-killing
    /// exception: the core surfaces it to the player, and the cause survives the frame's
    /// continued dispatch.
    /// </summary>
    public sealed class HandlerFailure
    {
        public MessageDescriptor Descriptor
        {
            get;
        }

        public MessageContext Context
        {
            get;
        }

        public Exception Error
        {
            get;
        }

        public string WireName => Descriptor?.WireName;

        public Type MessageType => Descriptor?.MessageType;

        public HandlerFailure(MessageDescriptor descriptor, MessageContext context, Exception error)
        {
            Descriptor = descriptor;
            Context = context;
            Error = error;
        }
    }

    /// <summary>
    /// How one dispatch ended. The core needs to tell a genuine protocol rejection (an unknown,
    /// unauthorized, or unhandled pre-activation frame - a peer violation) from a message whose
    /// handler ran and threw: only the former may trigger the pre-activation protocol-violation
    /// disconnect, because a thrown handler is a bug on THIS side, not misbehavior by the peer.
    /// </summary>
    internal enum DispatchOutcome
    {
        Handled,
        Rejected,
        HandlerFailed,
    }

    public sealed class MessageRouter : ICoopMessageRegistry
    {
        private sealed class TargetRegistration
        {
            public object Target;
            public IProtocolRegistration Registration;
        }

        private readonly List<TargetRegistration> _targetRegistrations = new();
        private readonly object _targetLock = new();

        /// <summary>Contextual rejection seam for diagnostics, metrics, or disconnect policy.</summary>
        public event Action<MessageRejection> Rejected;

        /// <summary>Raised after a reliable handler throws and its message is discarded. The
        /// connection is deliberately kept, so this is the seam the core uses to surface the
        /// failure (and any rate-limited diagnostics) to the player.</summary>
        public event Action<HandlerFailure> HandlerFailed;

        public MessageRouter()
        {
            MessageRegistry.EnsureInitialized();
        }

        public void RegisterAttributedHandlers(object target)
        {
            RegisterAttributedHandlers(target, false);
        }

        /// <summary>
        /// Registers handlers owned by the core lifecycle. This is intentionally internal: an
        /// extension can register handlers, but cannot obtain the pre-authentication privilege.
        /// </summary>
        internal void RegisterCoreAttributedHandlers(object target)
        {
            RegisterAttributedHandlers(target, true);
        }

        private void RegisterAttributedHandlers(object target, bool coreOwner)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            MessageRegistry.EnsureInitialized();

            // Probe: which behaviours actually REACH registration on this process. A module whose
            // handlers are missing never got this far, which separates "the behaviour was never enabled"
            // from "it was enabled and bound nothing" - and those are the two candidates left for the
            // client dropping every message. Cheap, and only until that is settled.
            CoopPlugin.Log?.LogInfo("handler registration: " + target.GetType().FullName
                + (coreOwner ? " (core)" : ""));
            var methods = target.GetType().GetMethods(BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic);
            var pending = new List<ProtocolHandlerRegistration>();
            var pendingTypes = new HashSet<Type>();
            for (var i = 0; i < methods.Length; i++)
            {
                var method = methods[i];
                var attribute = method.GetCustomAttribute<MessageHandlerAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var isExternal = parameters.Length > 0
                    && parameters[0].ParameterType == typeof(CoopMessageContext);
                if (method.IsStatic || method.ReturnType != typeof(void) || parameters.Length != 2
                    || (parameters[0].ParameterType != typeof(MessageContext) && !isExternal)
                    || !typeof(INetMessage).IsAssignableFrom(parameters[1].ParameterType)
                    || parameters[1].ParameterType != attribute.MessageType)
                {
                    throw new InvalidOperationException("Invalid network handler signature: "
                        + method.DeclaringType?.FullName + "." + method.Name);
                }

                var messageType = parameters[1].ParameterType;
                if (!pendingTypes.Add(messageType))
                {
                    throw new InvalidOperationException("Duplicate network handler for "
                        + messageType.FullName + " in one target");
                }
                if (!ProtocolRegistry.Default.TryGetRegisteredDescriptor(messageType,
                    out _))
                {
                    throw new InvalidOperationException("No registered network descriptor for "
                        + messageType.FullName);
                }

                pending.Add(new ProtocolHandlerRegistration(messageType,
                    isExternal
                        ? (context, message) => method.Invoke(target,
                            new object[] { ToPublicContext(context), message })
                        : (context, message) => method.Invoke(target,
                            new object[] { context, message })));
            }

            if (pending.Count == 0)
            {
                return;
            }

            var owner = coreOwner
                ? ProtocolRegistry.CoreOwner
                : ProtocolRegistry.CreateOwner(
                    "handlers:" + target.GetType().Assembly.GetName().Name);
            lock (_targetLock)
            {
                var registration = ProtocolRegistry.Default.RegisterHandlers(pending, owner);
                _targetRegistrations.Add(new TargetRegistration
                {
                    Target = target,
                    Registration = registration,
                });
            }
        }

        public IProtocolRegistration RegisterHandler(Type messageType,
            ProtocolMessageHandler handler, ProtocolRegistrationOwner owner = null)
        {
            owner ??= ProtocolRegistry.CreateOwner("router:explicit");
            return ProtocolRegistry.Default.RegisterHandler(messageType, handler, owner);
        }

        public void UnregisterAttributedHandlers(object target)
        {
            if (target == null)
            {
                return;
            }

            List<IProtocolRegistration> registrations = null;
            lock (_targetLock)
            {
                for (var i = _targetRegistrations.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_targetRegistrations[i].Target, target))
                    {
                        registrations ??= new List<IProtocolRegistration>();
                        registrations.Add(_targetRegistrations[i].Registration);
                        _targetRegistrations.RemoveAt(i);
                    }
                }
            }

            if (registrations == null)
            {
                return;
            }
            for (var i = 0; i < registrations.Count; i++)
            {
                registrations[i].Dispose();
            }
        }

        private static CoopMessageContext ToPublicContext(MessageContext context)
            => new CoopMessageContext(context.Connection, context.InGame);

        public bool Dispatch(MessageContext context, INetMessage message)
        {
            return Dispatch(context, message, false) == DispatchOutcome.Handled;
        }

        /// <summary>
        /// Internal core lifecycle path for handshake/control dispatch before the generic
        /// authenticated-session gate is established. External assemblies cannot call it. Unlike
        /// the public <see cref="Dispatch(MessageContext, INetMessage)"/> it reports the exact
        /// outcome, so the core can keep a thrown handler from being read as a peer violation.
        /// </summary>
        internal DispatchOutcome DispatchCore(MessageContext context, INetMessage message)
        {
            return Dispatch(context, message, true);
        }

        public bool IsRegistered(INetMessage message)
        {
            if (message == null)
            {
                return false;
            }
            return ProtocolRegistry.Default.CurrentSnapshot.TryGetHandler(message.GetType(),
                out _);
        }

        private DispatchOutcome Dispatch(MessageContext context, INetMessage message, bool corePath)
        {
            if (context == null)
            {
                return Reject(null, null, "message context is null", context);
            }
            if (message == null)
            {
                return Reject(null, null, "message is null", context);
            }

            var snapshot = ProtocolRegistry.Default.CurrentSnapshot;
            if (!snapshot.TryGet(message.GetType(), out var descriptor))
            {
                return Reject(message.GetType().FullName, message.GetType(),
                    "no descriptor in the current protocol snapshot", context);
            }
            if (!snapshot.TryGetHandler(message.GetType(), out var handler))
            {
                Msg.LogDecodeWarning(descriptor.WireName,
                    "coop: no handler bound for " + descriptor.WireName + " on connection "
                    + context.ConnectionId + "; message dropped (module disabled or "
                    + "RegisterAttributedHandlers was not called)");
                return Reject(descriptor.WireName, descriptor.MessageType,
                    "no runtime handler is bound", context);
            }

            var authorization = ProtocolAuthorization.Authorize(context,
                corePath && handler.IsCoreOwner);
            if (!authorization.Allowed)
            {
                return Reject(descriptor.WireName, descriptor.MessageType,
                    authorization.Reason, context);
            }

            var timing = PerfProbe.StartHandlerMetric();
            var completed = false;
            try
            {
                handler.Invoke(context, message);
                completed = true;
                return DispatchOutcome.Handled;
            }
            catch (Exception error)
            {
                var typeName = descriptor.WireName;
                if (descriptor.Reliability == Reliability.Reliable)
                {
                    // A reliable handler is no longer allowed to take the session down. Drop the
                    // one message it could not process, keep the connection, and hand the failure
                    // to the core so the player is told instead of being silently disconnected.
                    CoopPlugin.Log.LogError("reliable handler failed for " + typeName
                        + " on connection " + context.ConnectionId
                        + "; discarding the message and keeping the session: " + error);
                    RaiseHandlerFailed(new HandlerFailure(descriptor, context, error));
                    return DispatchOutcome.HandlerFailed;
                }

                CoopPlugin.Log.LogWarning("transient handler failed for " + typeName
                    + " on connection " + context.ConnectionId + "; dropping message: " + error);
                return DispatchOutcome.HandlerFailed;
            }
            finally
            {
                PerfProbe.EndHandlerMetric(descriptor.MessageType, context.WorkCost,
                    timing, !completed);
            }
        }

        /// <summary>Runs the <see cref="HandlerFailed"/> observers. This is a boundary between the
        /// router and pluggable subscribers, so one observer throwing must not escape into the
        /// dispatch loop (where a reliable message's exception would be read as a dispatch failure
        /// and disconnect the peer the failure was just reported against).</summary>
        private void RaiseHandlerFailed(HandlerFailure failure)
        {
            try
            {
                HandlerFailed?.Invoke(failure);
            }
            catch (Exception subscriberError)
            {
                CoopPlugin.Log.LogError("HandlerFailed observer threw; the failure was still "
                    + "discarded and the session kept: " + subscriberError);
            }
        }

        private DispatchOutcome Reject(string wireName, Type messageType, string reason,
            MessageContext context)
        {
            var rejection = new MessageRejection(wireName, messageType, reason, context);
            Rejected?.Invoke(rejection);
            ProtocolRegistry.Default.RaiseRejection(rejection);
            return DispatchOutcome.Rejected;
        }
    }
}
