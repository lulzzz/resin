﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using log4net;
using Resin.IO;

namespace Resin
{
    public class IndexWriter : IDisposable
    {
        private readonly string _directory;
        private readonly IAnalyzer _analyzer;
        private static readonly ILog Log = LogManager.GetLogger(typeof(IndexWriter));

        /// <summary>
        /// field/trie
        /// </summary>
        private readonly IDictionary<string, Trie> _trieFiles;

        /// <summary>
        /// field.token/postings
        /// </summary>
        private readonly IDictionary<string, PostingsFile> _postingsFiles;

        private readonly ConcurrentQueue<DocContainerFile> _docContainerFiles; 
            
        private readonly IxFile _ix;
        private readonly FileSystemWatcher _dfileWatcher;

        public IndexWriter(string directory, IAnalyzer analyzer)
        {
            _directory = directory;
            _analyzer = analyzer;
            _trieFiles = new Dictionary<string, Trie>();
            _postingsFiles = new Dictionary<string, PostingsFile>();
            _docContainerFiles = new ConcurrentQueue<DocContainerFile>();

            var ixFileName = Path.Combine(directory, "0.ix");
            _ix = File.Exists(ixFileName) ? IxFile.Load(ixFileName) : new IxFile();

            _dfileWatcher = new FileSystemWatcher(_directory, "*.do") {NotifyFilter = NotifyFilters.LastWrite};
            _dfileWatcher.Changed += DfileWatcherChanged;
            _dfileWatcher.EnableRaisingEvents = true;
        }

        void DfileWatcherChanged(object sender, FileSystemEventArgs e)
        {
            DocContainerFile container;
            if (!_docContainerFiles.TryDequeue(out container))
            {
                container = new DocContainerFile(Path.GetRandomFileName());
            }
            var fileName = e.FullPath;
            var file = DocFieldFile.Load(fileName);
            File.Delete(fileName);
            var key = string.Format(("{0}.{1}"), file.DocId, file.Field);
            container.Files.Add(key, file);
            _ix.DocContainers.Add(key, container.Id);

            if (container.Files.Count == 1000)
            {
                var containerFileName = Path.Combine(_directory, container.Id + ".dl");
                container.Save(containerFileName);
            }
            else
            {
                _docContainerFiles.Enqueue(container);
            }
        }

        public void Remove(string docId, string field)
        {
            var docFieldId = string.Format("{0}.{1}", docId, field);
            if (_ix.Fields[field].ContainsKey(docId))
            {
                _ix.Fields[field].Remove(docId);
                var containerFileName = Path.Combine(_directory, _ix.DocContainers[docFieldId] + ".dl");
                var container = DocContainerFile.Load(containerFileName);
                var docField = container.Files[docFieldId];
                container.Files.Remove(docFieldId);
                IEnumerable<string> tokens;
                if (field[0] == '_')
                {
                    tokens = new[] { docField.Value };
                }
                else
                {
                    tokens = _analyzer.Analyze(docField.Value);
                }
                foreach (var token in tokens)
                {
                    var fieldTokenId = string.Format("{0}.{1}", field, token);
                    var postingsFile = GetPostingsFile(field, token);
                    postingsFile.Postings.Remove(docId);
                    if (postingsFile.NumDocs() == 0)
                    {
                        var fileName = Path.Combine(_directory, fieldTokenId.ToHash() + ".po");
                        File.Delete(fileName);
                        var trie = GetTrie(field);
                        trie.Remove(token);
                    }
                }
            }  
        }

        public void Write(IDictionary<string, string> doc)
        {
            Write(new Document(doc));
        }

        public void Write(Document doc)
        {
            foreach (var field in doc.Fields)
            {
                Analyze(doc.Id, field.Key, field.Value);
                var docFileName = Path.Combine(_directory, Path.GetRandomFileName() + ".do");
                new DocFieldFile(doc.Id, field.Key, field.Value).Save(docFileName);
            }
        }

        private void Analyze(string docId, string field, string value)
        {
            var termFrequencies = new Dictionary<string, int>();
            var analyze = field[0] != '_';
            if (analyze)
            {
                foreach (var token in _analyzer.Analyze(value))
                {
                    if (termFrequencies.ContainsKey(token)) termFrequencies[token] += 1;
                    else termFrequencies.Add(token, 1);
                }
            }
            else
            {
                if (termFrequencies.ContainsKey(value)) termFrequencies[value] += 1;
                else termFrequencies.Add(value, 1);
            }
            foreach (var token in termFrequencies)
            {
                Write(docId, field, token.Key, token.Value);
            }
        }

        private void Write(string docId, string field, string token, int termFrequency)
        {
            var trie = GetTrie(field);
            trie.Add(token);
            var pf = GetPostingsFile(field, token);
            pf.Postings[docId] = termFrequency;

            if (!_ix.Fields.ContainsKey(field))
            {
                _ix.Fields.Add(field, new Dictionary<string, object>());
            }
            _ix.Fields[field][docId] = null;
        }

        private PostingsFile GetPostingsFile(string field, string token)
        {
            var fieldTokenId = string.Format("{0}.{1}", field, token);
            PostingsFile file;
            if (!_postingsFiles.TryGetValue(fieldTokenId, out file))
            {
                var fileName = Path.Combine(_directory, fieldTokenId.ToHash() + ".po");
                if (File.Exists(fileName))
                {
                    file = PostingsFile.Load(fileName);
                }
                else
                {
                    file = new PostingsFile(field);
                }
                _postingsFiles.Add(fieldTokenId, file);
            }
            return file;
        }

        private Trie GetTrie(string field)
        {
            Trie file;
            if (!_trieFiles.TryGetValue(field, out file))
            {
                var fileName = Path.Combine(_directory, field.ToHash() + ".tr");
                if (File.Exists(fileName))
                {
                    file = Trie.Load(fileName);
                }
                else
                {
                    file = new Trie();
                }
                _trieFiles.Add(field, file);
            }
            return file;
        }

        public void Dispose()
        {
            foreach (var trie in _trieFiles)
            {
                string fileName = Path.Combine(_directory, trie.Key.ToHash() + ".tr");
                trie.Value.Save(fileName);
            }
            foreach (var pf in _postingsFiles)
            {
                string fileName = Path.Combine(_directory, pf.Key.ToHash() + ".po");
                pf.Value.Save(fileName);
            }
            _dfileWatcher.Dispose();
            foreach (var container in _docContainerFiles.ToArray())
            {
                var fileName = Path.Combine(_directory, container.Id + ".dl");
                container.Save(fileName);
            }
            _ix.Save(Path.Combine(_directory, "0.ix"));
        }
    }
}