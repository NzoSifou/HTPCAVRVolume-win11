using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;

namespace HTPCAVRVolume.Ipc
{
    /// <summary>
    /// The background process's end of the pipe. It accepts one window at a time, answers what it
    /// is asked, and pushes the state whenever it changes so the window never has to poll.
    /// </summary>
    sealed class IpcServer : IDisposable
    {
        private readonly Func<Request, State> _handle;
        private readonly Action<Exception> _onError;
        private readonly object _gate = new object();

        private Thread _thread;
        private NamedPipeServerStream _pipe;
        private StreamWriter _writer;
        private volatile bool _stopping;

        /// <param name="handle">
        /// Runs a request and returns the state that followed. Called on the pipe thread, so it
        /// must marshal onto whatever thread owns the state.
        /// </param>
        public IpcServer(Func<Request, State> handle, Action<Exception> onError = null)
        {
            _handle = handle;
            _onError = onError;
        }

        public void Start()
        {
            _thread = new Thread(Listen) { IsBackground = true, Name = "Settings pipe" };
            _thread.Start();
        }

        /// <summary>Raised on the pipe thread once a window has gone away.</summary>
        public event EventHandler ClientLeft;

        /// <summary>Tells the window, if one is attached, that something changed.</summary>
        public void Push(State state)
        {
            lock (_gate)
            {
                if (_writer == null)
                {
                    return;
                }

                try
                {
                    _writer.WriteLine(JsonSerializer.Serialize(state, Protocol.Json));
                }
                catch
                {
                    // The window went away mid-sentence; the listener will notice and wait for
                    // the next one.
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;

            lock (_gate)
            {
                try
                {
                    _pipe?.Dispose();
                }
                catch
                {
                    // Shutting down.
                }
            }

            _thread?.Join(500);
        }

        private void Listen()
        {
            while (!_stopping)
            {
                try
                {
                    // Asynchronous, and it matters: both ends read on one thread while writing on
                    // another, and on a handle opened for synchronous I/O a blocked read stops the
                    // write from ever starting. The two sides simply wait for each other forever.
                    using NamedPipeServerStream pipe = new NamedPipeServerStream(
                        Protocol.PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    lock (_gate)
                    {
                        _pipe = pipe;
                    }

                    pipe.WaitForConnection();

                    using StreamReader reader = new StreamReader(pipe);
                    StreamWriter writer = new StreamWriter(pipe) { AutoFlush = true };

                    lock (_gate)
                    {
                        _writer = writer;
                    }

                    string line;
                    while (!_stopping && (line = reader.ReadLine()) != null)
                    {
                        Request request;
                        try
                        {
                            request = JsonSerializer.Deserialize<Request>(line, Protocol.Json);
                        }
                        catch
                        {
                            continue;
                        }

                        State state = _handle(request);
                        if (state != null)
                        {
                            lock (_gate)
                            {
                                writer.WriteLine(JsonSerializer.Serialize(state, Protocol.Json));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        _onError?.Invoke(ex);
                        Thread.Sleep(200);
                    }
                }
                finally
                {
                    bool had;
                    lock (_gate)
                    {
                        had = _writer != null;
                        _writer = null;
                        _pipe = null;
                    }

                    if (had && !_stopping)
                    {
                        ClientLeft?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
    }

    /// <summary>The window's end of the pipe.</summary>
    sealed class IpcClient : IDisposable
    {
        private readonly object _gate = new object();
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _thread;
        private volatile bool _stopping;

        /// <summary>Raised on a background thread each time the other end sends the state.</summary>
        public event EventHandler<State> StateReceived;

        /// <summary>Raised on a background thread when the pipe goes away.</summary>
        public event EventHandler Closed;

        public bool Connect(int timeoutMs = 4000)
        {
            try
            {
                // Asynchronous for the same reason as the server: this end reads on its listener
                // thread and writes from the window's thread, and those must not block each other.
                _pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                _pipe.Connect(timeoutMs);
                _reader = new StreamReader(_pipe);
                _writer = new StreamWriter(_pipe) { AutoFlush = true };

                _thread = new Thread(Listen) { IsBackground = true, Name = "Settings pipe client" };
                _thread.Start();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Send(Request request)
        {
            lock (_gate)
            {
                try
                {
                    _writer?.WriteLine(JsonSerializer.Serialize(request, Protocol.Json));
                }
                catch
                {
                    // Treated as a disconnection by the reader.
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;

            try
            {
                _pipe?.Dispose();
            }
            catch
            {
                // Closing anyway.
            }

            _thread?.Join(300);
        }

        private void Listen()
        {
            try
            {
                string line;
                while (!_stopping && (line = _reader.ReadLine()) != null)
                {
                    State state;
                    try
                    {
                        state = JsonSerializer.Deserialize<State>(line, Protocol.Json);
                    }
                    catch
                    {
                        continue;
                    }

                    if (state != null)
                    {
                        StateReceived?.Invoke(this, state);
                    }
                }
            }
            catch
            {
                // Falls through to Closed, which is all the window needs to know.
            }

            if (!_stopping)
            {
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
