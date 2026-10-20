# Investigation & Post-Mortem: MIDI Disconnect Crash on Windows 11

## Overview

When disconnecting a USB MIDI device (such as an Alesis e-drum kit) while running YARG on Windows 11, the application abruptly crashed. Two distinct crash mechanisms occurred depending on the teardown sequence:
1. **Error Code `0xc0000409` (`FAST_FAIL_FATAL_APP_EXIT` / `ucrtbase!abort`)**: Process terminated instantly when an exception was thrown from inside `RtMidi`'s C++ destructor during `rtmidi_in_free`.
2. **Error Code `0xc0000374` (`STATUS_HEAP_CORRUPTION` / `DOUBLE_FREE`)**: Process terminated when `closePort()` was called a second time (or during destruction) because `connected_` was never cleared after `midiInClose` failed.

---

## Native Crash Dump Analyses

### Crash 1: Destructor Exception (`0xc0000409`)
* **Process**: `YARG.exe`
* **Exception Code**: `0xc0000409` (`FAST_FAIL_FATAL_APP_EXIT` triggered via `int 29h`)
* **Call Stack**:
  ```text
  ucrtbase!abort+0x4e
  ucrtbase!terminate+0x1e
  VCRUNTIME140!FindHandler<__FrameHandler3>+0x476
  KERNELBASE!RaiseException+0x8a
  VCRUNTIME140!_CxxThrowException+0x99
  RtMidi!MidiApi::error+0x2f1
  RtMidi!~MidiInWinMM+0x15
  RtMidi!rtmidi_in_free+0x2f
  ```

### Crash 2: Heap Corruption Double Free (`0xc0000374`)
* **Process**: `Unity.exe` (Editor Play Mode)
* **Exception Code**: `0xc0000374` (`STATUS_HEAP_CORRUPTION`)
* **Bucket**: `HEAP_CORRUPTION_ACTIONABLE_BlockNotBusy_DOUBLE_FREE_c0000374_RtMidi.dll!unknown_function`
* **Call Stack**:
  ```text
  ntdll!RtlReportFatalFailure+0x9
  ntdll!RtlReportCriticalFailure+0xa9
  ntdll!RtlpHeapHandleError+0x12
  ntdll!RtlpHpHeapHandleError+0x7a
  ntdll!RtlFreeHeap+0x6da
  msvcrt!free+0x1c
  RtMidi!closePort+0x70
  RtMidi!rtmidi_close_port+0x11
  Minis.Native.RtMidiInHandle.ReleaseHandle()
  ```

---

## Root Cause Breakdown

### 1. Windows 11 MIDI Services (`midisrv`)
In Windows 11 24H2+, Microsoft introduced **Windows MIDI Services** (`midisrv`). Legacy WinMM calls (`midiInClose`) route through an internal shim.
* On Windows 10, calling `midiInClose` on an unplugged USB MIDI device returned `MMSYSERR_NOERROR`.
* On Windows 11, calling `midiInClose` on an unplugged USB device returns a driver error (`MMSYSERR_INVALHANDLE`) because the underlying device handle is already invalidated by the OS plug-and-play manager.

### 2. Destructor and Double-Free Bugs in `RtMidi.dll` (`MidiInWinMM::closePort`)
In `RtMidi.dll` (`MidiInWinMM`):
```cpp
void MidiInWinMM::closePort( void )
{
  if ( connected_ ) {
    for ( int i=0; i<2; i++ ) {
      midiInUnprepareHeader( data->inHandle, data->sysexBuffer[i], sizeof(MIDIHDR) );
      delete [] data->sysexBuffer[i]->lpData;
      delete data->sysexBuffer[i];
    }
    result = midiInClose( data->inHandle );
    if ( result != MMSYSERR_NOERROR ) {
      error( RtMidiError::DRIVER_ERROR, "MidiInWinMM::closePort: error closing Windows MM MIDI input port." );
      return;
    }
    connected_ = false;
  }
}

MidiInWinMM::~MidiInWinMM()
{
  closePort();
  ...
}
```

This implementation contains two critical bugs when `midiInClose` fails:
1. **Uncaught Exception in Destructor**: If `closePort()` is not explicitly called beforehand, `~MidiInWinMM()` invokes `closePort()`. When `midiInClose` fails, `error()` throws `RtMidiError` inside a C++ destructor, triggering `std::terminate()` -> `ucrtbase!abort` (`0xc0000409`).
2. **Buffer Double Free**: Notice `sysexBuffer[i]` is deleted **before** calling `midiInClose`. When `midiInClose` fails, `closePort()` throws and aborts without setting `connected_ = false;`. The pointers in `sysexBuffer` are now dangling. Any subsequent call to `closePort()`—or when `~MidiInWinMM()` is called during `rtmidi_in_free`—sees `connected_ == true` and calls `delete` on `sysexBuffer` a second time, immediately triggering an NT heap double-free corruption (`0xc0000374`).

---

## Implemented Solution in Minis

### 1. Safe Port Closure & Connection Invariant Correction (`SafeClosePort` & `MarkClosed`)
In `Backend/RtMidi.cs`, we introduced `SafeClosePort` and `MarkClosed`:
```csharp
public static unsafe void SafeClosePort(IntPtr handle)
{
    if (handle == IntPtr.Zero)
    {
        return;
    }

    try
    {
        rtmidi_close_port(handle);
    }
    finally
    {
        MarkClosed(handle);
    }
}

internal static unsafe void MarkClosed(IntPtr handle)
{
    if (handle == IntPtr.Zero)
    {
        return;
    }

    var wrapper = (RtMidiHandle.RtMidiWrapper*)handle;
    if (wrapper->ptr == IntPtr.Zero)
    {
        return;
    }

    var rtMidi = (byte*)wrapper->ptr;
    var rtapi = *(byte**)(rtMidi + IntPtr.Size);
    if (rtapi != null)
    {
        int connectedOffset = IntPtr.Size == 8 ? 0x10 : 0x08;
        *(rtapi + connectedOffset) = 0;
    }
}
```

1. `rtmidi_close_port` executes `closePort()` inside native `try/catch`, freeing the sysex buffers and absorbing the `RtMidiError`.
2. `MarkClosed` immediately zeroes out `connected_` (`rtapi_ + 0x10`).
3. When `rtmidi_in_free` subsequently runs `~MidiInWinMM()`, `closePort()` checks `if (connected_)` and sees `false`, immediately returning without double-freeing buffers or calling `midiInClose`.

### 2. Single Source of Truth Lifecycle in SafeHandle
Handle teardown is unified exclusively in `SafeHandle.ReleaseHandle`:
```csharp
protected override bool ReleaseHandle()
{
    RtMidi.SafeClosePort(handle);
    RtMidi.rtmidi_in_free(handle);
    return true;
}
```
Redundant manual calls to `rtmidi_close_port` in `MidiPort.Dispose()` were removed. `_portHandle.Dispose()` is the single lifecycle authority.

### 3. Backwards Compatibility Bridge
In `Runtime/Controls/MidiButtonControl.cs`:
```csharp
[System.Obsolete("Use MidiButtonControl instead.")]
public class MidiNoteControl : ButtonControl {}

public class MidiButtonControl : MidiNoteControl
```
Preserves compatibility with existing YARG binding code.
