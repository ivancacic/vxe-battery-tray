# VXE R1 SE+ battery HID protocol

Notes on how `vxe-battery-tray` reads the battery, for anyone porting to another
model or language. Reverse-engineered with reference to
[OpenVXE](https://github.com/BuSd777/OpenVXE) and
[svetikas/battery](https://github.com/svetikas/battery), and verified against a
physical R1 SE+ on Windows 11.

## Device

| | |
| --- | --- |
| USB Vendor ID | `0x373B` |
| USB Product ID | `0x1085` |
| USB product string | `Wireless mouse -1k dongle` |

The 2.4 GHz receiver exposes several HID top-level collections. The battery
lives on the **vendor collection**, identified by its capabilities rather than
by name:

| Property | Value |
| --- | --- |
| Usage Page | `0xFF02` |
| Input report length | 17 bytes |
| Output report length | 17 bytes |
| Feature report length | 0 (none) |
| Report ID | `0x08` |

Because there is **no feature report**, the exchange uses an **output report**
(host → device) followed by an **input report** (device → host). `HidD_SetFeature`
does not work here — that was the main trap during development.

On Windows the matching interface path looks like:

```
\\?\hid#vid_373b&pid_1085&mi_01&col05#...#{4d1e55b2-f16f-11cf-88cb-001111000030}
```

Select it programmatically by opening each `vid_373b&pid_1085` HID interface and
picking the one whose caps report `UsagePage == 0xFF02` and `In == Out == 17`.

## Request

Write a 17-byte **output report**:

```
byte 0  : 0x08          report ID
byte 1  : cmd           command (0x04 for full battery info)
byte 2..15 : 0x00
byte 16 : checksum      = 0x4D - cmd
```

The checksum is a simple `0x4D - cmd`. Observed pairs: `cmd 0x04 → 0x49`,
`cmd 0x17 → 0x36`.

## Response

Read a 17-byte **input report**. For `cmd = 0x04`:

```
byte 0  : 0x08          report ID (echo)
byte 1  : 0x04          command echo
byte 6  : percent       battery %, 0..100
byte 7  : charging      1 = charging, 0 = on battery
byte 8  : voltage high  \  millivolts, big-endian
byte 9  : voltage low   /  (byte8 << 8) | byte9
```

Example reply at 95%, unplugged, ~4.10 V:

```
08 04 00 00 00 02 5F 00 10 00 00 00 00 00 00 00 D8
                  ^^          ^^ ^^
               0x5F=95     0x1000 = 4096 mV
                     ^^ 0x00 = not charging
```

## Command variants

Two battery commands exist in the wild; the correct one is model-dependent:

- **`0x04`** — full report: percent (byte 6), charging flag (byte 7), voltage
  (bytes 8–9). This is the correct command for the **R1 SE+**.
- **`0x17`** — used by OpenVXE for some models; battery = `byte 5 * 2`, capped at
  100. On the R1 SE+ this returns a *different, non-battery* value, so `0x04` is
  used here.

If you're adapting this to another VXE mouse and the number looks wrong, try the
other command and re-check which byte holds a sane 0..100 value.

## Robustness notes

- Use an **overlapped** read with a timeout (~800 ms) so the call can't hang if
  the mouse is asleep and never replies. Cancel the I/O on timeout.
- A sleeping mouse may not answer the first poll; a wiggle wakes it. The tray app
  surfaces this as *"No reply — mouse may be asleep"* rather than silently showing
  a stale value as if it were current.
- **After system resume the receiver takes several seconds to re-enumerate**, so
  the first read after waking will usually fail. Don't treat one failure as
  "device gone": listen for `PowerModeChanged`/`Resume` and `WM_DEVICECHANGE`,
  then retry on a short backoff (this app uses 1.5s → 3 → 6 → 10 → 20 → 30).
  Re-resolve the interface path on every attempt — the old path is invalid after
  re-enumeration.
