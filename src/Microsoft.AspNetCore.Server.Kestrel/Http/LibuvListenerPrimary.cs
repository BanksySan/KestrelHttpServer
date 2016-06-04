// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Networking;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Http
{
    /// <summary>
    /// A primary listener waits for incoming connections on a specified socket. Incoming
    /// connections may be passed to a secondary listener to handle.
    /// </summary>
    public abstract class LibuvListenerPrimary : LibuvListener
    {
        private readonly List<UvPipeHandle> _dispatchPipes = new List<UvPipeHandle>();
        private int _dispatchIndex;
        private string _pipeName;
        private IntPtr _fileCompletionInfoPtr;
        private bool _tryDetachFromIOCP = PlatformApis.IsWindows;

        // this message is passed to write2 because it must be non-zero-length,
        // but it has no other functional significance
        private readonly ArraySegment<ArraySegment<byte>> _dummyMessage = new ArraySegment<ArraySegment<byte>>(new[] { new ArraySegment<byte>(new byte[] { 1, 2, 3, 4 }) });

        protected LibuvListenerPrimary(ServiceContext serviceContext) : base(serviceContext)
        {
        }

        private UvPipeHandle ListenPipe { get; set; }

        public async Task StartAsync(
            string pipeName,
            ServerAddress address,
            LibuvThread thread)
        {
            _pipeName = pipeName;

            if (_fileCompletionInfoPtr == IntPtr.Zero)
            {
                var fileCompletionInfo = new FILE_COMPLETION_INFORMATION() { Key = IntPtr.Zero, Port = IntPtr.Zero };
                _fileCompletionInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(fileCompletionInfo));
                Marshal.StructureToPtr(fileCompletionInfo, _fileCompletionInfoPtr, false);
            }

            await StartAsync(address, thread);

            await UvThread;

            ListenPipe = new UvPipeHandle(Log);
            ListenPipe.Init(UvThread.Loop, UvThread.QueueCloseHandle, false);
            ListenPipe.Bind(_pipeName);
            ListenPipe.Listen(Constants.ListenBacklog,
                (pipe, status, error, state) => ((LibuvListenerPrimary)state).OnListenPipe(pipe, status, error), this);

            await ThreadPool;
        }

        private void OnListenPipe(UvStreamHandle pipe, int status, Exception error)
        {
            if (status < 0)
            {
                return;
            }

            var dispatchPipe = new UvPipeHandle(Log);
            dispatchPipe.Init(UvThread.Loop, UvThread.QueueCloseHandle, true);

            try
            {
                pipe.Accept(dispatchPipe);
            }
            catch (UvException ex)
            {
                dispatchPipe.Dispose();
                Log.LogError(0, ex, "ListenerPrimary.OnListenPipe");
                return;
            }

            _dispatchPipes.Add(dispatchPipe);
        }

        protected override void DispatchConnection(UvStreamHandle socket)
        {
            var index = _dispatchIndex++ % (_dispatchPipes.Count + 1);
            if (index == _dispatchPipes.Count)
            {
                base.DispatchConnection(socket);
            }
            else
            {
                DetachFromIOCP(socket);
                var dispatchPipe = _dispatchPipes[index];
                var write = new UvWriteReq(Log);
                write.Init(UvThread.Loop);
                write.Write2(
                    dispatchPipe,
                    _dummyMessage,
                    socket,
                    (write2, status, state) =>
                    {
                        write2.Dispose();
                        ((UvStreamHandle)state).Dispose();
                    },
                    socket);
            }
        }

        private void DetachFromIOCP(UvHandle handle)
        {
            if (!_tryDetachFromIOCP)
            {
                return;
            }

            // https://msdn.microsoft.com/en-us/library/windows/hardware/ff728840(v=vs.85).aspx
            const int FileReplaceCompletionInformation = 61;
            // https://msdn.microsoft.com/en-us/library/cc704588.aspx
            const uint STATUS_INVALID_INFO_CLASS = 0xC0000003;

            var statusBlock = new IO_STATUS_BLOCK();
            var socket = IntPtr.Zero;
            UvThread.Loop.Libuv.uv_fileno(handle, ref socket);

            if (NtSetInformationFile(socket, out statusBlock, _fileCompletionInfoPtr,
                (uint)Marshal.SizeOf<FILE_COMPLETION_INFORMATION>(), FileReplaceCompletionInformation) == STATUS_INVALID_INFO_CLASS)
            {
                // Replacing IOCP information is only supported on Windows 8.1 or newer
                _tryDetachFromIOCP = false;
            }
        }

        private struct IO_STATUS_BLOCK
        {
            uint status;
            ulong information;
        }

        private struct FILE_COMPLETION_INFORMATION
        {
            public IntPtr Port;
            public IntPtr Key;
        }

        [DllImport("NtDll.dll")]
        private static extern uint NtSetInformationFile(IntPtr FileHandle,
                out IO_STATUS_BLOCK IoStatusBlock, IntPtr FileInformation, uint Length,
                int FileInformationClass);

        public override async Task DisposeAsync()
        {
            // Call base first so the ListenSocket gets closed and doesn't
            // try to dispatch connections to closed pipes.
            await base.DisposeAsync().ConfigureAwait(false);

            if (_fileCompletionInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_fileCompletionInfoPtr);
                _fileCompletionInfoPtr = IntPtr.Zero;
            }

            if (UvThread.FatalError == null && ListenPipe != null)
            {
                await UvThread;

                ListenPipe.Dispose();

                foreach (var dispatchPipe in _dispatchPipes)
                {
                    dispatchPipe.Dispose();
                }

                await ThreadPool;
            }
        }
    }
}