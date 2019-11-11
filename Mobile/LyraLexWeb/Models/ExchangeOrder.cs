﻿using Lyra.Core.Blocks;
using Lyra.Exchange;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace LyraLexWeb.Models
{
    public enum DealState { Invalid, Placed, Executed, PartialExecuted, Canceled };
    public class ExchangeOrder
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public IPAddress ClientIP { get; set; }

        public bool CanDeal { get; set; }

        public DealState State { get; set; }

        public TokenTradeOrder Order { get; set; }
    }
}
