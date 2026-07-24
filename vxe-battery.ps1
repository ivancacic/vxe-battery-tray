<#
    vxe-battery.ps1  -  POC battery monitor for ATK GEAR / VXE R1 SE+
    Reads battery straight from the wireless dongle over raw HID (no vendor software).

    Protocol (reverse-engineered; confirmed on this device):
      VID 0x373B  PID 0x1085.  Vendor comms channel = HID collection with
      UsagePage 0xFF02, 17-byte Output + 17-byte Input reports (report ID 0x08).
      Request  (Output report): [0x08][0x04][0..][checksum]  checksum = 0x4D - cmd
      Response (Input report) : [0x08][0x04][..][pct@6][charge@7][mV_hi@8][mV_lo@9]
        pct     = byte 6   (0-100 %)
        charging= byte 7   (1 = charging, 0 = on battery)
        voltage = (byte8 << 8) | byte9   millivolts

    Usage:  powershell -ExecutionPolicy Bypass -File .\vxe-battery.ps1 [-Watch] [-IntervalSec 30]
#>
[CmdletBinding()]
param(
    [switch]$Watch,
    [int]$IntervalSec = 30
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Hid
{
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)] struct DID { public int cbSize; public Guid g; public uint f; public IntPtr r; }
    [StructLayout(LayoutKind.Sequential)] struct CAPS {
        public ushort Usage, UsagePage; public ushort InLen, OutLen, FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort n1,n2,n3,n4,n5,n6,n7,n8,n9;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr h, uint f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, uint i, ref DID a);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr h, ref DID a, IntPtr d, uint sz, ref uint req, IntPtr dd);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sec, uint c, uint fl, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteFile(IntPtr h, byte[] b, uint n, out uint w, byte[] o);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadFile(IntPtr h, byte[] b, uint n, out uint r, byte[] o);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateEvent(IntPtr a, bool manual, bool init, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetOverlappedResult(IntPtr h, byte[] o, out uint n, bool wait);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CancelIo(IntPtr h);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int  HidP_GetCaps(IntPtr p, out CAPS c);

    // Find the vendor comms collection: UsagePage 0xFF02 with 17-byte In+Out reports.
    public static string FindCommsPath(ushort vid, ushort pid)
    {
        string match = string.Format("vid_{0:x4}&pid_{1:x4}", vid, pid);
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        var did = new DID(); did.cbSize = Marshal.SizeOf(did);
        string best = null;
        for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did); i++)
        {
            uint req = 0; SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref req, IntPtr.Zero);
            IntPtr det = Marshal.AllocHGlobal((int)req); Marshal.WriteInt32(det, IntPtr.Size==8?8:6);
            if (SetupDiGetDeviceInterfaceDetail(set, ref did, det, req, ref req, IntPtr.Zero))
            {
                string path = Marshal.PtrToStringUni((IntPtr)(det.ToInt64()+4));
                if (path != null && path.ToLower().Contains(match))
                {
                    IntPtr h = CreateFile(path, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h != (IntPtr)(-1)) {
                        IntPtr pp;
                        if (HidD_GetPreparsedData(h, out pp)) {
                            CAPS c; HidP_GetCaps(pp, out c); HidD_FreePreparsedData(pp);
                            if (c.OutLen == 17 && c.InLen == 17) { best = path; if (c.UsagePage == 0xFF02) { CloseHandle(h); Marshal.FreeHGlobal(det); break; } }
                        }
                        CloseHandle(h);
                    }
                }
            }
            Marshal.FreeHGlobal(det);
        }
        SetupDiDestroyDeviceInfoList(set);
        return best;
    }

    // Write output report, read the input report reply (overlapped, timeout-guarded). Returns 17 bytes or null.
    public static byte[] Query(string path, byte cmd, int timeoutMs)
    {
        const int len = 17;
        IntPtr h = CreateFile(path, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h == (IntPtr)(-1)) return null;
        try {
            byte[] req = new byte[len]; req[0]=0x08; req[1]=cmd; req[len-1]=(byte)(0x4D-cmd);
            int ovSize = 8*2 + 8 + IntPtr.Size;

            byte[] ovW = new byte[ovSize]; IntPtr evW = CreateEvent(IntPtr.Zero, true, false, null);
            BitConverter.GetBytes(evW.ToInt64()).CopyTo(ovW, ovSize-IntPtr.Size);
            uint w; if (!WriteFile(h, req, len, out w, ovW) && Marshal.GetLastWin32Error()==997) { WaitForSingleObject(evW, (uint)timeoutMs); GetOverlappedResult(h, ovW, out w, false); }

            byte[] resp = new byte[len]; byte[] ovR = new byte[ovSize]; IntPtr evR = CreateEvent(IntPtr.Zero, true, false, null);
            BitConverter.GetBytes(evR.ToInt64()).CopyTo(ovR, ovSize-IntPtr.Size);
            uint r;
            if (!ReadFile(h, resp, len, out r, ovR) && Marshal.GetLastWin32Error()==997) {
                if (WaitForSingleObject(evR, (uint)timeoutMs)==0) GetOverlappedResult(h, ovR, out r, false);
                else { CancelIo(h); return null; }
            }
            return resp;
        } finally { CloseHandle(h); }
    }
}
"@

