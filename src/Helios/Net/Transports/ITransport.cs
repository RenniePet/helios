﻿using System.Threading;
using System.Threading.Tasks;

namespace Helios.Net.Transports
{
    /// <summary>
    /// Interface used to place an I/O interface
    /// on top of a connection
    /// </summary>
    public interface ITransport
    {
        bool Peek();

        int Read(byte[] buffer, int offset, int length);

#if !NET35 && !NET40

        Task<int> ReadAsync(byte[] buffer, int offset, int length);

        Task<int> ReadAsync(byte[] buffer, int offset, int length, CancellationToken token);
#endif

        int ReadAll(byte[] buffer, int offset, int length);

#if !NET35 && !NET40
        Task<int> ReadAllAsync(byte[] buffer, int offset, int length);

        Task<int> ReadAllAsync(byte[] buffer, int offset, int length, CancellationToken token);
#endif

        void Write(byte[] buffer);

#if !NET35 && !NET40
        Task WriteAsync(byte[] buffer);

        Task WriteAsync(byte[] buffer, CancellationToken token);

        Task WriteAsync(byte[] buffer, int offset, int length);

        Task WriteAsync(byte[] buffer, int offset, int length, CancellationToken token);
#endif

        void Write(byte[] buffer, int offset, int length);

        void Flush();

#if !NET35 && !NET40
        Task FlushAsync();

        Task FlushAsync(CancellationToken token);
#endif
    }
}
