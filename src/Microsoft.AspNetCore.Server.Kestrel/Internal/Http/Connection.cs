// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Channels;
using Microsoft.AspNetCore.Server.Kestrel.Filter;
using Microsoft.AspNetCore.Server.Kestrel.Filter.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Internal.Http
{
    public class Connection : ConnectionContext, IConnectionControl
    {
        // Base32 encoding - in ascii sort order for easy text based sorting
        private static readonly string _encode32Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

        private static readonly Action<UvStreamHandle, int, object> _readCallback =
            (handle, status, state) => ReadCallback(handle, status, state);
        private static readonly Func<UvStreamHandle, int, object, Libuv.uv_buf_t> _allocCallback =
            (handle, suggestedsize, state) => AllocCallback(handle, suggestedsize, state);
        private static readonly Action<UvWriteReq, int, Exception, object> _writeCallback = WriteCallback;

        // Seed the _lastConnectionId for this application instance with
        // the number of 100-nanosecond intervals that have elapsed since 12:00:00 midnight, January 1, 0001
        // for a roughly increasing _requestId over restarts
        private static long _lastConnectionId = DateTime.UtcNow.Ticks;

        private readonly UvStreamHandle _socket;
        private readonly Frame _frame;
        private ConnectionFilterContext _filterContext;
        private LibuvStream _libuvStream;
        //private FilteredStreamAdapter _filteredStreamAdapter;
        //private Task _readInputTask;

        private readonly Queue<PreservedBuffer> _outgoing = new Queue<PreservedBuffer>(1);

        private TaskCompletionSource<object> _socketClosedTcs = new TaskCompletionSource<object>();
        private BufferSizeControl _bufferSizeControl;
        private TaskCompletionSource<object> _drainWrites;

        private long _lastTimestamp;
        private long _timeoutTimestamp = long.MaxValue;
        private TimeoutAction _timeoutAction;
        private WritableBuffer _writableBuffer;
        private Task _sendingTask;

        public Connection(ListenerContext context, UvStreamHandle socket) : base(context)
        {
            _socket = socket;
            socket.Connection = this;
            ConnectionControl = this;

            ConnectionId = GenerateConnectionId(Interlocked.Increment(ref _lastConnectionId));

            if (ServerOptions.Limits.MaxRequestBufferSize.HasValue)
            {
                _bufferSizeControl = new BufferSizeControl(ServerOptions.Limits.MaxRequestBufferSize.Value, this, Thread);
            }

            //SocketInput = new SocketInput(Thread.Memory, ThreadPool, _bufferSizeControl);
            //SocketOutput = new SocketOutput(Thread, _socket, this, ConnectionId, Log, ThreadPool);
            Input = Thread.ChannelFactory.CreateChannel();
            Output = Thread.ChannelFactory.CreateChannel();

            var tcpHandle = _socket as UvTcpHandle;
            if (tcpHandle != null)
            {
                RemoteEndPoint = tcpHandle.GetPeerIPEndPoint();
                LocalEndPoint = tcpHandle.GetSockIPEndPoint();
            }

            _frame = FrameFactory(this);
            _lastTimestamp = Thread.Loop.Now();
        }

        // Internal for testing
        internal Connection()
        {
        }

        public KestrelServerOptions ServerOptions => ListenerContext.ServiceContext.ServerOptions;
        private Func<ConnectionContext, Frame> FrameFactory => ListenerContext.ServiceContext.FrameFactory;
        private IKestrelTrace Log => ListenerContext.ServiceContext.Log;
        private IThreadPool ThreadPool => ListenerContext.ServiceContext.ThreadPool;
        private ServerAddress ServerAddress => ListenerContext.ServerAddress;
        private KestrelThread Thread => ListenerContext.Thread;

        public void Start()
        {
            Log.ConnectionStart(ConnectionId);

            // Start socket prior to applying the ConnectionFilter
            _socket.ReadStart(_allocCallback, _readCallback, this);

            _sendingTask = ProcessWrites();
            if (ServerOptions.ConnectionFilter == null)
            {
                _frame.Start();
            }
            else
            {
                _libuvStream = new LibuvStream(Input, Output);

                _filterContext = new ConnectionFilterContext
                {
                    Connection = _libuvStream,
                    Address = ServerAddress
                };

                try
                {
                    ServerOptions.ConnectionFilter.OnConnectionAsync(_filterContext).ContinueWith((task, state) =>
                    {
                        var connection = (Connection)state;

                        if (task.IsFaulted)
                        {
                            connection.Log.LogError(0, task.Exception, "ConnectionFilter.OnConnection");
                            connection.ConnectionControl.End(ProduceEndType.SocketDisconnect);
                        }
                        else if (task.IsCanceled)
                        {
                            connection.Log.LogError("ConnectionFilter.OnConnection Canceled");
                            connection.ConnectionControl.End(ProduceEndType.SocketDisconnect);
                        }
                        else
                        {
                            connection.ApplyConnectionFilter();
                        }
                    }, this);
                }
                catch (Exception ex)
                {
                    Log.LogError(0, ex, "ConnectionFilter.OnConnection");
                    ConnectionControl.End(ProduceEndType.SocketDisconnect);
                }
            }
        }

        public Task StopAsync()
        {
            _frame.Stop();
            // _frame.SocketInput.CompleteAwaiting();
            _frame.Input.CompleteWriter();

            return _socketClosedTcs.Task;
        }

        public virtual void Abort(Exception error = null)
        {
            // Frame.Abort calls user code while this method is always
            // called from a libuv thread.
            ThreadPool.Run(() =>
            {
                _frame.Abort(error);
            });
        }

        // Called on Libuv thread
        public virtual void OnSocketClosed()
        {
            //if (_filteredStreamAdapter != null)
            //{
            //    _readInputTask.ContinueWith((task, state) =>
            //    {
            //        var connection = (Connection)state;
            //        connection._filterContext.Connection.Dispose();
            //        connection._filteredStreamAdapter.Dispose();
            //    }, this);
            //}

            Input.CompleteWriter();
            Input.CompleteReader();
            _socketClosedTcs.TrySetResult(null);
        }

        // Called on Libuv thread
        public void Tick(long timestamp)
        {
            if (timestamp > Interlocked.Read(ref _timeoutTimestamp))
            {
                ConnectionControl.CancelTimeout();

                if (_timeoutAction == TimeoutAction.SendTimeoutResponse)
                {
                    _frame.SetBadRequestState(RequestRejectionReason.RequestTimeout);
                }

                StopAsync();
            }

            Interlocked.Exchange(ref _lastTimestamp, timestamp);
        }

        private async Task ProcessWrites()
        {
            try
            {
                while (true)
                {
                    var result = await Output.ReadAsync();
                    var buffer = result.Buffer;

                    try
                    {
                        // Make sure we're on the libuv thread
                        await Thread;

                        if (buffer.IsEmpty && result.IsCompleted)
                        {
                            break;
                        }

                        if (!buffer.IsEmpty)
                        {
                            var writeReq = Thread.WriteReqPool.Allocate();
                            writeReq.Write(this._socket, buffer, _writeCallback, this);

                            // Preserve this buffer for disposal after the write completes
                            _outgoing.Enqueue(buffer.Preserve());
                        }
                    }
                    finally
                    {
                        Output.Advance(buffer.End);
                    }
                }
            }
            catch (Exception ex)
            {
                Output.CompleteReader(ex);
            }
            finally
            {
                Output.CompleteReader();

                // Drain the pending writes
                if (_outgoing.Count > 0)
                {
                    _drainWrites = new TaskCompletionSource<object>();

                    await _drainWrites.Task;
                }

                _socket.Dispose();

                // We'll never call the callback after disposing the handle
                Input.CompleteWriter();
            }
        }

        private static void WriteCallback(UvWriteReq req, int status, Exception ex, object state)
        {
            var connection = ((Connection)state);

            var buffer = connection._outgoing.Dequeue();

            // Dispose the preserved buffer
            buffer.Dispose();

            // Return the WriteReq
            connection.Thread.WriteReqPool.Return(req);

            if (connection._drainWrites != null)
            {
                if (connection._outgoing.Count == 0)
                {
                    connection._drainWrites.TrySetResult(null);
                }
            }
        }

        private void ApplyConnectionFilter(){
        //    if (_filterContext.Connection != _libuvStream)
        //    {
        //        _filteredStreamAdapter = new FilteredStreamAdapter(ConnectionId, _filterContext.Connection, Thread.Memory, Log, ThreadPool, _bufferSizeControl);

        //        _frame.SocketInput = _filteredStreamAdapter.SocketInput;
        //        _frame.SocketOutput = _filteredStreamAdapter.SocketOutput;

        //        _readInputTask = _filteredStreamAdapter.ReadInputAsync();
        //    }

            _frame.PrepareRequest = _filterContext.PrepareRequest;

            _frame.Start();
        }

        private static Libuv.uv_buf_t AllocCallback(UvStreamHandle handle, int suggestedSize, object state)
        {
            return ((Connection)state).OnAlloc(handle, suggestedSize);
        }

        private unsafe Libuv.uv_buf_t OnAlloc(UvStreamHandle handle, int suggestedSize)
        {
            _writableBuffer = Input.Alloc(4096);

            void* pointer;
            var success = _writableBuffer.Memory.TryGetPointer(out pointer);
            Debug.Assert(success);

            return handle.Libuv.buf_init((IntPtr)pointer, _writableBuffer.Memory.Length);
        }

        private static void ReadCallback(UvStreamHandle handle, int status, object state)
        {
            ((Connection)state).OnRead(handle, status);
        }

        private void OnRead(UvStreamHandle handle, int status)
        {
            if (status == 0)
            {
                // A zero status does not indicate an error or connection end. It indicates
                // there is no data to be read right now.
                // See the note at http://docs.libuv.org/en/v1.x/stream.html#c.uv_read_cb.
                // We need to clean up whatever was allocated by OnAlloc.
                //Input.
                _writableBuffer.FlushAsync();
                return;
            }

            var normalRead = status > 0;
            var normalDone = status == Constants.EOF;
            var errorDone = !(normalDone || normalRead);
            var readCount = normalRead ? status : 0;

            if (normalRead)
            {
                Log.ConnectionRead(ConnectionId, readCount);
            }
            else
            {
                _socket.ReadStop();

                if (normalDone)
                {
                    Log.ConnectionReadFin(ConnectionId);
                }
            }

            IOException error = null;
            if (errorDone)
            {
                Exception uvError;
                handle.Libuv.Check(status, out uvError);
                Log.ConnectionError(ConnectionId, uvError);
                error = new IOException(uvError.Message, uvError);

                Input.CompleteWriter(error);
            }
            if (readCount == 0 || Input.Writing.IsCompleted)
            {
                Input.CompleteWriter();
            }
            else
            {
                _writableBuffer.Advance(readCount);

                var task = _writableBuffer.FlushAsync();

                if (!task.IsCompleted)
                {
                    // If there's back pressure
                    handle.ReadStop();

                    // Resume reading when task continues
                    task.ContinueWith((t, state) => ((Connection)state).StartReading(), this);
                }
            }

            if (errorDone)
            {
                Abort(error);
            }
        }

        private void StartReading()
        {
            _socket.ReadStart(_allocCallback, _readCallback, this);
        }

        void IConnectionControl.Pause()
        {
            Log.ConnectionPause(ConnectionId);
            _socket.ReadStop();
        }

        void IConnectionControl.Resume()
        {
            Log.ConnectionResume(ConnectionId);
            try
            {
                _socket.ReadStart(_allocCallback, _readCallback, this);
            }
            catch (UvException)
            {
                // ReadStart() can throw a UvException in some cases (e.g. socket is no longer connected).
                // This should be treated the same as OnRead() seeing a "normalDone" condition.
                Log.ConnectionReadFin(ConnectionId);
                Input.CompleteWriter();
            }
        }

        void IConnectionControl.End(ProduceEndType endType)
        {
            switch (endType)
            {
                case ProduceEndType.ConnectionKeepAlive:
                    Log.ConnectionKeepAlive(ConnectionId);
                    break;
                case ProduceEndType.SocketShutdown:
                case ProduceEndType.SocketDisconnect:
                    Log.ConnectionDisconnect(ConnectionId);
                    Output.CompleteWriter();
                    //((SocketOutput)SocketOutput).End(endType);
                    break;
            }
        }

        void IConnectionControl.SetTimeout(long milliseconds, TimeoutAction timeoutAction)
        {
            Debug.Assert(_timeoutTimestamp == long.MaxValue, "Concurrent timeouts are not supported");

            AssignTimeout(milliseconds, timeoutAction);
        }

        void IConnectionControl.ResetTimeout(long milliseconds, TimeoutAction timeoutAction)
        {
            AssignTimeout(milliseconds, timeoutAction);
        }

        void IConnectionControl.CancelTimeout()
        {
            Interlocked.Exchange(ref _timeoutTimestamp, long.MaxValue);
        }

        private void AssignTimeout(long milliseconds, TimeoutAction timeoutAction)
        {
            _timeoutAction = timeoutAction;

            // Add KestrelThread.HeartbeatMilliseconds extra milliseconds since this can be called right before the next heartbeat.
            Interlocked.Exchange(ref _timeoutTimestamp, _lastTimestamp + milliseconds + KestrelThread.HeartbeatMilliseconds);
        }

        private static unsafe string GenerateConnectionId(long id)
        {
            // The following routine is ~310% faster than calling long.ToString() on x64
            // and ~600% faster than calling long.ToString() on x86 in tight loops of 1 million+ iterations
            // See: https://github.com/aspnet/Hosting/pull/385

            // stackalloc to allocate array on stack rather than heap
            char* charBuffer = stackalloc char[13];

            charBuffer[0] = _encode32Chars[(int)(id >> 60) & 31];
            charBuffer[1] = _encode32Chars[(int)(id >> 55) & 31];
            charBuffer[2] = _encode32Chars[(int)(id >> 50) & 31];
            charBuffer[3] = _encode32Chars[(int)(id >> 45) & 31];
            charBuffer[4] = _encode32Chars[(int)(id >> 40) & 31];
            charBuffer[5] = _encode32Chars[(int)(id >> 35) & 31];
            charBuffer[6] = _encode32Chars[(int)(id >> 30) & 31];
            charBuffer[7] = _encode32Chars[(int)(id >> 25) & 31];
            charBuffer[8] = _encode32Chars[(int)(id >> 20) & 31];
            charBuffer[9] = _encode32Chars[(int)(id >> 15) & 31];
            charBuffer[10] = _encode32Chars[(int)(id >> 10) & 31];
            charBuffer[11] = _encode32Chars[(int)(id >> 5) & 31];
            charBuffer[12] = _encode32Chars[(int)id & 31];

            // string ctor overload that takes char*
            return new string(charBuffer, 0, 13);
        }
    }
}
