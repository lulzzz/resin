﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Sir.Store
{
    /// <summary>
    /// Index bitmap reader. Each block is a <see cref="Sir.Store.VectorNode"/>.
    /// </summary>
    public class NodeReader : ILogger
    {
        private readonly SessionFactory _sessionFactory;
        private readonly IConfigurationProvider _config;
        private readonly string _ixpFileName;
        private readonly string _vixpFileName;
        private readonly string _ixMapName;
        private readonly string _ixFileName;
        private readonly string _vecFileName;
        private long _optimizedOffset;

        public NodeReader(
            string ixFileName,
            string ixpFileName,
            string vecFileName,
            string vecixpFileName,
            SessionFactory sessionFactory,
            IConfigurationProvider config)
        {
            _vecFileName = vecFileName;
            _ixFileName = ixFileName;
            _sessionFactory = sessionFactory;
            _config = config;
            _ixpFileName = ixpFileName;
            _vixpFileName = vecixpFileName;
            _ixMapName = _ixFileName.Replace(":", "").Replace("\\", "_");
        }

        public VectorNode Optimized((float identicalAngle, float foldAngle) similarity)
        {
            return Optimize(similarity);
        }

        private VectorNode Optimize((float identicalAngle, float foldAngle) similarity)
        {
            var time = Stopwatch.StartNew();
            var root = new VectorNode();
            var pages = _sessionFactory.ReadPageInfoFromDisk(_ixpFileName);
            var bufferSize = int.Parse(_config.Get("read_buffer_size") ?? "4096");

            using (var vectorStream = _sessionFactory.CreateReadStream(_vecFileName))
            using (var ixStream = new BufferedStream(_sessionFactory.CreateReadStream(_ixFileName), bufferSize))
            {
                ixStream.Seek(_optimizedOffset, SeekOrigin.Begin);

                foreach (var page in pages)
                {
                    var offset = page.offset + VectorNode.BlockSize;
                    Span<byte> pageBuf = new byte[Convert.ToInt32(page.length)];

                    ixStream.Seek(offset, SeekOrigin.Begin);

                    var read = ixStream.Read(pageBuf);

                    var position = 0;

                    while (position < page.length)
                    {
                        var buf = pageBuf.Slice(position, VectorNode.BlockSize);

                        var terminator = buf[buf.Length - 1];
                        var vecOffset = MemoryMarshal.Cast<byte, long>(buf.Slice(0, sizeof(long)))[0];
                        var postingsOffset = MemoryMarshal.Cast<byte, long>(buf.Slice(sizeof(long), sizeof(long)))[0];
                        var componentCount = MemoryMarshal.Cast<byte, int>(buf.Slice(sizeof(long) + sizeof(long), sizeof(int)))[0];
                        var weight = MemoryMarshal.Cast<byte, int>(buf.Slice(sizeof(long) + sizeof(long) + sizeof(int), sizeof(int)))[0];

                        root.Add(
                            VectorOperations.DeserializeNode(
                                vecOffset,
                                postingsOffset,
                                componentCount,
                                weight,
                                vectorStream,
                                ref terminator),
                            similarity);

                        position += VectorNode.BlockSize;
                    }

                    _optimizedOffset = ixStream.Position;

                    this.Log($"optimized {page}");
                }
            }

            this.Log($"optimized {_ixFileName} in {time.Elapsed}");

            return root;
        }

        public Hit ClosestMatch(SortedList<long, int> vector, (float identicalAngle, float foldAngle) similarity)
        {
            var readSetting = _config.Get("read_mode") ?? "false";
            var readonlyMode = readSetting.Equals("true");

            var hits = readonlyMode ? 
                ClosestMatchInMemoryMap(vector, similarity) : 
                ClosestMatchOnDisk(vector, similarity);

            // find best hit

            var time = Stopwatch.StartNew();

            Hit best = null;

            foreach (var hit in hits)
            {
                if (best == null || hit.Score > best.Score)
                {
                    best = hit;
                }
                else if (hit.Score == best.Score)
                {
                    best.Node.Merge(hit.Node);
                }
            }

            this.Log($"merge took {time.Elapsed}");

            return best;
        }

        private ConcurrentBag<Hit> ClosestMatchInMemoryMap(SortedList<long, int> vector, (float identicalAngle, float foldAngle) similarity)
        {
            var time = Stopwatch.StartNew();
            var ixPages = _sessionFactory.ReadPageInfoFromDisk(_ixpFileName);
            var vixPages = _sessionFactory.ReadPageInfoFromDisk(_vixpFileName);

            var hits = new ConcurrentBag<Hit>();
            var bufferSize = int.Parse(_config.Get("read_buffer_size") ?? "4096");

            using (var mmf = _sessionFactory.OpenMMF(_ixFileName))
            using (var vmmf = _sessionFactory.OpenMMF(_vecFileName))
            {
                //for(int i = 0; i < ixPages.Count; i++)
                Parallel.ForEach(ixPages, (ixpage, state, i) =>
                {
                    //var ixpage = ixPages[i];
                    var vpage = vixPages[(int)i];

                    using (var indexStream = mmf.CreateViewStream(ixpage.offset, ixpage.length, MemoryMappedFileAccess.Read))
                    using (var vectorStream = vmmf.CreateViewStream(vpage.offset, vpage.length, MemoryMappedFileAccess.Read))
                    {
                        var hit = ClosestMatchInPage(
                                    vector,
                                    indexStream,
                                    vectorStream,
                                    similarity,
                                    vpage.offset);

                        hits.Add(hit);
                    }
                });
            }

            this.Log($"scan took {time.Elapsed}");

            return hits;
        }

        private ConcurrentBag<Hit> ClosestMatchOnDisk(SortedList<long, int> vector, (float identicalAngle, float foldAngle) similarity)
        {
            var time = Stopwatch.StartNew();
            var pages = _sessionFactory.ReadPageInfoFromDisk(_ixpFileName);
            var hits = new ConcurrentBag<Hit>();
            var ixbufferSize = int.Parse(_config.Get("index_read_buffer_size") ?? "4096");

            Parallel.ForEach(pages, page =>
            {
                using (var indexStream = new BufferedStream(_sessionFactory.CreateReadStream(_ixFileName), ixbufferSize))
                using (var vectorStream = _sessionFactory.CreateReadStream(_vecFileName))
                {
                    indexStream.Seek(page.offset, SeekOrigin.Begin);

                    var hit = ClosestMatchInPage(
                                vector,
                                indexStream,
                                vectorStream,
                                similarity);

                    hits.Add(hit);
                }
            });

            this.Log($"scan took {time.Elapsed}");

            return hits;
        }

        private Hit ClosestMatchInPage(
            SortedList<long, int> node,
            Stream indexStream,
            Stream vectorStream,
            (float identicalAngle, float foldAngle) similarity,
            Queue<(long offset, long length)> pages)
        {
            pages.Dequeue();

            Span<byte> block = new byte[VectorNode.BlockSize];

            var read = indexStream.Read(block);

            VectorNode best = null;
            float highscore = 0;

            while (read > 0)
            {
                var vecOffset = MemoryMarshal.Cast<byte, long>(block.Slice(0, sizeof(long)))[0];
                var componentCount = MemoryMarshal.Cast<byte, int>(block.Slice(sizeof(long) + sizeof(long), sizeof(int)))[0];
                var cursorVector = VectorOperations.DeserializeVector(vecOffset, componentCount, vectorStream);
                var cursorTerminator = block[block.Length - 1];
                var postingsOffset = MemoryMarshal.Cast<byte, long>(block.Slice(sizeof(long), sizeof(long)))[0];

                var angle = cursorVector.CosAngle(node);

                if (angle >= similarity.identicalAngle)
                {
                    if (best == null || angle > highscore)
                    {
                        highscore = angle;
                        best = new VectorNode(cursorVector);
                        best.PostingsOffset = postingsOffset;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, postingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(postingsOffset);
                        }
                    }

                    if (pages.Count == 0)
                        break; // There are no more pages.

                    // There are more pages.
                    // We can continue scanning by picking up at the first node of the next page.
                    indexStream.Seek(pages.Dequeue().offset, SeekOrigin.Begin);
                    read = indexStream.Read(block);
                }
                else if (angle > similarity.foldAngle)
                {
                    if (best == null || angle > highscore)
                    {
                        highscore = angle;
                        best = new VectorNode(cursorVector);
                        best.PostingsOffset = postingsOffset;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, postingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(postingsOffset);
                        }
                    }

                    // We need to determine if we can traverse further left.
                    bool canGoLeft = cursorTerminator == 0 || cursorTerminator == 1;

                    if (canGoLeft)
                    {
                        // There exists either a left and a right child or just a left child.
                        // Either way, we want to go left and the next node in bitmap is the left child.

                        read = indexStream.Read(block);
                    }
                    else
                    {
                        // There is no left child.

                        if (pages.Count == 0)
                            break; // There are no more pages.

                        // There are more pages.
                        // We can continue scanning by picking up at the first node of the next page.
                        indexStream.Seek(pages.Dequeue().offset, SeekOrigin.Begin);
                        read = indexStream.Read(block);
                    }
                }
                else
                {
                    if (best == null || angle > highscore)
                    {
                        highscore = angle;
                        best = new VectorNode(cursorVector);
                        best.PostingsOffset = postingsOffset;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, postingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(postingsOffset);
                        }
                    }

                    // We need to determine if we can traverse further to the right.

                    if (cursorTerminator == 0)
                    {
                        // There exists a left and a right child.
                        // Next node in bitmap is the left child. 
                        // To find cursor's right child we must skip over the left tree.

                        SkipTree(indexStream);
                        read = indexStream.Read(block);
                    }
                    else if (cursorTerminator == 2)
                    {
                        // Next node in bitmap is the right child,
                        // which is good because we want to go right.

                        read = indexStream.Read(block);
                    }
                    else
                    {
                        // There is no right child.

                        if (pages.Count == 0)
                            break; // There are no more pages.

                        // There are more pages.
                        // We can continue scanning by picking up at the first node of the next page.
                        indexStream.Seek(pages.Dequeue().offset, SeekOrigin.Begin);
                        read = indexStream.Read(block);
                    }
                }
            }

            return new Hit
            {
                Score = highscore,
                Node = best
            };
        }

        private Hit ClosestMatchInPage(
            SortedList<long, int> node,
            Stream indexStream,
            MemoryMappedViewAccessor vectorView,
            (float identicalAngle, float foldAngle) similarity)
        {
            var cursor = ReadNode(indexStream, vectorView);

            if (cursor == null)
            {
                throw new InvalidOperationException();
            }

            var best = cursor;
            float highscore = 0;

            while (cursor != null)
            {
                var angle = cursor.Vector.CosAngle(node);

                if (angle >= similarity.identicalAngle)
                {
                    if (angle > highscore)
                    {
                        highscore = angle;
                        best = cursor;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, cursor.PostingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(cursor.PostingsOffset);
                        }
                    }

                    break;
                }
                else if (angle > similarity.foldAngle)
                {
                    if (angle > highscore)
                    {
                        highscore = angle;
                        best = cursor;
                    }
                    else if (angle > 0 && angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, cursor.PostingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(cursor.PostingsOffset);
                        }
                    }

                    // We need to determine if we can traverse further left.
                    bool canGoLeft = cursor.Terminator == 0 || cursor.Terminator == 1;

                    if (canGoLeft)
                    {
                        // There exists either a left and a right child or just a left child.
                        // Either way, we want to go left and the next node in bitmap is the left child.

                        cursor = ReadNode(indexStream, vectorView);
                    }
                    else
                    {
                        // There is no left child.
                        break;
                    }
                }
                else
                {
                    if (angle > highscore)
                    {
                        highscore = angle;
                        best = cursor;
                    }
                    else if (angle > 0 && angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, cursor.PostingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(cursor.PostingsOffset);
                        }
                    }

                    // We need to determine if we can traverse further to the right.

                    if (cursor.Terminator == 0)
                    {
                        // There exists a left and a right child.
                        // Next node in bitmap is the left child. 
                        // To find cursor's right child we must skip over the left tree.

                        SkipTree(indexStream);
                        cursor = ReadNode(indexStream, vectorView);
                    }
                    else if (cursor.Terminator == 2)
                    {
                        // Next node in bitmap is the right child,
                        // which is good because we want to go right.

                        cursor = ReadNode(indexStream, vectorView);
                    }
                    else
                    {
                        // There is no right child.
                        break;
                    }
                }
            }

            return new Hit
            {
                Score = highscore,
                Node = best
            };
        }

        private Hit ClosestMatchInPage(
            SortedList<long, int> node,
            Stream indexStream,
            Stream vectorStream,
            (float identicalAngle, float foldAngle) similarity,
            long offset = 0
        )
        {
            Span<byte> block = new byte[VectorNode.BlockSize];

            var read = indexStream.Read(block);

            VectorNode best = null;
            float highscore = 0;

            while (read > 0)
            {
                var vecOffset = MemoryMarshal.Cast<byte, long>(block.Slice(0, sizeof(long)))[0] - offset;
                var componentCount = MemoryMarshal.Cast<byte, int>(block.Slice(sizeof(long) + sizeof(long), sizeof(int)))[0];
                var cursorVector = VectorOperations.DeserializeVector(vecOffset, componentCount, vectorStream);
                var cursorTerminator = block[block.Length - 1];
                var postingsOffset = MemoryMarshal.Cast<byte, long>(block.Slice(sizeof(long), sizeof(long)))[0];

                var angle = cursorVector.CosAngle(node);

                if (angle >= similarity.identicalAngle)
                {
                    if (best == null || angle > highscore)
                    {
                        highscore = angle;
                        best = new VectorNode(cursorVector);
                        best.PostingsOffset = postingsOffset;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, postingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(postingsOffset);
                        }
                    }

                    break;
                }
                else if (angle > similarity.foldAngle)
                {
                    if (best == null || angle > highscore)
                    {
                        highscore = angle;
                        best = new VectorNode(cursorVector);
                        best.PostingsOffset = postingsOffset;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, postingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(postingsOffset);
                        }
                    }

                    // We need to determine if we can traverse further left.
                    bool canGoLeft = cursorTerminator == 0 || cursorTerminator == 1;

                    if (canGoLeft)
                    {
                        // There exists either a left and a right child or just a left child.
                        // Either way, we want to go left and the next node in bitmap is the left child.

                        read = indexStream.Read(block);
                    }
                    else
                    {
                        // There is no left child.

                        break;
                    }
                }
                else
                {
                    if (best == null || angle > highscore)
                    {
                        highscore = angle;
                        best = new VectorNode(cursorVector);
                        best.PostingsOffset = postingsOffset;
                    }
                    else if (angle == highscore)
                    {
                        if (best.PostingsOffsets == null)
                        {
                            best.PostingsOffsets = new List<long> { best.PostingsOffset, postingsOffset };
                        }
                        else
                        {
                            best.PostingsOffsets.Add(postingsOffset);
                        }
                    }

                    // We need to determine if we can traverse further to the right.

                    if (cursorTerminator == 0)
                    {
                        // There exists a left and a right child.
                        // Next node in bitmap is the left child. 
                        // To find cursor's right child we must skip over the left tree.

                        SkipTree(indexStream);
                        read = indexStream.Read(block);
                    }
                    else if (cursorTerminator == 2)
                    {
                        // Next node in bitmap is the right child,
                        // which is good because we want to go right.

                        read = indexStream.Read(block);
                    }
                    else
                    {
                        // There is no right child.

                        break;
                    }
                }
            }

            return new Hit
            {
                Score = highscore,
                Node = best
            };
        }

        private VectorNode ReadNode(Stream indexStream, MemoryMappedViewAccessor vectorView)
        {
            var buf = new byte[VectorNode.BlockSize];
            var read = indexStream.Read(buf);

            if (read == 0) return null;

            var terminator = buf[buf.Length - 1];
            var node = VectorOperations.DeserializeNode(buf, vectorView, ref terminator);

            return node;
        }

        private VectorNode ReadNode(Stream indexStream, Stream vectorStream)
        {
            var buf = new byte[VectorNode.BlockSize];
            var read = indexStream.Read(buf);

            if (read == 0) return null;

            var terminator = buf[buf.Length - 1];
            var node = VectorOperations.DeserializeNode(buf, vectorStream, ref terminator);

            return node;
        }

        private void SkipTree(Stream indexStream)
        {
            var buf = new byte[VectorNode.BlockSize];

            var read = indexStream.Read(buf);

            if (read == 0)
            {
                throw new InvalidOperationException();
            }

            var positionInBuffer = VectorNode.BlockSize - (sizeof(int) + sizeof(byte));
            var weight = BitConverter.ToInt32(buf, positionInBuffer);
            var distance = weight * VectorNode.BlockSize;

            if (distance > 0)
            {
                indexStream.Seek(distance, SeekOrigin.Current);
            }
        }
    }
}