$VxeVid = 0x373B; $VxePid = 0x1085

function Get-VxeBattery {
    $path = [Hid]::FindCommsPath($VxeVid, $VxePid)
    if (-not $path) {
        return [pscustomobject]@{ Found=$false; Reason='VXE dongle (VID_373B&PID_1085) not found. Plug in the receiver / turn the mouse on.' }
    }
    # cmd 0x04 = full battery report (percent + charge flag + voltage)
    $resp = [Hid]::Query($path, [byte]0x04, 1000)
    if (-not $resp) {
        return [pscustomobject]@{ Found=$false; Reason='Dongle found but no reply. The mouse may be asleep — give it a wiggle and retry.' }
    }
    if ($resp[1] -ne 0x04) {
        return [pscustomobject]@{ Found=$false; Reason=('Unexpected reply: ' + (($resp | ForEach-Object { $_.ToString("X2") }) -join ' ')) }
    }
    $pct = [int]$resp[6]
    [pscustomobject]@{
        Found      = $true
        Percent    = [Math]::Min(100, $pct)
        Charging   = ($resp[7] -eq 1)
        Volts      = [Math]::Round((([int]$resp[8] -shl 8) -bor [int]$resp[9]) / 1000.0, 2)
    }
}

function Show-Battery($b) {
    $ts = Get-Date -Format 'HH:mm:ss'
    if (-not $b.Found) { Write-Host "[$ts] " -NoNewline; Write-Host $b.Reason -ForegroundColor Yellow; return }
    $color = if ($b.Percent -ge 50) {'Green'} elseif ($b.Percent -ge 20) {'Yellow'} else {'Red'}
    $barLen = 20; $fill = [int]([Math]::Round($b.Percent / 100 * $barLen))
    $bar = ('#' * $fill).PadRight($barLen, '-')
    Write-Host "[$ts] VXE R1 SE+  " -NoNewline
    Write-Host ("[{0}] " -f $bar) -ForegroundColor $color -NoNewline
    Write-Host ("{0,3}%" -f $b.Percent) -ForegroundColor $color -NoNewline
    Write-Host ("  {0}" -f $(if($b.Charging){'CHARGING'}else{'on battery'})) -NoNewline
    if ($b.Volts) { Write-Host ("  {0:N2} V" -f $b.Volts) -ForegroundColor DarkGray -NoNewline }
    Write-Host ""
}

Write-Host "VXE R1 SE+ battery monitor (POC)" -ForegroundColor Cyan
if ($Watch) {
    Write-Host "Polling every $IntervalSec s. Ctrl+C to stop.`n" -ForegroundColor DarkGray
    while ($true) { Show-Battery (Get-VxeBattery); Start-Sleep -Seconds $IntervalSec }
} else {
    Show-Battery (Get-VxeBattery)
}
