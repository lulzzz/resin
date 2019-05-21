﻿using System;
using System.IO;

namespace Sir.Store
{
    /// <summary>
    /// Index segment writer.
    /// </summary>
    public class PageIndexWriter : IDisposable
    {
        private readonly Stream _stream;

        public PageIndexWriter(Stream stream)
        {
            _stream = stream;
        }

        public void Write(long offset, long length)
        {
            _stream.Write(BitConverter.GetBytes(offset));
            _stream.Write(BitConverter.GetBytes(length));
        }

        public void Flush()
        {
            _stream.Flush();
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}