﻿using BoxOptions.Common.Interfaces;
using BoxOptions.Core;
using BoxOptions.Core.Interfaces;
using BoxOptions.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BoxOptions.Public.Processors
{
    public class AzureQuoteDatabase : IAssetDatabase, IDisposable
    {
        IAssetRepository assetRep;
        bool isDisposing = false;
        const int queueMaxSize = 100;
        Dictionary<string, Queue<BestBidAsk>> assetCache;
        
        public AzureQuoteDatabase(IAssetRepository assetRep)
        {
            this.assetRep = assetRep;
            assetCache = new Dictionary<string, Queue<BestBidAsk>>();
        }

        public Task AddToAssetHistory(BestBidAsk bidask)
        {

            AddToHistory(bidask);
            return Task.FromResult(0);
        }
                
        private void AddToHistory(BestBidAsk bidask)
        {

            if (!assetCache.ContainsKey(bidask.Asset))
                assetCache.Add(bidask.Asset, new Queue<BestBidAsk>());

            assetCache[bidask.Asset].Enqueue(bidask);



            if (assetCache[bidask.Asset].Count >= queueMaxSize)
            {
                List<BestBidAsk> buffer = assetCache[bidask.Asset].ToList();
                assetCache[bidask.Asset].Clear();
                //Console.WriteLine("{0} > Buffer Full Inserting {1} items [{2}]", DateTime.UtcNow.ToString("HH:mm:ss.fff"), buffer.Count, buffer[0].Asset);
                InsertInAzure(buffer);
                //Console.WriteLine("{0} > DONE [{1}]", DateTime.UtcNow.ToString("HH:mm:ss.fff"), buffer[0].Asset);
            }


         
        }

        private async void InsertInAzure(List<BestBidAsk> buffer)
        {            
            try
            {  
                await assetRep.InsertManyAsync(buffer);             
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }            
        }
              
        public async Task<LinkedList<BestBidAsk>> GetAssetHistory(DateTime dateFrom, DateTime dateTo, string assetPair)
        {
            var history = await assetRep.GetRange(dateFrom, dateTo, assetPair); ;
            var sorted = from h in history
                            orderby h.Timestamp
                            select h;
            return new LinkedList<BestBidAsk>(sorted);
        }

        public void Dispose()
        {
            if (isDisposing)
                return;
            isDisposing = true;

            // Flush Asset Catch to Azure
            foreach (var key in assetCache.Keys)
            {
                if (assetCache[key].Count > 0)
                {
                    List<BestBidAsk> buffer = assetCache[key].ToList();
                    assetCache[key].Clear();

                    InsertInAzure(buffer);
                }
            }
           


        }
    }
}
