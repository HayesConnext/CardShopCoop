using CardShopCoop.Net.Connection;
using PeerConnection = CardShopCoop.Net.Connection.PeerConnection;
using System;
using System.Collections.Generic;

namespace CardShopCoop.Net
{
    /// <summary>One received message, already reassembled and decoded from the wire.
    /// Transports never expose partial frames to callers.</summary>
    public struct InMsg
    {
        public PeerConnection Connection;
        public INetMessage Message;

        /// <summary>Set by the runtime's transports; not part of the public contract.</summary>
        internal bool ReceivedBeforeMessageIdActivation;

        public Type MessageType
        {
            get
            {
                return Message?.GetType();
            }
        }
    }

    /// <summary>
    /// Transport contract shared by the LAN UDP/KCP transport and the Steam
    /// KCP over Steam Networking Sockets transport.
    /// Connection ids are small ints; 1 is always "the host" from a client's view.
    ///
    /// This lives in CardShopCoop.Api so a transport can be contributed by its own assembly: the
    /// Steam transport is a separate plugin that references only this assembly.
    /// </summary>
    public interface ICoopTransport : IDisposable
    {
        /// <summary>
        /// Removes one decoded message and releases the transport's incoming-byte admission for
        /// it. Callers must use this method instead of retaining a raw producer queue.
        /// </summary>
        bool TryDequeueIncoming(out InMsg message);
        /// <summary>Removes one peer-disconnect notification, if any. Replaces the old public queue
        /// property, which handed every caller the transport's own mutable internals.</summary>
        bool TryDequeueDisconnect(out ConnectionEvent disconnect);
        event Action<PeerConnection> PeerConnected;

        /// <summary>Activates compact message ids for an authenticated peer.</summary>
        void ActivateMessageIds(PeerConnection connection);

        /// <summary>Queues a message using its registered MessageDescriptor.Reliability.</summary>
        void Send(PeerConnection connection, INetMessage message);
        /// <summary>Queues a message for every peer using its registered MessageDescriptor.Reliability.</summary>
        void Broadcast(INetMessage message);
        int ConnectionCount
        {
            get;
        }
        double SecondsSinceLastRecv(PeerConnection connection);
        IReadOnlyList<PeerConnection> Connections
        {
            get;
        }
        void Kick(PeerConnection connection, DisconnectInfo info = null);
        /// <summary>Send a bounded protocol disconnect, then tear down the connection.</summary>
        void GracefulDisconnect(PeerConnection connection, DisconnectInfo info = null);
        void Stop();

        /// <summary>Advances one transport pump step. This runs on the dedicated network
        /// thread owned by NetworkPump, never on the Unity update loop. All
        /// lifecycle calls (Start/Stop/Dispose/ActivateMessageIds) are marshalled onto that
        /// same thread so bearer and KCP state stay single-threaded.</summary>
        void PumpNetworkThread();

        /// <summary>Peer-silence tolerance. The Steam KCP over Steam Networking Sockets
        /// transport uses a longer window than LAN because Steam P2P links can be slower to
        /// report loss. Both transports are advanced on the dedicated network thread.</summary>
        double TimeoutSeconds
        {
            get;
        }
    }

    /// <summary>
    /// Internal control-plane admission used only by Core for the named application handshake.
    /// Keeping this separate from the public send surface prevents external callers from
    /// bypassing the Handshaking admission gate.
    /// </summary>
    public interface ICoopHandshakeTransport
    {
        void SendHandshake(PeerConnection connection, INetMessage message);
    }

    /// <summary>Lifecycle seam: a transport whose bearer must be started on the network pump
    /// thread. The runtime wraps contributed transports in NetworkPump, which starts them here.</summary>
    public interface ICoopStartable
    {
        void Start();
    }

    /// <summary>
    /// Fan-out admission used only by the host world relay. A relay is a
    /// <see cref="ICoopTransport.Broadcast"/> that omits one connection, so it must honor the
    /// exact same recipient admission as <c>Broadcast</c>: a peer that is disconnecting, still
    /// completing the named handshake, or has not yet activated compact message ids is skipped
    /// rather than treated as a hard send failure.
    /// </summary>
    public interface ICoopRelayTransport
    {
        void Relay(INetMessage message, int exceptConnectionId);
    }
}
