﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sir.Postings
{
    public class StreamRepository : IDisposable, ILogger
    {
        private readonly IConfigurationProvider _config;
        private const string DataFileNameFormat = "{0}.pos";
        private readonly IDictionary<(ulong, long), IList<long>> _cache;

        public StreamRepository(IConfigurationProvider config)
        {
            _config = config;
            _cache = new ConcurrentDictionary<(ulong, long), IList<long>>();
        }

        public async Task<IList<long>> Read(ulong collectionId, long offset)
        {
            var key = (collectionId, offset);
            IList<long> result;

            if (!_cache.TryGetValue(key, out result))
            {
                result = await ReadFromDisk(collectionId, offset);
                _cache[key] = result;
            }

            return result;
        }

        private async Task<IList<long>> ReadFromDisk(ulong collectionId, long offset)
        {
            var timer = Stopwatch.StartNew();
            var result = new HashSet<long>();
            var pageCount = 0;

            using (var data = CreateReadableDataStream(collectionId))
            {
                data.Seek(offset, SeekOrigin.Begin);

                // We are now at the first page.
                // Each page starts with a header consisting of (a) count, (b) next page offset and (c) last page offset (long, long, long).
                // The rest of the page is data (long's).

                var lbuf = new byte[sizeof(long)];

                await data.ReadAsync(lbuf);

                var count = BitConverter.ToInt64(lbuf);

                await data.ReadAsync(lbuf);

                var nextPageOffset = BitConverter.ToInt64(lbuf);

                var pageLen = sizeof(long) + (count * sizeof(long));
                var pageBuf = new byte[pageLen];

                await data.ReadAsync(pageBuf);

                for (int i = 0; i < count; i++)
                {
                    var entryOffset = sizeof(long) + (i * sizeof(long));
                    var entry = BitConverter.ToInt64(pageBuf, entryOffset);

                    if (!result.Add(entry))
                    {
                        throw new DataMisalignedException("first page is crap");
                    }
                }

                pageCount++;

                while (nextPageOffset > -1)
                {
                    data.Seek(nextPageOffset, SeekOrigin.Begin);

                    await data.ReadAsync(lbuf);

                    count = BitConverter.ToInt64(lbuf);

                    await data.ReadAsync(lbuf);

                    nextPageOffset = BitConverter.ToInt64(lbuf);

                    pageLen = sizeof(long) + (count * sizeof(long));
                    var page = new byte[pageLen];

                    await data.ReadAsync(page);

                    for (int i = 0; i < count; i++)
                    {
                        var entryOffset = sizeof(long) + (i * sizeof(long));
                        var entry = BitConverter.ToInt64(page, entryOffset);

                        if (!result.Add(entry))
                        {
                            throw new DataMisalignedException("page is crap");
                        }
                    }

                    pageCount++;
                }
            }

            this.Log("read {0} postings from {1} pages in {2}", result.Count, pageCount, timer.Elapsed);

            return result.ToList();
        }

        public MemoryStream Write(ulong collectionId, byte[] messageBuf)
        {
            var time = Stopwatch.StartNew();
            int read = 0;
            var lists = new List<byte[]>();
            var offsets = new List<long>();
            var lengths = new List<int>();

            // read first word of payload
            var payloadCount = BitConverter.ToInt32(messageBuf, 0);
            read = sizeof(int);

            // read list lengths
            for (int index = 0; index < payloadCount; index++)
            {
                var len = BitConverter.ToInt32(messageBuf, read);
                lengths.Add(len);
                read += sizeof(int);
            }

            // read offsets (IDs)
            for (int index = 0; index < payloadCount; index++)
            {
                var offset = BitConverter.ToInt64(messageBuf, read);
                offsets.Add(offset);
                read += sizeof(long);
            }

            // read lists
            for (int index = 0; index < lengths.Count; index++)
            {
                var size = lengths[index];
                var buf = new byte[size];
                Buffer.BlockCopy(messageBuf, read, buf, 0, size);
                read += size;
                lists.Add(buf);
            }

            if (lists.Count != payloadCount)
            {
                throw new DataMisalignedException();
            }

            this.Log("parsed payload in {0}", time.Elapsed);
            time.Restart();

            var listOffsets = new List<long>(lists.Count);

            // persist payload

            using (var data = CreateReadableWritableDataStream(collectionId))
            {
                for (int listIndex = 0; listIndex < lists.Count; listIndex++)
                {
                    var offset = offsets[listIndex];
                    var list = lists[listIndex];

                    // invalidate cache
                    var cacheKey = (collectionId, offset);
                    if (_cache.ContainsKey(cacheKey))
                    {
                        _cache.Remove(cacheKey);
                    }

                    if (offset < 0)
                    {
                        // Since this data does not have an ID we are going to append it and give it one.

                        data.Seek(0, SeekOrigin.End);

                        // record new file location (its ID)
                        offset = data.Position;

                        // write count to header of this page
                        data.Write(BitConverter.GetBytes(Convert.ToInt64(list.Length/sizeof(long))));

                        // write nextPageOffset
                        data.Write(BitConverter.GetBytes((long)-1));

                        // write lastPageOffset
                        data.Write(BitConverter.GetBytes(offset));

                        // write payload
                        data.Write(list);
                    }
                    else
                    {
                        // There is already at least one page with this ID.
                        // We need to find the offset of that page so that we can update its header,
                        // then write the data and record its offset,
                        // and then write that offset into the header of the first page.

                        var nextPageOffsetWordPosition = offset + sizeof(long);
                        long lastPageOffsetWordPosition = offset + (2 * sizeof(long));
                        var lbuf = new byte[sizeof(long)];

                        data.Seek(lastPageOffsetWordPosition, SeekOrigin.Begin);

                        data.Read(lbuf);

                        long lastPageOffset = BitConverter.ToInt64(lbuf);

                        if (lastPageOffset != offset)
                        {
                            nextPageOffsetWordPosition = lastPageOffset + sizeof(long);
                        }

                        data.Seek(0, SeekOrigin.End);

                        var newPageOffset = data.Position;

                        // write count to header of this page
                        data.Write(BitConverter.GetBytes(Convert.ToInt64(list.Length / sizeof(long))));

                        // write nextPageOffset
                        data.Write(BitConverter.GetBytes((long)-1));

                        // write lastPageOffset
                        data.Write(BitConverter.GetBytes((long)-1));

                        // write payload
                        data.Write(list);

                        // update next page offset
                        data.Seek(nextPageOffsetWordPosition, SeekOrigin.Begin);
                        data.Write(BitConverter.GetBytes(newPageOffset));

                        // update last page offset
                        data.Seek(lastPageOffsetWordPosition, SeekOrigin.Begin);
                        data.Write(BitConverter.GetBytes(newPageOffset));
                    }

                    listOffsets.Add(offset);
                }
            }

            this.Log("serialized data and index in {0}", time.Elapsed);

            time.Restart();

            if (listOffsets.Count != payloadCount)
            {
                throw new DataMisalignedException();
            }

            // construct a response message that contains a list of offsets (IDs).

            var res = new MemoryStream();

            for (int i = 0; i < listOffsets.Count; i++)
            {
                res.Write(BitConverter.GetBytes(listOffsets[i]));
            }

            res.Position = 0;

            this.Log("serialized response message in {0}", time.Elapsed);

            return res;
        }

        public static MemoryStream Serialize(IEnumerable<KeyValuePair<long, float>> docs)
        {
            var result = new MemoryStream();

            foreach (var doc in docs)
            {
                result.Write(BitConverter.GetBytes(doc.Key));
                result.Write(BitConverter.GetBytes(doc.Value));
            }

            return result;
        }

        private Stream CreateReadableWritableDataStream(ulong collectionId)
        {
            var fileName = Path.Combine(_config.Get("data_dir"), string.Format(DataFileNameFormat, collectionId));

            var stream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            return Stream.Synchronized(stream);
        }

        private Stream CreateReadableDataStream(ulong collectionId)
        {
            var fileName = Path.Combine(_config.Get("data_dir"), string.Format(DataFileNameFormat, collectionId));

            if (File.Exists(fileName))
            {
                var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
                return stream;
            }

            return null;
        }

        public void Dispose()
        {
        }
    }
}