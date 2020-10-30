﻿using Microsoft.Extensions.Logging;
using Sir.VectorSpace;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Sir.Search
{
    /// <summary>
    /// Indexing session targeting a single collection.
    /// </summary>
    public class IndexSession<T> : IIndexSession, IDisposable
    {
        private readonly ulong _collectionId;
        private readonly SessionFactory _sessionFactory;
        private readonly Stream _postingsStream;
        private readonly Stream _vectorStream;
        private readonly ILogger _logger;
        private readonly IModel<T> _model;
        private readonly ConcurrentDictionary<long, VectorNode> _index;
        private readonly IIndexingStrategy _indexingStrategy;
        private bool _flushing;

        /// <summary>
        /// Creates an instance of an indexing session targeting a single collection.
        /// </summary>
        /// <param name="collectionId">A hash of your collection name, e.g. "YourCollectionName".ToHash();</param>
        /// <param name="sessionFactory">A session factory</param>
        /// <param name="model">A model</param>
        /// <param name="config">A configuration provider</param>
        /// <param name="logger">A logger</param>
        public IndexSession(
            ulong collectionId,
            SessionFactory sessionFactory,
            IModel<T> model,
            IIndexingStrategy indexingStrategy,
            ILogger logger)
        {
            _collectionId = collectionId;
            _sessionFactory = sessionFactory;
            _postingsStream = sessionFactory.CreateAppendStream(Path.Combine(sessionFactory.Directory, $"{collectionId}.pos"));
            _vectorStream = sessionFactory.CreateAppendStream(Path.Combine(sessionFactory.Directory, $"{collectionId}.vec"));
            _model = model;
            _index = new ConcurrentDictionary<long, VectorNode>();
            _logger = logger;
            _indexingStrategy = indexingStrategy;
        }

        public void Put(long docId, long keyId, T value)
        {
            var vectors = _model.Tokenize(value);
            var column = _index.GetOrAdd(keyId, new VectorNode());

            foreach (var vector in vectors)
            {
                _indexingStrategy.ExecutePut(column, keyId, new VectorNode(vector, docId), _model);
            }
        }

        public VectorNode GetInMemoryIndex(long keyId)
        {
            return _index[keyId];
        }

        public IndexInfo GetIndexInfo()
        {
            return new IndexInfo(GetGraphInfo());
        }

        private IEnumerable<GraphInfo> GetGraphInfo()
        {
            foreach (var ix in _index)
            {
                yield return new GraphInfo(ix.Key, ix.Value);
            }
        }

        public void Flush()
        {
            if (_flushing)
                return;

            _flushing = true;

            foreach (var column in _index)
            {
                using (var indexStream = _sessionFactory.CreateAppendStream(Path.Combine(_sessionFactory.Directory, $"{_collectionId}.{column.Key}.ix")))
                using (var columnWriter = new ColumnStreamWriter(indexStream))
                using (var pageIndexWriter = new PageIndexWriter(_sessionFactory.CreateAppendStream(Path.Combine(_sessionFactory.Directory, $"{_collectionId}.{column.Key}.ixtp"))))
                {
                    var size = columnWriter.CreatePage(column.Value, _vectorStream, _postingsStream, pageIndexWriter);

                    _logger.LogInformation($"serialized column {column.Key}, weight {column.Value.Weight} {size}");
                }
            }

            _sessionFactory.ClearPageInfo();
        }

        public void Dispose()
        {
            if (!_flushing)
                Flush();

            _postingsStream.Dispose();
            _vectorStream.Dispose();
        }
    }
}