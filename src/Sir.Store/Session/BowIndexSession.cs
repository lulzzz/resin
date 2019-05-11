﻿using Sir.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sir.Store
{
    public class BowIndexSession : CollectionSession, IDisposable, ILogger
    {
        private readonly IConfigurationProvider _config;
        private readonly ReadSession _readSession;
        private readonly ITokenizer _tokenizer;
        private readonly ProducerConsumerQueue<(long docId, long keyId, SortedList<long, int> vector)> _indexWriter;
        private readonly SortedList<long, VectorNode> _newColumns;
        private readonly Stream _documentVectorStream;
        private readonly object _writeSync = new object();

        public BowIndexSession(
            string collectionName,
            ulong collectionId,
            SessionFactory sessionFactory,
            IConfigurationProvider config,
            ITokenizer tokenizer
            ) : base(collectionName, collectionId, sessionFactory)
        {
            _config = config;
            _readSession = new ReadSession(collectionName, collectionId, sessionFactory, config);
            _tokenizer = tokenizer;

            _indexWriter = new ProducerConsumerQueue<(long docId, long keyId, SortedList<long, int> vector)>(
                int.Parse(_config.Get("write_thread_count")), WriteToMemIndex);

            _newColumns = new SortedList<long, VectorNode>();

            var docVecFileName = Path.Combine(SessionFactory.Dir, CollectionId + ".vec1");

            _documentVectorStream = SessionFactory.CreateAppendStream(docVecFileName);
        }

        public void Write(IEnumerable<IDictionary> documents, params long[] excludeKeyIds)
        {
            foreach (var doc in documents)
            {
                var docId = (long)doc["___docid"];

                foreach (var key in doc.Keys)
                {
                    var strKey = key.ToString();

                    if (strKey.StartsWith("__"))
                    {
                        continue;
                    }

                    var keyId = SessionFactory.GetKeyId(CollectionId, strKey.ToHash());

                    if (excludeKeyIds.Contains(keyId))
                    {
                        continue;
                    }

                    var treeReader = _readSession.CreateIndexReader(keyId);
                    var docVec = CreateDocumentVector(doc[key].ToString(), treeReader, _tokenizer);

                    _indexWriter.Enqueue((docId, keyId, docVec));
                }
            }
        }

        private void WriteToMemIndex((long docId, long keyId, SortedList<long, int> vector) item)
        {
            throw new NotImplementedException();
        }

        private static SortedList<long, int> CreateDocumentVector(
            string value, NodeReader reader, ITokenizer tokenizer)
        {
            var terms = tokenizer.Tokenize(value);

            return VectorOperations.CreateDocumentVector(terms.Embeddings, Similarity.Document, reader, tokenizer);
        }

        private void Flush()
        {
            throw new NotImplementedException();

            this.Log("***FLUSHED*** and completed building of model {0}", CollectionId);
        }

        public void Dispose()
        {
            _readSession.Dispose();

            Flush();
        }
    }
}