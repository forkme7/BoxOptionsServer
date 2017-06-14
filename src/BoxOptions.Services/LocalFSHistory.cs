﻿using BoxOptions.Common.Interfaces;
using BoxOptions.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BoxOptions.Services
{
    public class LocalFSHistory : IAssetDatabase
    {
        static System.Globalization.CultureInfo Ci = new System.Globalization.CultureInfo("en-us");
        static object AssetFileAccessLock = new object();
        static object UserFileAccessLock = new object();

        private Task<LinkedList<BestBidAsk>> LoadAssetHistory(DateTime dateFrom, DateTime dateTo, string assetPair)
        {
            LinkedList<BestBidAsk> retval = new LinkedList<BestBidAsk>();
            try
            {
                lock (AssetFileAccessLock)
                {
                    DateTime currentDay = dateFrom;
                    do
                    {
                        string currentFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"boxoptions.assets{currentDay.ToString("yyyyMMdd")}.hist");
                        if (System.IO.File.Exists(currentFile))
                        {
                            using (var filestream = System.IO.File.OpenRead(currentFile))
                            using (var textstream = new System.IO.StreamReader(filestream))
                            {
                                while (!textstream.EndOfStream)
                                {

                                    string assetEntry = textstream.ReadLine();
                                    string[] values = assetEntry.Split('|');
                                    // Filter Asset Pair 
                                    if (values[1] == assetPair)
                                    {
                                        // Filter Date
                                        DateTime dt = DateTime.ParseExact(values[0], "yyyyMMdd_HHmmssff", Ci);
                                        if (dt >= dateFrom && dt <= dateTo)
                                        {
                                            retval.AddLast(new BestBidAsk()
                                            {
                                                Timestamp = dt,
                                                Asset = values[1],
                                                BestBid = double.Parse(values[2], Ci),
                                                BestAsk = double.Parse(values[3], Ci),
                                                Source = values[4]
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        currentDay = currentDay.AddDays(1);
                    } while (currentDay <= dateTo);
                }
            }
            catch
            {
                throw;
            }
            return Task.FromResult(retval);
        }

        private Task AddToAssetFile(BestBidAsk[] buffer)
        {
            
            lock (AssetFileAccessLock)
            {
                string assetHistoryFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"boxoptions.assets{DateTime.UtcNow.ToString("yyyyMMdd")}.hist");
                if (!System.IO.File.Exists(assetHistoryFile))
                {
                    var historystream = System.IO.File.Create(assetHistoryFile);
                    historystream.Dispose();
                }

                using (var filestream = new System.IO.FileStream(assetHistoryFile, System.IO.FileMode.Append, System.IO.FileAccess.Write))
                using (var textstream = new System.IO.StreamWriter(filestream))
                {
                    foreach (var quote in buffer)
                    {
                        string line = string.Format("{0}|{1}|{2}|{3}|{4}",
                            quote.Timestamp.ToString("yyyyMMdd_HHmmssff", Ci),
                            quote.Asset,
                            quote.BestBid.Value.ToString(Ci),
                            quote.BestAsk.Value.ToString(Ci),
                            quote.Source
                            );
                        textstream.WriteLine(line);
                    }
                    
                }
            }
            return Task.FromResult(0);
        }

        public Task<LinkedList<BestBidAsk>> GetAssetHistory(DateTime dateFrom, DateTime dateTo, string assetPair)
        {
            return LoadAssetHistory(dateFrom, dateTo, assetPair);
        }
        Queue<BestBidAsk> AssetQueue = new Queue<BestBidAsk>();
        Task IAssetDatabase.AddToAssetHistory(BestBidAsk quote)
        {            
            AssetQueue.Enqueue(quote);
            try
            {
                if (AssetQueue.Count >= 512)
                {
                    BestBidAsk[] buffer = AssetQueue.ToArray();
                    AssetQueue.Clear();
                    AddToAssetFile(buffer);
                }
                return Task.FromResult(0);

            }
            catch
            {
                throw;
            }
        }
    }
}
