﻿using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BoxOptions.Core;
using BoxOptions.Public.Models;
using Microsoft.AspNetCore.Mvc;
using BoxOptions.Core.Models;
using BoxOptions.Public.ViewModels;
using System.Text;
using BoxOptions.Services;

namespace BoxOptions.Public.Controllers
{
    [Route("api/[controller]")]
    public class LogController : Controller
    {
        private readonly ILogRepository _logRepository;

        public LogController(ILogRepository logRepository)
        {
            _logRepository = logRepository;
        }

        [HttpPost]
        public async Task<ActionResult> Post([FromBody] LogModel model)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            await _logRepository.InsertAsync(new LogItem
            {
                ClientId = model.ClientId,
                EventCode = model.EventCode,
                Message = model.Message
            });

            return Ok();
        }

        [HttpGet]
        public async Task<LogModel[]> Get([FromQuery] string dateFrom, [FromQuery] string dateTo,
            [FromQuery] string clientId)
        {
            const string format = "yyyyMMdd";
            var entities = await _logRepository.GetRange(DateTime.ParseExact(dateFrom, format, CultureInfo.InvariantCulture), DateTime.ParseExact(dateTo, format, CultureInfo.InvariantCulture), clientId);
            return entities.Select(e => new LogModel
            {
                ClientId = e.ClientId,
                EventCode = e.EventCode,
                Message = e.Message,
                Timestamp = e.Timestamp
            }).ToArray();
        }

        [HttpGet]
        [Route("boxoptionlogclientlist")]
        public async Task<IActionResult> ClientList([FromQuery] string dateFrom, [FromQuery] string dateTo)
        {
            try
            {
                const string format = "yyyyMMdd";
                var entities = await _logRepository.GetClients(DateTime.ParseExact(dateFrom, format, CultureInfo.InvariantCulture), DateTime.ParseExact(dateTo, format, CultureInfo.InvariantCulture).AddDays(1));

                return Ok(entities);
            }
            catch (Exception ex) { return StatusCode(500, ex.Message); }
        }

        [HttpGet]
        [Route("getall")]
        public async Task<ActionResult> GetAll([FromQuery] string dateFrom, [FromQuery] string dateTo)
        {
            const string format = "yyyyMMdd";
            var entities = await _logRepository.GetAll(DateTime.ParseExact(dateFrom, format, CultureInfo.InvariantCulture), DateTime.ParseExact(dateTo, format, CultureInfo.InvariantCulture).AddDays(1));

            var res = entities.Select(e => new LogModel
            {
                ClientId = e.ClientId,
                EventCode = e.EventCode,
                Message = e.Message,
                Timestamp = e.Timestamp
            }).ToArray();

            return View(res);

        }

        [HttpGet]
        [Route("clientlogs")]
        public async Task<ActionResult> ClientLogs()
        {
            try
            {
                ClientLogsViewModel model = new ClientLogsViewModel()
                {
                    EndDate = DateTime.UtcNow.Date,
                    StartDate = DateTime.UtcNow.Date.AddDays(-7)
                };

                var entities = await _logRepository.GetClients(model.StartDate, model.EndDate.AddDays(1));
                model.ClientList = (from l in entities
                                    select l.Substring(0,36)).Distinct().ToArray();
                return View(model);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
        [HttpPost]
        [Route("clientlogs")]
        public async Task<ActionResult> ClientLogs(ClientLogsViewModel model)
        {
            if (ModelState.IsValid)
            {
                try
                {
                    var entities = await _logRepository.GetRange(model.StartDate, model.EndDate.AddDays(1), model.Client);
                    if (entities.Count() > 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (var item in entities)
                        {
                            sb.AppendLine($"{item.Timestamp};{item.ClientId};{item.EventCode}-{(GameStatus)int.Parse(item.EventCode)};{item.Message}");
                        }
                        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"clientLogs_{model.Client}.csv");
                    }
                    else
                    {
                        return StatusCode(500, "Log file is empty");
                    }
                }
                catch (Exception ex)
                {
                    return StatusCode(500, ex.Message);
                }
            }
            else
                return StatusCode(500, "Invalid model");
        }
    }
}
