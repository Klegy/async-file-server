﻿using System.Collections.Generic;
using System.Linq;
using AaronLuna.Common.Linq;

namespace AaronLuna.AsyncSocketServer
{
    public static class ServerEventFilter
    {
        public static List<ServerEvent> ApplyFilter(this List<ServerEvent> log, LogLevel logLevel)
        {
            log.RemoveAll(DoNotDisplayInLog);

            if (logLevel == LogLevel.Debug)
            {
                log.RemoveAll(LogLevelIsTraceOnly);
            }

            if (logLevel == LogLevel.Info)
            {
                log.RemoveAll(LogLevelIsTraceOnly);
                log.RemoveAll(LogLevelIsDebugOnly);
            }

            return
                log.DistinctBy(e => new { e.TimeStamp, e.EventType })
                    .OrderBy(e => e.TimeStamp)
                    .ToList();
        }

        static bool DoNotDisplayInLog(ServerEvent serverEvent)
        {
            return serverEvent.DoNotDisplayInLog;
        }

        static bool LogLevelIsTraceOnly(ServerEvent serverEvent)
        {
            return serverEvent.LogLevelIsTraceOnly;
        }

        static bool LogLevelIsDebugOnly(ServerEvent serverEvent)
        {
            return serverEvent.LogLevelIsDebugOnly;
        }
    }
}
