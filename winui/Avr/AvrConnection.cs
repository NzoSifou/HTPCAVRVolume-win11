using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace HTPCAVRVolume.AVRDevices
{
    enum AvrLinkState
    {
        Disconnected,
        Connecting,
        Connected
    }

    class LinkEventArgs : EventArgs
    {
        public LinkEventArgs(AvrLinkState state, string detail)
        {
            State = state;
            Detail = detail;
        }

        public AvrLinkState State { get; }

        /// <summary>Why we are in that state, for the status line. Null when there is nothing to say.</summary>
        public string Detail { get; }
    }

    class LineEventArgs : EventArgs
    {
        public LineEventArgs(string line)
        {
            Line = line;
        }

        public string Line { get; }
    }

    /// <summary>
    /// One long lived control session to an AVR.
    ///
    /// Callers only ever drop a string into a queue, so the keyboard hook feeding this class is
    /// never held up by a connect, a write, or an amplifier that is switched off. A worker thread
    /// owns the socket: it reconnects on its own, spaces commands out so the AVR is not flooded,
    /// and reports back everything the AVR says.
    /// </summary>
    class AvrConnection : IDisposable
    {
        private const int ConnectTimeoutMs = 3000;
        private const int MaxBackoffMs = 15000;

        private readonly string _host;
        private readonly int _port;
        private readonly string _terminator;
        private readonly int _minCommandIntervalMs;

        private readonly object _gate = new object();
        private readonly List<Command> _queue = new List<Command>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;

        private volatile bool _stopping;
        private TcpClient _client;
        private int _lastSentTicks;
        private int _lastHeartbeatTicks;
        private AvrLinkState _state = AvrLinkState.Disconnected;

        public AvrConnection(string host, int port, string terminator, int minCommandIntervalMs)
        {
            _host = host;
            _port = port;
            _terminator = terminator;
            _minCommandIntervalMs = minCommandIntervalMs;
            _lastSentTicks = Environment.TickCount;
            _lastHeartbeatTicks = Environment.TickCount;

            _worker = new Thread(Run) { IsBackground = true, Name = "AVR link" };
            _worker.Start();
        }

        /// <summary>Raised on the worker thread for each line the AVR sends us.</summary>
        public event EventHandler<LineEventArgs> LineReceived;

        /// <summary>Raised on the worker thread whenever the link comes up or goes down.</summary>
        public event EventHandler<LinkEventArgs> LinkChanged;

        /// <summary>Sent when the session has been idle, to keep it open and to resync our state.</summary>
        public string HeartbeatCommand { get; set; }

        public int HeartbeatIntervalMs { get; set; } = 45000;

        public AvrLinkState State
        {
            get { lock (_gate) { return _state; } }
        }

        /// <param name="coalesceKey">
        /// When set, a queued command carrying the same key is replaced rather than appended. A
        /// flick of the volume wheel then leaves one absolute command in the queue, not twenty.
        /// </param>
        /// <returns>
        /// True when this command adds one more line for the AVR to answer, false when it took
        /// the place of one already waiting. A caller counting the answers it is still owed needs
        /// to know the difference.
        /// </returns>
        public bool Send(string command, string coalesceKey = null)
        {
            if (string.IsNullOrEmpty(command))
            {
                return false;
            }

            lock (_gate)
            {
                if (coalesceKey != null)
                {
                    for (int i = 0; i < _queue.Count; i++)
                    {
                        if (_queue[i].CoalesceKey == coalesceKey)
                        {
                            _queue[i] = new Command(command, coalesceKey);
                            _wake.Set();
                            return false;
                        }
                    }
                }

                _queue.Add(new Command(command, coalesceKey));
            }

            _wake.Set();
            return true;
        }

        public void Dispose()
        {
            _stopping = true;
            _wake.Set();
            CloseClient(null);
            _worker.Join(500);
        }

        private void Run()
        {
            int backoffMs = 0;

            while (!_stopping)
            {
                if (CurrentClient == null)
                {
                    if (backoffMs > 0)
                    {
                        _wake.WaitOne(backoffMs);
                        if (_stopping)
                        {
                            break;
                        }
                    }

                    if (!TryConnect())
                    {
                        backoffMs = Math.Min(backoffMs == 0 ? 1000 : backoffMs * 2, MaxBackoffMs);
                        continue;
                    }

                    backoffMs = 0;
                }

                // Wakes up on its own as well, so a dropped link is noticed even when nobody is
                // touching the volume.
                _wake.WaitOne(250);
                if (_stopping)
                {
                    break;
                }

                DrainQueue();
                Heartbeat();
            }

            CloseClient(null);
        }

        private TcpClient CurrentClient
        {
            get { lock (_gate) { return _client; } }
        }

        private bool TryConnect()
        {
            SetState(AvrLinkState.Connecting, "Connecting to " + _host + ":" + _port + "...");

            TcpClient client = new TcpClient();
            try
            {
                client.NoDelay = true;
                IAsyncResult pending = client.BeginConnect(_host, _port, null, null);
                if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    client.Close();
                    SetState(AvrLinkState.Disconnected, "No answer from " + _host + ":" + _port);
                    return false;
                }

                client.EndConnect(pending);
            }
            catch (Exception ex)
            {
                client.Close();
                SetState(AvrLinkState.Disconnected, Describe(ex));
                return false;
            }

            lock (_gate)
            {
                _client = client;
            }

            _lastHeartbeatTicks = Environment.TickCount;
            SetState(AvrLinkState.Connected, null);

            Thread reader = new Thread(() => ReadLoop(client)) { IsBackground = true, Name = "AVR reader" };
            reader.Start();
            return true;
        }

        private void ReadLoop(TcpClient client)
        {
            byte[] buffer = new byte[512];
            StringBuilder line = new StringBuilder();

            try
            {
                NetworkStream stream = client.GetStream();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char c = (char)buffer[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (line.Length > 0)
                            {
                                LineReceived?.Invoke(this, new LineEventArgs(line.ToString()));
                                line.Length = 0;
                            }
                        }
                        else if (c >= ' ')
                        {
                            line.Append(c);
                        }
                    }
                }

                CloseClient(client, "Connection closed by the AVR");
            }
            catch (Exception ex)
            {
                CloseClient(client, _stopping ? null : Describe(ex));
            }
        }

        private void DrainQueue()
        {
            while (!_stopping)
            {
                TcpClient client = CurrentClient;
                if (client == null)
                {
                    return;
                }

                Command command;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        return;
                    }

                    command = _queue[0];
                    _queue.RemoveAt(0);
                }

                // Denon and friends ignore commands that arrive back to back, so a burst is
                // spaced out here rather than at the call site.
                int wait = _minCommandIntervalMs - unchecked(Environment.TickCount - _lastSentTicks);
                if (wait > 0)
                {
                    Thread.Sleep(Math.Min(wait, _minCommandIntervalMs));
                }

                if (!Write(client, command.Text))
                {
                    return;
                }
            }
        }

        private void Heartbeat()
        {
            if (string.IsNullOrEmpty(HeartbeatCommand) || CurrentClient == null)
            {
                return;
            }

            if (unchecked(Environment.TickCount - _lastHeartbeatTicks) < HeartbeatIntervalMs)
            {
                return;
            }

            _lastHeartbeatTicks = Environment.TickCount;
            Send(HeartbeatCommand, "heartbeat");
        }

        private bool Write(TcpClient client, string text)
        {
            try
            {
                byte[] bytes = Encoding.ASCII.GetBytes(text + _terminator);
                client.GetStream().Write(bytes, 0, bytes.Length);
                _lastSentTicks = Environment.TickCount;
                return true;
            }
            catch (Exception ex)
            {
                CloseClient(client, Describe(ex));
                return false;
            }
        }

        private void CloseClient(string detail)
        {
            CloseClient(CurrentClient, detail);
        }

        private void CloseClient(TcpClient client, string detail)
        {
            if (client == null)
            {
                return;
            }

            lock (_gate)
            {
                if (!ReferenceEquals(_client, client))
                {
                    // Someone else already tore this session down.
                    return;
                }

                _client = null;

                // Whatever was still queued was aimed at a session that no longer exists; a stale
                // absolute volume must not be replayed minutes later against a different state.
                _queue.Clear();
            }

            try
            {
                client.Close();
            }
            catch
            {
                // Nothing useful to do with a socket we are throwing away.
            }

            if (!_stopping)
            {
                SetState(AvrLinkState.Disconnected, detail);
                _wake.Set();
            }
        }

        private void SetState(AvrLinkState state, string detail)
        {
            lock (_gate)
            {
                _state = state;
            }

            LinkChanged?.Invoke(this, new LinkEventArgs(state, detail));
        }

        private static string Describe(Exception ex)
        {
            SocketException socketError = ex as SocketException ?? ex.InnerException as SocketException;
            return socketError != null ? socketError.Message : ex.Message;
        }

        private struct Command
        {
            public Command(string text, string coalesceKey)
            {
                Text = text;
                CoalesceKey = coalesceKey;
            }

            public string Text { get; }

            public string CoalesceKey { get; }
        }
    }
}
