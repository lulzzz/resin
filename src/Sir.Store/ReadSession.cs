﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sir.Store
{
    /// <summary>
    /// Create a read session targetting a single collection ("table").
    /// </summary>
    public class ReadSession : CollectionSession
    {
        private readonly DocIndexReader _docIx;
        private readonly DocReader _docs;
        private readonly ValueIndexReader _keyIx;
        private readonly ValueIndexReader _valIx;
        private readonly ValueReader _keyReader;
        private readonly ValueReader _valReader;
        private readonly PagedPostingsReader _postingsReader;

        public ReadSession(ulong collectionId, LocalStorageSessionFactory sessionFactory) 
            : base(collectionId, sessionFactory)
        {
            ValueStream = sessionFactory.ValueStream;
            KeyStream = sessionFactory.CreateReadWriteStream(Path.Combine(sessionFactory.Dir, string.Format("{0}.key", collectionId)));
            DocStream = sessionFactory.CreateReadWriteStream(Path.Combine(sessionFactory.Dir, string.Format("{0}.docs", collectionId)));
            ValueIndexStream = sessionFactory.ValueIndexStream;
            KeyIndexStream = sessionFactory.CreateReadWriteStream(Path.Combine(sessionFactory.Dir, string.Format("{0}.kix", collectionId)));
            DocIndexStream = sessionFactory.CreateReadWriteStream(Path.Combine(sessionFactory.Dir, string.Format("{0}.dix", collectionId)));
            PostingsStream = sessionFactory.CreateReadWriteStream(Path.Combine(sessionFactory.Dir, string.Format("{0}.pos", collectionId)));
            VectorStream = sessionFactory.CreateReadWriteStream(Path.Combine(sessionFactory.Dir, string.Format("{0}.vec", collectionId)));
            Index = sessionFactory.GetIndex(collectionId);

            _docIx = new DocIndexReader(DocIndexStream);
            _docs = new DocReader(DocStream);
            _keyIx = new ValueIndexReader(KeyIndexStream);
            _valIx = new ValueIndexReader(ValueIndexStream);
            _keyReader = new ValueReader(KeyStream);
            _valReader = new ValueReader(ValueStream);
            _postingsReader = new PagedPostingsReader(PostingsStream);
        }

        public IEnumerable<IDictionary> Read(Query query)
        {
            SortedList<ulong, double> result = null;

            while (query != null)
            {
                var keyHash = query.Term.Key.ToString().ToHash();
                var ix = GetIndex(keyHash);

                if (ix != null)
                {
                    var matches = ix.Match(query.Term.Value.ToString()).ToList();

                    foreach (var match in matches)
                    {
                        if (match.Highscore > VectorNode.FalseAngle)
                        {
                            var docIds = _postingsReader.Read(match.PostingsOffset)
                                .ToDictionary(x => x, y => match.Highscore);

                            if (result == null)
                            {
                                result = new SortedList<ulong, double>(docIds);
                            }
                            else
                            {
                                if (query.And)
                                {
                                    var subset = (from doc in result
                                                  join x in docIds on doc.Key equals x.Key
                                                  select doc).ToDictionary(x => x.Key, y => y.Value);

                                    result = new SortedList<ulong, double>(subset);

                                }
                                else if (query.Not)
                                {
                                    foreach (var id in docIds.Keys)
                                    {
                                        result.Remove(id);
                                    }
                                }
                                else // Or
                                {
                                    foreach (var id in docIds)
                                    {
                                        double score;

                                        if (result.TryGetValue(id.Key, out score))
                                        {
                                            result[id.Key] = Math.Max(score, id.Value) + ((score+id.Value)/2);
                                        }
                                        else
                                        {
                                            result.Add(id.Key, id.Value);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    query = query.Next;
                }
            }

            if (result == null)
            {
                return Enumerable.Empty<IDictionary>();
            }
            else
            {
                return ReadDocs(result).OrderByDescending(d => d["_score"]);
            }
        }

        private IEnumerable<IDictionary> ReadDocs(IEnumerable<ulong> docIds)
        {
            foreach (var id in docIds)
            {
                var docInfo = _docIx.Read(id);
                var docMap = _docs.Read(docInfo.offset, docInfo.length);
                var doc = new Dictionary<IComparable, IComparable>();

                for (int i = 0; i < docMap.Count; i++)
                {
                    var kvp = docMap[i];
                    var kInfo = _keyIx.Read(kvp.keyId);
                    var vInfo = _valIx.Read(kvp.valId);
                    var key = _keyReader.Read(kInfo.offset, kInfo.len, kInfo.dataType);
                    var val = _valReader.Read(vInfo.offset, vInfo.len, vInfo.dataType);

                    doc[key] = val;
                }

                doc["_docid"] = id;

                yield return doc;
            }
        }

        private IEnumerable<IDictionary> ReadDocs(IDictionary<ulong, double> docs)
        {
            foreach (var d in docs)
            {
                var docInfo = _docIx.Read(d.Key);
                var docMap = _docs.Read(docInfo.offset, docInfo.length);
                var doc = new Dictionary<IComparable, IComparable>();

                for (int i = 0; i < docMap.Count; i++)
                {
                    var kvp = docMap[i];
                    var kInfo = _keyIx.Read(kvp.keyId);
                    var vInfo = _valIx.Read(kvp.valId);
                    var key = _keyReader.Read(kInfo.offset, kInfo.len, kInfo.dataType);
                    var val = _valReader.Read(vInfo.offset, vInfo.len, vInfo.dataType);

                    doc[key] = val;
                }

                doc["_docid"] = d.Key;
                doc["_score"] = d.Value;

                yield return doc;
            }
        }
    }
}
