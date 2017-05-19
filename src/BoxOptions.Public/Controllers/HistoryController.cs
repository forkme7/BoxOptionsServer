﻿using BoxOptions.Common;
using BoxOptions.Core;
using BoxOptions.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;


namespace BoxOptions.Public.Controllers
{
    [Route("api/[controller]")]
    public class HistoryController: Controller
    {
        IBoxOptionsHistory history;

        public HistoryController(IBoxOptionsHistory history)
        {
            this.history = history;
        }

        [HttpGet]
        [Route("bidhistory")]
        public async Task<IActionResult> BidHistory(DateTime dtFrom, DateTime dtTo, string assetPair)
        {
            if (history != null)
            {
                try
                {
                    Core.AssetQuote[] res = null;
                    var his = await history.GetAssetHistory(dtFrom, dtTo, assetPair);
                    res = new Core.AssetQuote[his.Count];
                    if (res.Length > 0)
                    {
                        his.CopyTo(res, 0);
                        var bidhistory = AssetBidProcessor.CreateBidHistory(assetPair, res);
                        return Ok(bidhistory);
                    }
                    else
                        return Ok("history is empty");
                }
                catch (Exception ex)
                {
                    return StatusCode(500, ex.Message);
                }
            }
            else
                return StatusCode(500, "History Not Available");

        }

        [HttpGet]
        [Route("assethistory")]
        public async Task<IActionResult> AssetHistory(DateTime dtFrom, DateTime dtTo, string assetPair)
        {
            if (history != null)
            {
                try
                {
                    var his = await history.GetAssetHistory(dtFrom, dtTo, assetPair);
                    return Ok(his);                    
                }
                catch (Exception ex)
                {
                    return StatusCode(500, ex.Message);
                }
            }
            else
                return StatusCode(500, "History Not Available");

        }
    }
}