﻿using System.Collections.Generic;

namespace Sir.Store
{
    public class ReadResult
    {
        public long Total { get; set; }
        public IEnumerable<IDictionary<string, object>> Docs { get; set; }


    }
}