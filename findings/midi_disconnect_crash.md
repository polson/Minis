# Investigation & Post-Mortem: MIDI Disconnect Crash on Windows 11

## Overview

When disconnecting a USB MIDI device (such as an Alesis e-drum kit) while running YARG on Windows 11, the application abruptly crashed with error code `0xc0000409` (`FAST_FAIL_FATAL_APP_EXIT` / `STATUS_STACK_BUFFER_OVERRUN`). No managed C# exception was reported in the application logs; instead, the process aborted instantly at the native C runtime level (`ucrtbase!abort`).

This document details the root cause, native call stack, Windows 11 architectural changes, and the dual-layer fix implemented in Minis.

---

## Native Crash Analysis

### Crash Dump Signature
* **Process**: `YARG.exe` (and applicable to `Unity.exe` in Editor Play Mode)
* **Exception Code**: `0xc0000409` (`FAST_FAIL_FATAL_APP_EXIT` triggered via `int 29h`)
* **Faulting Module**: `ucrtbase.dll`
* **Triggering Module**: `RtMidi.dll`

### Native Call Stack
```text
ucrtbase!abort+0x4e
ucrtbase!terminate+0x1e
VCRUNTIME140!FindHandler<__FrameHandler3>+0x476
VCRUNTIME140!__InternalCxxFrameHandler<__FrameHandler3>+0x208
VCRUNTIME140!__CxxFrameHandler3+0x68
ntdll!RtlpExecuteHandlerForException+0xf
ntdll!RtlDispatchException+0x437
ntdll!RtlRaiseException+0x221
KERNELBASE!RaiseException+0x8a
VCRUNTIME140!_CxxThrowException+0x99
RtMidi!MidiApi::error+0x2f1
RtMidi!MidiOutApi::MidiOutApi+0x433
RtMidi!RtMidiIn::`default constructor closure'+0x11a
RtMidi!rtmidi_in_free+0x2f
Minis.Native.RtMidiInHandle.ReleaseHandle()
```

---

## Root Cause Breakdown

### 1. The Windows 11 MIDI Services Architecture
In modern Windows 11 builds (24H2 / build 26100+), Microsoft introduced **Windows MIDI Services** (`midisrv`), a complete redesign of the Windows MIDI subsystem supporting MIDI 2.0. Legacy WinMM API calls (`winmm.dll`, `midiInOpen`, `midiInClose`) are routed through an internal translation shim to `midisrv`.

* **Legacy WinMM (Windows 10 / earlier Win 11)**: Calling `midiInClose` on an active port whose USB cable had been physically disconnected cleaned up the driver state and returned `MMSYSERR_NOERROR`.
* **Windows 11 MIDI Services Shim**: Calling `midiInClose` on a physically disconnected USB device fails because the underlying device handle is already invalidated by the OS plug-and-play manager, returning a driver error code (`MMSYSERR_INVALHANDLE` or `MIDIERR_STILLPLAYING`).

### 2. Flawed Destructor Error Handling in RtMidi
Inside `RtMidi`'s Windows MM implementation (`RtMidi.cpp` / `MidiInWinMM::closePort`):
```cpp
result = midiInClose( data->inHandle );
if ( result != MMSYSERR_NOERROR ) {
    LeaveCriticalSection( &(data->mutex) );
    errorString = "MidiInWinMM::closePort: error closing Windows MM MIDI input port.";
    error( RtMidiError::DRIVER_ERROR, errorString );
}
```
`error()` executes:
```cpp
throw RtMidiError( errorString, type );
```
When `rtmidi_in_free(handle)` is called, it executes `delete (RtMidiIn*) device->ptr`. If the port was not explicitly closed beforehand, the C++ destructor `~RtMidiIn()` invokes `closePort()`.

In C++11 and later, all destructors are `noexcept(true)` by default. Throwing an exception from within a destructor—or across an unmanaged P/Invoke boundary where no C++ catch block exists—violates runtime invariants. The C++ runtime immediately invokes `std::terminate()`, which calls `ucrtbase!abort` and triggers a fast-fail exit (`0xc0000409`), immediately killing the host process.

### 3. Minis Teardown Lifecycle
In `TheNathannator/Minis` (branch `yarg`), `MidiBackend.OnUpdate()` polls `rtmidi_get_port_count` each frame. When a cable is unplugged:
1. `portCount != _lastPortCount` is detected.
2. `MidiBackend` invokes `MidiPort.Dispose()` on all active ports.
3. Older builds of Minis called `_portHandle.Dispose()` directly without first calling `rtmidi_close_port(_portHandle)`.
4. While Nathan added an explicit `rtmidi_close_port` call to `MidiPort.Dispose()` in commit `1172820` (November 2025), YARG's `Packages/packages-lock.json` was pinned to commit `29dee9f7` from October 2024, leaving YARG running the defective teardown code.

---

## Implemented Fix

### Layer 1: Exception-Safe Port Closure in C Wrapper
The C wrapper function `rtmidi_close_port` in `RtMidi` wraps `closePort()` in a `try / catch` block:
```cpp
void rtmidi_close_port (RtMidiPtr device) {
    try {
        ((RtMidi*)device->ptr)->closePort();
    }
    catch (const RtMidiError &error) {
        device->ok = false;
        device->msg = error.what();
    }
}
```
When `rtmidi_close_port` is called on a disconnected device, the `RtMidiError` thrown by `midiInClose` is absorbed inside the native layer. The port is marked `open = false`. When the native object is subsequently freed via `rtmidi_in_free`, the destructor sees `open == false` and does not attempt `midiInClose` again.

### Layer 2: SafeHandle Release Safety Net
To guard against any scenario where a handle is disposed directly or finalized by the garbage collector without `MidiPort.Dispose()`, `RtMidiInHandle.ReleaseHandle` and `RtMidiOutHandle.ReleaseHandle` in `Backend/RtMidi.cs` were updated to always invoke `rtmidi_close_port` prior to `rtmidi_in_free`:
```csharp
protected override bool ReleaseHandle()
{
    RtMidi.rtmidi_close_port(handle);
    RtMidi.rtmidi_in_free(handle);
    return true;
}
```

### Layer 3: Backwards Compatibility Bridge
During Nathan's refactor, `MidiNoteControl` was renamed to `MidiButtonControl`. In `Runtime/Controls/MidiButtonControl.cs`, `MidiNoteControl` was reinstated as a base class:
```csharp
[System.Obsolete("Use MidiButtonControl instead.")]
public class MidiNoteControl : ButtonControl {}

public class MidiButtonControl : MidiNoteControl
```
This preserves binary and source compatibility with existing YARG binding code (`ButtonBindGroup.cs:44`).

---

## Alternative OS-Level Workaround (`UseLegacyMidi`)

Microsoft released an optional Windows 11 update (`KB5121767`) introducing a compatibility toggle for applications suffering from disconnect crashes under the new MIDI stack:
* **Registry Key**: `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Drivers32`
* **DWORD**: `UseLegacyMidi = 1`

Setting `UseLegacyMidi = 1` forces Windows 11 to revert to the legacy `wdmaud.drv` pipeline, where `midiInClose` returns `MMSYSERR_NOERROR` on disconnect. While this workaround works without code modifications, it relies on legacy OS fallback paths that Microsoft plans to phase out. The software fix in Minis ensures stability on both modern and legacy Windows MIDI stacks.
