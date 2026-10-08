using NAudio.CoreAudioApi;

namespace VRCGuiter.VirtualMic;

public sealed record EndpointInfo(MMDevice Device, string Id, string Name, string Adapter);

public sealed record CablePair(string Kind, string Label, EndpointInfo Render, EndpointInfo Capture, int Priority)
{
    public bool IsVbCable => Kind == "vbcable";
    public string MicName => Capture.Name;
}

public static class VirtualCable
{
    public const string VbAdapterName = "VB-Audio Virtual Cable";
    public const string VrcgName = "VRCG";

    public static string AdapterOf(MMDevice d)
    {
        try { return d.DeviceFriendlyName ?? ""; } catch { return ""; }
    }

    public static List<EndpointInfo> Snapshot(MMDeviceEnumerator en, DataFlow flow)
    {
        var list = new List<EndpointInfo>();
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            string name;
            try { name = d.FriendlyName; } catch { continue; }
            list.Add(new EndpointInfo(d, d.ID, name, AdapterOf(d)));
        }
        return list;
    }

    public static List<CablePair> Detect(MMDeviceEnumerator en) =>
        Detect(Snapshot(en, DataFlow.Render), Snapshot(en, DataFlow.Capture));

    public static List<CablePair> Detect(List<EndpointInfo> renders, List<EndpointInfo> captures)
    {
        var list = new List<CablePair>();

        var vbr = renders.FirstOrDefault(d => d.Adapter == VbAdapterName && d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        var vbc = captures.FirstOrDefault(d => d.Adapter == VbAdapterName &&
                    (d.Name.StartsWith(VrcgName, StringComparison.OrdinalIgnoreCase) || d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase)))
                  ?? captures.FirstOrDefault(d => d.Adapter == VbAdapterName);
        if (vbr != null && vbc != null)
            list.Add(new CablePair("vbcable", "VRCG（VB-CABLE）", vbr, vbc, 0));

        foreach (var adapter in renders.Select(r => r.Adapter).Distinct())
        {
            if (adapter == VbAdapterName || adapter.Length == 0) continue;
            if (!LooksVirtual(adapter)) continue;
            var rr = renders.Where(d => d.Adapter == adapter).ToList();
            var cc = captures.Where(d => d.Adapter == adapter).ToList();
            if (rr.Count == 1 && cc.Count == 1)
            {
                int pri = adapter.Contains("MagicMic", StringComparison.OrdinalIgnoreCase) ? 5
                        : adapter.Contains("Steam", StringComparison.OrdinalIgnoreCase) ? 6 : 10;
                list.Add(new CablePair("other", adapter, rr[0], cc[0], pri));
            }
        }
        return list.OrderBy(p => p.Priority).ToList();
    }

    public static CablePair? FindVbCable(MMDeviceEnumerator en) => Detect(en).FirstOrDefault(p => p.IsVbCable);

    public static bool LooksVirtual(string adapter) =>
        adapter.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        adapter.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
        adapter.Contains("Streaming Microphone", StringComparison.OrdinalIgnoreCase) ||
        adapter.Contains("MagicMic", StringComparison.OrdinalIgnoreCase) ||
        adapter.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        adapter.Contains("Steam", StringComparison.OrdinalIgnoreCase);
}
