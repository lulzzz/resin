﻿using System.Collections.Generic;

namespace Sir
{
    /// <summary>
    /// An analyzed (tokenized) string.
    /// </summary>
    public class AnalyzedString
    {
        public IList<(int offset, int length)> Tokens { get; private set; }
        public IList<Vector> Embeddings { get; private set; }
        public string Original { get; private set; }

        public AnalyzedString(IList<(int offset, int length)> tokens, IList<Vector> embeddings, string original)
        {
            Tokens = tokens;
            Embeddings = embeddings;
            Original = original;
        }

        public override string ToString()
        {
            return Original;
        }

        public static AnalyzedString AsSingleToken(string text)
        {
            var tokens = new List<(int, int)> { (0, text.Length) };
            var vectors = new List<Vector> { text.ToSparseVector(0, text.Length) };

            return new AnalyzedString(tokens, vectors, text);
        }
    }
}