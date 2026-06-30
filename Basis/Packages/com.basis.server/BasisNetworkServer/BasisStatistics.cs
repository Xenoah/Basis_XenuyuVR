using Basis.Network.Core;
using System;
using System.Threading;

namespace Basis.Network.Server
{
    public static class BasisStatistics
    {
        public static NetManager Manager;
        private static Thread workerThread;
        private static volatile bool keepPolling = true; // thread lifecycle の制御に使う

        public static void StartWorkerThread(NetManager manager)
        {
          //  Manager = manager;

            // worker thread を開始する
           // workerThread = new Thread(PollStatistics);
          //  workerThread.IsBackground = true; // background thread は main application 終了時に自動終了する
          //  workerThread.Start();
        }

        public static void StopWorkerThread()
        {
          //  keepPolling = false;

          // worker thread が正常に終了するのを待つ
           // if (workerThread != null && workerThread.IsAlive)
          //  {
            //    workerThread.Join();
           // }
        }

        private static void PollStatistics()
        {
           /// while (keepPolling)
           // {
           //     // manager から statistics を poll する
             //   PollLatestStatistics();

            //    // 次の poll 前に少し待つ (例: 毎秒)
             //   Thread.Sleep(15000); // 必要に応じて delay を調整できる
        //    }
        }

        public static void PollLatestStatistics()
        {
        //  BNL.Log("Packet Loss: " + Manager.Statistics.PacketLoss + "Packet Loss Percent: " + Manager.Statistics.PacketLossPercent + "Bytes Received: " + Manager.Statistics.BytesReceived + "Bytes Sent: " + Manager.Statistics.BytesSent + "Packets Sent: " + Manager.Statistics.PacketsSent);
        }
    }
}
