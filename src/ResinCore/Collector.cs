﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using log4net;
using Resin.IO.Read;
using Resin.Querying;
using Resin.IO;
using DocumentTable;

namespace Resin
{
    public class Collector : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Collector));
        private readonly string _directory;
        private readonly IScoringSchemeFactory _scorerFactory;
        private readonly IDictionary<Query, IList<DocumentScore>> _scoreCache;
        private readonly IReadSession _readSession;

        public Collector(string directory, IReadSession readSession, IScoringSchemeFactory scorerFactory = null)
        {
            _readSession = readSession;
            _directory = directory;
            _scorerFactory = scorerFactory;
            _scoreCache = new Dictionary<Query, IList<DocumentScore>>();
        }

        public DocumentScore[] Collect(IList<QueryContext> query)
        {
            var scoreTime = Stopwatch.StartNew();

            foreach (var clause in query)
            {
                Scan(clause);
                Score(clause);
            }

            Log.DebugFormat("scored query {0} in {1}", query, scoreTime.Elapsed);

            var reduceTime = new Stopwatch();
            reduceTime.Start();

            var reduced = query.Reduce().ToArray();

            Log.DebugFormat("reduced query {0} producing {1} scores in {2}", query, reduced.Length, scoreTime.Elapsed);

            return reduced;
        }

        private void Scan(QueryContext ctx)
        {
            var time = new Stopwatch();
            time.Start();

            if (ctx.Query is TermQuery)
            {
                TermScan(ctx);
            }
            else if (ctx.Query is PhraseQuery)
            {
                PhraseScan(ctx);
            }
            else
            {
                RangeScan(ctx);
            }

            if (Log.IsDebugEnabled && ctx.Terms.Count > 1)
            {
                Log.DebugFormat("expanded {0}: {1}",
                    ctx.Query.Value, string.Join(" ", ctx.Terms.Select(t => t.Word.Value)));
            }

            Log.DebugFormat("scanned {0} in {1}", ctx.Query.Serialize(), time.Elapsed);
        }

        private void TermScan(QueryContext ctx)
        {
            IList<Term> terms;

            using (var reader = GetTreeReader(ctx.Query.Field))
            {
                if (ctx.Query.Fuzzy)
                {
                    terms = reader.SemanticallyNear(ctx.Query.Value, ctx.Query.Edits(ctx.Query.Value))
                        .ToTerms(ctx.Query.Field);
                }
                else if (ctx.Query.Prefix)
                {
                    terms = reader.StartsWith(ctx.Query.Value)
                        .ToTerms(ctx.Query.Field);
                }
                else
                {
                    terms = reader.IsWord(ctx.Query.Value)
                        .ToTerms(ctx.Query.Field);
                }
            }
            ctx.Terms = terms;
            ctx.Postings = GetPostings(ctx);
        }

        private void PhraseScan(QueryContext ctx)
        {
            var tokens = ((PhraseQuery)ctx.Query).Values;
            var postingsMatrix = new IList<DocumentPosting>[tokens.Count];

            for (int index = 0;index < tokens.Count; index++)
            {
                var token = tokens[index];
                IList<Term> terms;

                using (var reader = GetTreeReader(ctx.Query.Field))
                {
                    if (ctx.Query.Fuzzy)
                    {
                        terms = reader.SemanticallyNear(token, ctx.Query.Edits(token))
                            .ToTerms(ctx.Query.Field);
                    }
                    else if (ctx.Query.Prefix)
                    {
                        terms = reader.StartsWith(token)
                            .ToTerms(ctx.Query.Field);
                    }
                    else
                    {
                        terms = reader.IsWord(token)
                            .ToTerms(ctx.Query.Field);
                    }
                }

                var postings = GetPostings(terms);
                postingsMatrix[index] = postings;
            }

            ctx.Postings = postingsMatrix.Sum();
        }

        private void RangeScan(QueryContext ctx)
        {
            using (var reader = GetTreeReader(ctx.Query.Field))
            {
                ctx.Terms = reader.Range(ctx.Query.Value, ((RangeQuery)ctx.Query).ValueUpperBound)
                        .ToTerms(ctx.Query.Field);
            }

            ctx.Postings = GetPostings(ctx);
        }

        private IList<DocumentPosting> GetPostings(QueryContext query)
        {
            var time = Stopwatch.StartNew();

            var postings = GetPostings(query.Terms);

            Log.DebugFormat("read postings for {0} in {1}", query.Query.Serialize(), time.Elapsed);

            return postings;
        }

        private IList<DocumentPosting> GetPostings(IList<Term> terms)
        {
            var postings = terms.Count > 0 ? _readSession.ReadPostings(terms) : null;

            IList<DocumentPosting> reduced;

            if (postings == null)
            {
                reduced = new DocumentPosting[0];
            }
            else
            {
                reduced = postings.Sum();
            }

            return reduced;
        }

        private void Score(QueryContext ctx)
        {
            ctx.Scored = Score(ctx.Postings);
        }

        private IList<DocumentScore> Score(IList<DocumentPosting> postings)
        {
            var scores = new List<DocumentScore>(postings.Count);

            if (postings != null)
            {
                if (_scorerFactory == null)
                {
                    foreach (var posting in postings)
                    {
                        var docHash = _readSession.ReadDocHash(posting.DocumentId);

                        if (!docHash.IsObsolete)
                        {
                            scores.Add(new DocumentScore(posting.DocumentId, docHash.Hash, 0, _readSession.Version));
                        }
                    }
                }
                else
                {
                    if (postings.Any())
                    {
                        var docsWithTerm = postings.Count;
                        var scorer = _scorerFactory.CreateScorer(_readSession.Version.DocumentCount, docsWithTerm);

                        foreach (var posting in postings)
                        {
                            var docHash = _readSession.ReadDocHash(posting.DocumentId);

                            if (!docHash.IsObsolete)
                            {
                                var score = scorer.Score(posting);

                                scores.Add(new DocumentScore(posting.DocumentId, docHash.Hash, score, _readSession.Version));
                            }
                        }
                    }
                }
            }
            return scores;

        }

        private ITrieReader GetTreeReader(string field)
        {
            var key = field.ToHash();
            long offset;

            if (_readSession.Version.FieldOffsets.TryGetValue(key, out offset))
            {
                _readSession.Stream.Seek(offset, SeekOrigin.Begin);
                return new MappedTrieReader(_readSession.Stream);
            }
            return null;
        }

        public void Dispose()
        {
        }
    }
}