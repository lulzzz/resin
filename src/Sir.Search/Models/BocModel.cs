﻿using MathNet.Numerics.LinearAlgebra;
using Sir.VectorSpace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Sir.Search
{
    public class BocModel : DistanceCalculator, IStringModel
    {
        public double IdenticalAngle => 0.88d;
        public double FoldAngle => 0.58d;
        public int UnicodeStartingPoint => 32;
        public override int VectorWidth => 256;

        public IEnumerable<IVector> Tokenize(string data)
        {
            Memory<char> source = data.ToCharArray();
            var tokens = new List<IVector>();
            
            if (source.Length > 0)
            {
                var embedding = new SortedList<int, float>();
                var offset = 0;
                int index = 0;
                var span = source.Span;

                for (; index < source.Length; index++)
                {
                    char c = char.ToLower(span[index]);

                    if (c < UnicodeStartingPoint || c > UnicodeStartingPoint + VectorWidth)
                    {
                        continue;
                    }

                    if (char.IsLetterOrDigit(c))
                    {
                        embedding.AddOrAppendToComponent(c);
                    }
                    else
                    {
                        if (embedding.Count > 0)
                        {
                            var len = index - offset;
                            var slice = source.Slice(offset, len);

                            var vector = new IndexedVector(
                                embedding,
                                VectorWidth,
                                slice);

                            embedding.Clear();
                            tokens.Add(vector);
                        }

                        offset = index + 1;
                    }
                }

                if (embedding.Count > 0)
                {
                    var len = index - offset;

                    var vector = new IndexedVector(
                                embedding,
                                VectorWidth,
                                source.Slice(offset, len));

                    tokens.Add(vector);
                }
            }

            return tokens;
        }
    }

    public class StreamModel : DistanceCalculator, IStreamModel
    {
        public double IdenticalAngle => 0.88d;
        public double FoldAngle => 0.58d;
        public override int VectorWidth => 28;

        public IEnumerable<IVector> Tokenize(byte[][] data)
        {
            foreach (var row in data)
            {
                yield return new IndexedVector(row.Cast<float>(), row.Length);
            }
        }
    }
}
