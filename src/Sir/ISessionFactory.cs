﻿using System.IO;

namespace Sir
{
    public interface ISessionFactory
    {
        IConfigurationProvider Config { get; }
        string Dir { get; }
        Stream CreateAppendStream(string fileName, int bufferSize = 4096);
        Stream CreateAsyncAppendStream(string fileName, int bufferSize = 4096);
        Stream CreateAsyncReadStream(string fileName, int bufferSize = 4096);
        Stream CreateReadStream(string fileName, int bufferSize = 4096, FileOptions fileOptions = FileOptions.RandomAccess);
        void RegisterKeyMapping(ulong collectionId, ulong keyHash, long keyId);
        bool TryGetKeyId(ulong collectionId, ulong keyHash, out long keyId);
    }
}