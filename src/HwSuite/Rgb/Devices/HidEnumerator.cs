using HidSharp;
using HwSuite.Core;

namespace HwSuite.Lighting.Devices;

public sealed class HidInfo
{
    public string Path { get; init; } = "";
    public int Vid { get; init; }
    public int Pid { get; init; }
    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public int MaxFeature { get; init; }
    public int MaxInput { get; init; }
    public int MaxOutput { get; init; }
    public List<(ushort Page, ushort Usage)> Usages { get; init; } = new();
    public HidDevice Device { get; init; } = null!;

    public bool HasUsage(ushort page, ushort usage) => Usages.Any(u => u.Page == page && u.Usage == usage);
    public string UsageText => string.Join(", ", Usages.Select(u => $"{u.Page:X4}:{u.Usage:X4}"));
    public override string ToString() => $"{Vid:X4}:{Pid:X4} {Product} [{UsageText}] feat={MaxFeature} in={MaxInput} out={MaxOutput}";
}

public static class HidEnumerator
{
    public static List<HidInfo> Find(int vid, int? pid = null)
    {
        var result = new List<HidInfo>();
        if (RunMode.Refuse($"enumerate HID devices {vid:X4}")) return result;
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(vid, pid))
            {
                var usages = new List<(ushort, ushort)>();
                try
                {
                    var rd = d.GetReportDescriptor();
                    foreach (var item in rd.DeviceItems)
                        foreach (var u in item.Usages.GetAllValues())
                            usages.Add(((ushort)(u >> 16), (ushort)(u & 0xFFFF)));
                }
                catch (Exception ex) { Log.Warn($"HID descriptor read failed for {d.DevicePath}: {ex.Message}"); }

                string product = "", manufacturer = "";
                try { product = d.GetProductName(); } catch { }
                try { manufacturer = d.GetManufacturer(); } catch { }
                int feat = 0, inp = 0, outp = 0;
                try { feat = d.GetMaxFeatureReportLength(); } catch { }
                try { inp = d.GetMaxInputReportLength(); } catch { }
                try { outp = d.GetMaxOutputReportLength(); } catch { }

                result.Add(new HidInfo
                {
                    Path = d.DevicePath, Vid = d.VendorID, Pid = d.ProductID, Product = product, Manufacturer = manufacturer,
                    MaxFeature = feat, MaxInput = inp, MaxOutput = outp, Usages = usages, Device = d
                });
            }
        }
        catch (Exception ex) { Log.Error("HID enumeration failed", ex); }
        return result;
    }
}
