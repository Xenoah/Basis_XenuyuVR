using System;
using System.Collections.Generic;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;

// Wine/Proton 上で OS-codec video engine を起動するための gate。
//
// native plugin は Windows Media Foundation と D3D11 keyed-mutex shared texture を使う。
// Windows-on-Linux compatibility layer (Wine や Valve の Proton) ではそれらが欠落または
// 不完全な場合があり、初期化で process が hard crash する可能性がある。これは managed try/catch
// では回復できない native access violation になる。そのため Wine/Proton では、user が dialog で
// 明示的に許可するまで native plugin に触れない。判断は session ごとに一度だけ行われ、全
// BasisMediaPlayer で共有される。許可後に Media Foundation が無いと判明した場合も、
// 再生ではなく load failure として clean に扱う (catch 可能)。
//
// native Windows / Android / Quest ではこの gate は透過的。RequiresGate は false で、
// Request() は即座に allowed path を実行する。
public static class BasisVideoProtonGate
{
    private enum Decision { Unknown, Allowed, Denied }

    private static Decision _decision = Decision.Unknown;
    private static bool _prompting;
    private static readonly List<(Action onAllowed, Action onDenied)> _waiters = new List<(Action, Action)>();

    // Wine/Proton host 上で、load 前に確認が必要な場合だけ true。
    public static bool RequiresGate => BasisProtonDetection.IsWine;

    // この session で user が prompt に回答済みなら true。
    public static bool Decided => _decision != Decision.Unknown;

    // engine の load が許可されている場合 true (native Windows、または user が許可)。
    public static bool Allowed => !RequiresGate || _decision == Decision.Allowed;

    // engine を今起動できるなら onAllowed、できないなら onDenied を実行する。
    // 判断待ちの間 callback は queue され、user 回答後にまとめて解決されるため、
    // 複数 player がいても dialog は一度だけ出る。
    public static void Request(Action onAllowed, Action onDenied)
    {
        if (!RequiresGate || _decision == Decision.Allowed) { onAllowed?.Invoke(); return; }
        if (_decision == Decision.Denied) { onDenied?.Invoke(); return; }

        _waiters.Add((onAllowed, onDenied));
        if (!_prompting) Prompt();
    }

    // 次の load で再確認するために prompt を再装填する (例: settings toggle から)。
    public static void Reset() => _decision = Decision.Unknown;

    private static void Prompt()
    {
        _prompting = true;
        string ver = BasisProtonDetection.WineVersion;
        string host = string.IsNullOrEmpty(ver) ? "Proton/Wine" : $"Proton/Wine ({ver})";

        if (!BasisMainMenu.Instance) BasisMainMenu.Open();

        var panel = BasisMenuDialoguePanel.CreateNew(
            "Media Player",
            $"This session is running through {host}. The media player needs Windows Media Foundation, " +
            "which may not be installed in this compatibility layer. If it is missing, video will not play " +
            "(and initializing it can be unstable here). Try to load the media player anyway?",
            "Load", "Skip",
            Resolve);

        if (panel == null)
        {
            // まだ確認用 menu が無い。現在待機中の load は skip して fail safe に倒し、
            // crash を避ける。ただし decision は Unknown のまま残し、後で menu が使える load 時に
            // 再確認する。session 全体から video を締め出さないため。
            _prompting = false;
            var pending = _waiters.ToArray();
            _waiters.Clear();
            foreach (var w in pending) w.onDenied?.Invoke();
        }
    }

    private static void Resolve(bool accepted)
    {
        _decision = accepted ? Decision.Allowed : Decision.Denied;
        _prompting = false;

        var pending = _waiters.ToArray();
        _waiters.Clear();
        foreach (var w in pending)
        {
            if (accepted) w.onAllowed?.Invoke();
            else w.onDenied?.Invoke();
        }
    }
}
