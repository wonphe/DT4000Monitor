using System;
using System.Timers;

namespace DT4000Monitor
{
    class Program
    {
        static void Main(string[] args)
        {
            var monitor = new Monitor()
            {
                ServerIP = "10.220.67.65",
                ServerPort = 8135,
                ClientIP = "10.220.67.66",
                ClientPort = 4660,
                BeeperCount = 3
            };
            monitor.Listen();

            // 监听DT4000状态
            var timer = new Timer()
            {
                Enabled = true,
                Interval = 150000
            };
            timer.Elapsed += new ElapsedEventHandler(monitor.TimeMonitor);
            timer.Start();

            Console.ReadKey();
        }
    }
}
