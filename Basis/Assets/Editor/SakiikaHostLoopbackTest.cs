#if UNITY_EDITOR
using Basis.Network.Core;
using System;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reproduces the host-mode loopback failure outside the full game:
///   Unity.exe -batchmode -quit -projectPath Basis
///             -executeMethod SakiikaHostLoopbackTest.Run
/// Phase 1: NetworkServer (in-process, host-mode Configuration) + raw LiteNetLib
///          client → connect to 127.0.0.1. Any response (accept OR reject) proves
///          the transport path works; silent timeout reproduces the bug.
/// Phase 2: same client against a plain LiteNetLib echo server, to isolate
///          NetworkServer from LiteNetLib itself.
/// </summary>
public static class SakiikaHostLoopbackTest
{
    private const int TestPort = 4297;

    public static void Run()
    {
        int exit = 0;
        try
        {
            Debug.Log("[LoopbackTest] Phase 1: NetworkServer + LiteNetLib client");
            string phase1 = TestAgainstNetworkServer();
            Debug.Log($"[LoopbackTest] Phase 1 result: {phase1}");

            Debug.Log("[LoopbackTest] Phase 2: plain LiteNetLib server + client");
            string phase2 = TestAgainstPlainLnlServer();
            Debug.Log($"[LoopbackTest] Phase 2 result: {phase2}");

            Debug.Log($"[LoopbackTest] SUMMARY phase1={phase1} phase2={phase2}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LoopbackTest] FAILED: {ex}");
            exit = 2;
        }
        EditorApplication.Exit(exit);
    }

    private static string TestAgainstNetworkServer()
    {
        var config = new Configuration
        {
            IPv4Address = "localhost",
            SetPort = TestPort,
            HasFileSupport = false,
            UseAuthIdentity = true,
            UseAuth = true,
            Password = "testpw",
            ServerName = "LoopbackTest",
            PeerLimit = 8,
            NetworkStackId = string.Empty,
        };
        NetworkServer.StartServer(config);
        Thread.Sleep(500);
        try
        {
            return ConnectRawClient(TestPort, includeBasisHandshake: true);
        }
        finally
        {
            NetworkServer.StopServer();
        }
    }

    private static string TestAgainstPlainLnlServer()
    {
        var serverListener = new LiteNetLib.EventBasedNetListener();
        serverListener.ConnectionRequestEvent += request =>
        {
            Debug.Log($"[LoopbackTest] plain server got ConnectionRequest from {request.RemoteEndPoint}");
            request.Reject();
        };
        var server = new LiteNetLib.NetManager(serverListener)
        {
            UnsyncedEvents = true,
            IPv6Enabled = true,
            UseNativeSockets = true,
        };
        server.Start(System.Net.IPAddress.Any, System.Net.IPAddress.IPv6Any, TestPort + 1);
        Thread.Sleep(200);
        try
        {
            return ConnectRawClient(TestPort + 1, includeBasisHandshake: false);
        }
        finally
        {
            server.Stop();
        }
    }

    private static string ConnectRawClient(int port, bool includeBasisHandshake)
    {
        string result = "timeout(no events)";
        var mre = new ManualResetEventSlim(false);

        var listener = new LiteNetLib.EventBasedNetListener();
        listener.PeerConnectedEvent += peer =>
        {
            result = "CONNECTED";
            mre.Set();
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            result = $"DISCONNECTED({info.Reason})";
            mre.Set();
        };

        var client = new LiteNetLib.NetManager(listener)
        {
            UnsyncedEvents = true,
            IPv6Enabled = true,
            UseNativeSockets = true,
            ChannelsCount = BasisNetworkCommons.TotalChannels,
        };
        client.Start();

        var writer = new LiteNetLib.Utils.NetDataWriter();
        if (includeBasisHandshake)
        {
            // Same leading fields NetworkClient sends: version + auth bytes. The
            // ready-message payload is garbage on purpose — a reject response still
            // proves packets flow both ways on loopback.
            writer.Put(BasisNetworkVersion.ServerVersion);
            byte[] pw = Encoding.UTF8.GetBytes("testpw");
            writer.Put((ushort)pw.Length);
            writer.Put(pw);
        }
        else
        {
            writer.Put((byte)1);
        }

        client.Connect("127.0.0.1", port, writer);
        mre.Wait(TimeSpan.FromSeconds(12));
        client.Stop();
        return result;
    }
}
#endif
