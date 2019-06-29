﻿using System;
using System.Collections.Generic;

namespace Sir.Store
{
    /// <summary>
    /// Write session targeting a single collection.
    /// </summary>
    public class WriteSession : CollectionSession, ILogger, IDisposable
    {
        private readonly TermIndexSession _indexSession;
        private readonly CollectionStreamWriter _streamWriter;

        public WriteSession(
            string collectionName,
            ulong collectionId,
            SessionFactory sessionFactory,
            TermIndexSession indexSession,
            CollectionStreamWriter streamWriter,
            IConfigurationProvider config) : base(collectionName, collectionId, sessionFactory)
        {
            _indexSession = indexSession;
            _streamWriter = streamWriter;
        }

        public void Dispose()
        {
            _streamWriter.Flush();
        }

        /// <summary>
        /// Fields prefixed with "___" will not be stored.
        /// </summary>
        /// <returns>Document ID</returns>
        public void Write(IDictionary<string, object> document)
        {
            document["__created"] = DateTime.Now.ToBinary();

            var docMap = new List<(long keyId, long valId)>();
            var docId = _streamWriter.GetNextDocId();

            foreach (var key in document.Keys)
            {
                var val = document[key];

                if (val == null)
                {
                    continue;
                }

                var keyStr = key.ToString();

                if (keyStr.StartsWith("___"))
                {
                    continue;
                }

                var keyHash = keyStr.ToHash();
                long keyId;

                if (!SessionFactory.TryGetKeyId(CollectionId, keyHash, out keyId))
                {
                    // We have a new key!

                    // store key
                    var keyInfo = _streamWriter.PutKey(keyStr);

                    keyId = _streamWriter.PutKeyInfo(keyInfo.offset, keyInfo.len, keyInfo.dataType);
                    SessionFactory.PersistKeyMapping(CollectionId, keyHash, keyId);
                }

                // store value
                var valInfo = _streamWriter.PutValue(val);
                var valId = _streamWriter.PutValueInfo(valInfo.offset, valInfo.len, valInfo.dataType);

                // store refs to keys and values
                docMap.Add((keyId, valId));

                // index
                if (!keyStr.StartsWith("_") && valInfo.dataType == DataType.STRING)
                {
                    _indexSession.Put(docId, keyId, (string)val);
                }
            }

            var docMeta = _streamWriter.PutDocumentMap(docMap);

            _streamWriter.PutDocumentAddress(docMeta.offset, docMeta.length);
        }
    }
}