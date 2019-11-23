﻿using Lyra.Core;
using Lyra.Core.Accounts;
using Lyra.Core.API;
using Lyra.Core.Blocks.Transactions;
using Lyra.Core.Protos;
using Lyra.Exchange;
using Lyra.Node2.Authorizers;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lyra.Node2.Services
{
    public class NodeService : BackgroundService
    {
        private static LyraConfig _config;

        private static INodeAPI _node;
        public static MongoClient client;
        private static IMongoDatabase _db;
        private static IMongoCollection<ExchangeAccount> _exchangeAccounts;
        private static IMongoCollection<ExchangeOrder> _queue;
        private static IMongoCollection<ExchangeOrder> _finished;
        static AutoResetEvent _waitOrder;

        public NodeService(Microsoft.Extensions.Options.IOptions<LyraConfig> config,
            INodeAPI node)
        {
            _config = config.Value;
            _node = node;

            //BaseAuthorizer.OnAuthorized += (s, e) => { 
            //    if(e.Result is SendTransferBlock)
            //    {
            //        var block = e.Result as SendTransferBlock;

            //    }
            //};
        }

        internal async static Task<ExchangeAccount> GetExchangeAccount(string accountID, bool refreshBalance = false)
        {
            var findResult = await _exchangeAccounts.FindAsync(a => a.AssociatedToAccountId == accountID);
            var acct = await findResult.FirstOrDefaultAsync();
            if(!refreshBalance)
                return acct;

            if (acct != null)
            {
                // create wallet and update balance
                var memStor = new AccountInMemoryStorage();
                var acctWallet = new Wallet(memStor, _config.NetworkId);
                acctWallet.AccountName = "tmpAcct";
                acctWallet.RestoreAccount("", acct.PrivateKey);
                acctWallet.OpenAccount("", acctWallet.AccountName);
                var result = await acctWallet.Sync(_node);
                if (result == APIResultCodes.Success)
                {
                    var transb = acctWallet.GetLatestBlock();
                    if (transb != null)
                    {
                        if (acct.Balance == null)
                            acct.Balance = new Dictionary<string, decimal>();
                        else
                            acct.Balance.Clear();
                        foreach (var b in transb.Balances)
                        {
                            acct.Balance.Add(b.Key, b.Value);
                        }
                        _exchangeAccounts.ReplaceOne(a => a.AssociatedToAccountId == accountID, acct);
                    }
                }
            }
            return acct;
        }
        internal async static Task<decimal> GetExchangeAccountBalance(string accountID, string tokenName)
        {
            var findResult = await _exchangeAccounts.FindAsync(a => a.AssociatedToAccountId == accountID);
            var acct = await findResult.FirstOrDefaultAsync();
            if (acct == null)
                return 0;
            if (!acct.Balance.ContainsKey(tokenName))
                return 0;
            return acct.Balance[tokenName];
        }

        public static async Task<ExchangeAccount> AddExchangeAccount(string assocaitedAccountId, string privateKey, string publicKey)
        {
            var account = new ExchangeAccount()
            {
                AssociatedToAccountId = assocaitedAccountId,
                AccountId = publicKey,
                PrivateKey = privateKey
            };
            await _exchangeAccounts.InsertOneAsync(account);
            return account;
        }

        public static async Task<CancelKey> AddOrderAsync(ExchangeAccount acct, TokenTradeOrder order)
        {
            order.CreatedTime = DateTime.Now;
            var item = new ExchangeOrder()
            {
                ExchangeAccountId = acct.Id,
                Order = order,
                CanDeal = true,
                State = DealState.Placed,
                ClientIP = null
            };
            await _queue.InsertOneAsync(item);
            _waitOrder.Set();

            var key = new CancelKey()
            {
                State = OrderState.Placed,
                Key = item.Id.ToString(),
                Order = order
            };
            return key;
        }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _waitOrder = new AutoResetEvent(false);
            try
            {
                if (_db == null)
                {
                    BsonSerializer.RegisterSerializer(typeof(decimal), new DecimalSerializer(BsonType.Decimal128));
                    BsonSerializer.RegisterSerializer(typeof(decimal?), new NullableSerializer<decimal>(new DecimalSerializer(BsonType.Decimal128)));

                    client = new MongoClient(_config.DBConnect);
                    _db = client.GetDatabase("Dex");

                    _exchangeAccounts = _db.GetCollection<ExchangeAccount>("exchangeAccounts");
                    _queue = _db.GetCollection<ExchangeOrder>("queuedDexOrders");
                    _finished = _db.GetCollection<ExchangeOrder>("finishedDexOrders");
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Can find or create mongo database.", ex);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                // do work
                if (_waitOrder.WaitOne(1000))
                {
                    _waitOrder.Reset();
                    // has new order. do trade
                    var changedTokens = new List<string>();
                    var changedAccount = new List<string>();

                    var placed = await GetNewlyPlacedOrdersAsync();
                    for (int i = 0; i < placed.Length; i++)
                    {
                        var curOrder = placed[i];

                        if (!changedTokens.Contains(curOrder.Order.TokenName))
                            changedTokens.Add(curOrder.Order.TokenName);

                        var matchedOrders = await LookforExecution(curOrder);
                        if (matchedOrders.Count() > 0)
                        {
                            foreach (var matchedOrder in matchedOrders)
                            {
                                // three conditions
                                if (matchedOrder.Order.Amount < curOrder.Order.Amount)
                                {
                                    //matched -> archive, cur -> partial
                                    matchedOrder.State = DealState.Executed;
                                    await _queue.DeleteOneAsync(Builders<ExchangeOrder>.Filter.Eq(o => o.Id, matchedOrder.Id));
                                    await _finished.InsertOneAsync(matchedOrder);

                                    curOrder.State = DealState.PartialExecuted;
                                    curOrder.Order.Amount -= matchedOrder.Order.Amount;

                                    if (!changedAccount.Contains(matchedOrder.Order.AccountID))
                                        changedAccount.Add(matchedOrder.Order.AccountID);
                                    if (!changedAccount.Contains(matchedOrder.Order.AccountID))
                                        changedAccount.Add(matchedOrder.Order.AccountID);

                                    continue;
                                }
                                else if (matchedOrder.Order.Amount == curOrder.Order.Amount)
                                {
                                    // matched -> archive, cur -> archive
                                    matchedOrder.State = DealState.Executed;
                                    await _queue.DeleteOneAsync(Builders<ExchangeOrder>.Filter.Eq(o => o.Id, matchedOrder.Id));
                                    await _finished.InsertOneAsync(matchedOrder);

                                    curOrder.State = DealState.Executed;
                                    await _queue.DeleteOneAsync(Builders<ExchangeOrder>.Filter.Eq(o => o.Id, curOrder.Id));
                                    await _finished.InsertOneAsync(curOrder);

                                    if (!changedAccount.Contains(matchedOrder.Order.AccountID))
                                        changedAccount.Add(matchedOrder.Order.AccountID);
                                    if (!changedAccount.Contains(matchedOrder.Order.AccountID))
                                        changedAccount.Add(matchedOrder.Order.AccountID);

                                    break;
                                }
                                else // matchedOrder.Order.Amount > curOrder.Order.Amount
                                {
                                    // matched -> partial, cur -> archive
                                    matchedOrder.State = DealState.PartialExecuted;
                                    matchedOrder.Order.Amount -= curOrder.Order.Amount;
                                    await _queue.ReplaceOneAsync(Builders<ExchangeOrder>.Filter.Eq(o => o.Id, matchedOrder.Id), matchedOrder);

                                    curOrder.State = DealState.Executed;
                                    await _queue.DeleteOneAsync(Builders<ExchangeOrder>.Filter.Eq(o => o.Id, curOrder.Id));
                                    await _finished.InsertOneAsync(curOrder);

                                    if (!changedAccount.Contains(matchedOrder.Order.AccountID))
                                        changedAccount.Add(matchedOrder.Order.AccountID);
                                    if (!changedAccount.Contains(matchedOrder.Order.AccountID))
                                        changedAccount.Add(matchedOrder.Order.AccountID);

                                    break;
                                }
                            }
                        }

                        // all matched. update database                        
                        // change state from placed to queued, update amount also.
                        var update = Builders<ExchangeOrder>.Update.Set(o => o.Order.Amount, curOrder.Order.Amount);
                        if (curOrder.State == DealState.Placed)
                            update = update.Set(s => s.State, DealState.Queued);
                        if(curOrder.State == DealState.Placed || curOrder.State == DealState.PartialExecuted)
                            await _queue.UpdateOneAsync(Builders<ExchangeOrder>.Filter.Eq(o => o.Id, curOrder.Id), update);
                    }
                    foreach (var tokenName in changedTokens)
                    {
                        // the update the client
                        await SendMarket(tokenName);
                    }
                    foreach(var account in changedAccount)
                    {
                        // client must refresh by itself
                        NotifyService.Notify(account, Core.API.NotifySource.Dex, "Deal", "", "");
                    }
                }
                else
                {
                    // no new order. do house keeping.
                }
            }
        }

        private async Task DoExchange(string accounIdSellToken, Decimal tokenAmount, 
            string accountIdBuyToken, decimal lypeAmount)
        {
            
        }

        public async Task<ExchangeOrder[]> GetNewlyPlacedOrdersAsync()
        {
            var finds = await _queue.FindAsync(a => a.State == DealState.Placed);
            var fl = await finds.ToListAsync();
            return fl.OrderBy(a => a.Order.CreatedTime).ToArray();
        }

        public async Task<ExchangeOrder[]> GetQueuedOrdersAsync()
        {
            var finds = await _queue.FindAsync(a => a.CanDeal);
            var fl = await finds.ToListAsync();
            return fl.OrderBy(a => a.Order.CreatedTime).ToArray();
        }

        public async Task<IOrderedEnumerable<ExchangeOrder>> LookforExecution(ExchangeOrder order)
        {
            //var builder = Builders<ExchangeOrder>.Filter;
            //var filter = builder.Eq("Order.TokenName", order.Order.TokenName)
            //    & builder.Ne("State", DealState.Placed);

            //if (order.Order.BuySellType == OrderType.Buy)
            //{
            //    filter &= builder.Eq("Order.BuySellType", OrderType.Sell);
            //    filter &= builder.Lte("Order.Price", order.Order.Price);
            //}
            //else
            //{
            //    filter &= builder.Eq("Order.BuySellType", OrderType.Buy);
            //    filter &= builder.Gte("Order.Price", order.Order.Price);
            //}

            IAsyncCursor<ExchangeOrder> found;
            if (order.Order.BuySellType == OrderType.Buy)
            {
                found = await _queue.FindAsync(a => a.Order.TokenName == order.Order.TokenName
                                && a.State != DealState.Placed
                                && a.Order.BuySellType == OrderType.Sell
                                && a.Order.Price <= order.Order.Price);
            }
            else
            {
                found = await _queue.FindAsync(a => a.Order.TokenName == order.Order.TokenName
                && a.State != DealState.Placed
                && a.Order.BuySellType == OrderType.Buy
                && a.Order.Price >= order.Order.Price);
            }

            var matches0 = await found.ToListAsync();

            if (order.Order.BuySellType == OrderType.Buy)
            {
                var matches = matches0.OrderBy(a => a.Order.Price);
                return matches;
            }
            else
            {
                var matches = matches0.OrderByDescending(a => a.Order.Price);
                return matches;
            }
        }

        public static async Task<List<ExchangeOrder>> GetActiveOrders(string tokenName)
        {
            return await _queue.Find(a => a.CanDeal && a.Order.TokenName == tokenName).ToListAsync();
        }

        public static async Task SendMarket(string tokenName)
        {
            var excOrders = (await GetActiveOrders(tokenName)).OrderByDescending(a => a.Order.Price);
            var sellOrders = excOrders.Where(a => a.Order.BuySellType == OrderType.Sell)
                .GroupBy(a => a.Order.Price)
                .Select(a => new KeyValuePair<Decimal, Decimal>(a.Key, a.Sum(x => x.Order.Amount))).ToList();

            var buyOrders = excOrders.Where(a => a.Order.BuySellType == OrderType.Buy)
                .GroupBy(a => a.Order.Price)
                .Select(a => new KeyValuePair<Decimal, Decimal>(a.Key, a.Sum(x => x.Order.Amount))).ToList();

            var orders = new Dictionary<string, List<KeyValuePair<Decimal, Decimal>>>();
            orders.Add("SellOrders", sellOrders);
            orders.Add("BuyOrders", buyOrders);

            NotifyService.Notify("", Core.API.NotifySource.Dex, "Orders", tokenName, JsonConvert.SerializeObject(orders));
        }

        internal static async Task<List<ExchangeOrder>> GetOrdersForAccount(string accountId)
        {
            return await _queue.Find(a => a.Order.AccountID == accountId).ToListAsync();
        }


    }
}
