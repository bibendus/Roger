﻿using System.Diagnostics;
using System.Text;

namespace Tests.Integration.Utils
{
    public class TcpTrace
    {
        private static string tcpTraceExecutablePath;
        private Process process;

        public TcpTrace(string tcpTraceExecutablePath)
        {
            TcpTrace.tcpTraceExecutablePath = tcpTraceExecutablePath;
        }

        public void Start(int listenPort, string serverName, int serverPort, string title)
        {
            var args = new StringBuilder("/listen ")
                .Append(listenPort)
                .Append(" /serverPort ")
                .Append(serverPort)
                .Append(" /serverName ")
                .Append(serverName)
                .Append(" /title \"")
                .Append(title)
                .Append("\"");

            process = StartProcess(args.ToString());
        }

        private static Process StartProcess(string arguments)
        {
            return Process.Start(new ProcessStartInfo(tcpTraceExecutablePath, arguments)
            {
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        public void Stop()
        {
            if (process != null) 
                process.Kill();
        }

        public static void StopAll()
        {
            StartProcess("/kill");
        }
    }
}