using System;
using System.Threading;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.InputSystem.LowLevel;

using static Minis.Backend.RtMidi;

namespace Minis.Backend
{
    /// <summary>
    /// A MIDI device, as reported by RtMidi.
    /// </summary>
    internal unsafe sealed class MidiPort : IDisposable
    {
        private readonly MidiBackend _backend;

        private RtMidiInHandle _portHandle;
        private readonly uint _portNumber;

        private readonly string _name;
        private readonly byte[] _nameBytes;

        private MidiChannel _allChannels;
        private readonly MidiChannel[] _channels = new MidiChannel[16];

        private Thread _readThread;
        private EventWaitHandle m_ThreadStop = new EventWaitHandle(false, EventResetMode.ManualReset);

        private bool _activeSensing;
        private double _lastMessageTime;

        public MidiPort(MidiBackend backend, uint portNumber)
        {
            _backend = backend;
            _portNumber = portNumber;

            _portHandle = rtmidi_in_create_default();
            if (_portHandle == null || _portHandle.IsInvalid)
                throw new Exception("Failed to create RtMidi handle!");

            rtmidi_open_port(_portHandle, portNumber, "RtMidi Input");
            rtmidi_in_ignore_types(_portHandle, midiSysex: true, midiTime: true, midiSense: false);
            if (!_portHandle.Ok)
            {
                string error = _portHandle.ErrorMessage;
                _portHandle.Dispose();
                throw new Exception($"Failed to open RtMidi port {portNumber}: {error}");
            }

            (_name, _nameBytes) = rtmidi_get_port_name(_portHandle, portNumber);
            if (!_portHandle.Ok)
            {
                _portHandle.Dispose();
                throw new Exception($"Failed to get port name: {_portHandle.ErrorMessage}");
            }

            _allChannels = new MidiChannel(_backend, _name, -1);

            _readThread = new Thread(ReadThread) { IsBackground = true };
            _readThread.Start();
        }

        public void Dispose()
        {
            m_ThreadStop?.Set();

            _readThread?.Join();
            _readThread = null;

            m_ThreadStop?.Dispose();
            m_ThreadStop = null;

            if (_portHandle != null)
            {
                _portHandle.Dispose();
                _portHandle = null;
            }

            foreach (var channel in _channels)
                DisposeChannel(channel);

            DisposeChannel(_allChannels);
        }

        private void DisposeChannel(MidiChannel channel)
        {
            if (channel == null || channel.device == null)
                return;

            _backend.QueueDeviceRemove(channel.device);
        }

        public bool IsAlive()
        {
            if (_portHandle == null)
            {
                return false;
            }

            lock (_portHandle)
            {
                if (_portHandle.IsInvalid ||
                    m_ThreadStop == null ||
                    m_ThreadStop.WaitOne(0))
                {
                    return false;
                }

                if (HasNameChanged())
                {
                    return false;
                }

                return true;
            }
        }

        private bool HasNameChanged()
        { 
            int length = 0;
            rtmidi_get_port_name(_portHandle, _portNumber, null, ref length);
            if (!_portHandle.Ok)
            {
                return true;
            }

            if (length != _nameBytes.Length)
            {
                return true;
            }

            byte* buffer = stackalloc byte[length];
            int result = rtmidi_get_port_name(_portHandle, _portNumber, buffer, ref length);
            if (!_portHandle.Ok)
            {
                return true;
            }

            fixed (byte* ptr = _nameBytes)
            {
                return UnsafeUtility.MemCmp(buffer, ptr, length) != 0;
            }
        }

        private void ReadThread()
        {
            const int retryThreshold = 3;
            int retryCount = 0;

            const int bufferSize = 1024;
            byte* message = stackalloc byte[bufferSize];

            while (!m_ThreadStop.WaitOne(1))
            {
                if (_activeSensing && Math.Abs(InputState.currentTime - _lastMessageTime) > 0.330)
                {
                    FullReset();
                    _activeSensing = false;
                }

                uint size;
                lock (_portHandle)
                {
                    UIntPtr tmpSize = (UIntPtr)bufferSize;
                    double timestamp = rtmidi_in_get_message(_portHandle, message, ref tmpSize);
                    size = (uint)tmpSize;

                    if (!_portHandle.Ok)
                    {
                        Debug.LogError($"[Minis] Failed to read MIDI message: {_portHandle.ErrorMessage}");
                        if (++retryCount >= retryThreshold)
                            break;
                        continue;
                    }
                }

                if (size < 1)
                    continue;

                HandleStatus(message[0], message + 1, size - 1);
            }

            m_ThreadStop.Set();
        }

        private void HandleStatus(byte status, byte* buffer, uint length)
        {
            _lastMessageTime = InputState.currentTime;
            switch (status & 0xF0)
            {
                case 0x80: // Note off
                {
                    if (length < 2)
                        break;

                    int channel = status & 0x0F;
                    byte note = buffer[0];
                    // byte velocity = buffer[1];

                    _allChannels.ProcessNoteOff(note);
                    GetChannelDevice(channel).ProcessNoteOff(note);
                    break;
                }
                case 0x90: // Note on
                {
                    if (length < 2)
                        break;

                    int channel = status & 0x0F;
                    byte note = buffer[0];
                    byte velocity = buffer[1];

                    // Velocity 0 is equivalent to a note off
                    if (velocity == 0)
                    {
                        _allChannels.ProcessNoteOff(note);
                        GetChannelDevice(channel).ProcessNoteOff(note);
                    }
                    else
                    {
                        _allChannels.ProcessNoteOn(note, velocity);
                        GetChannelDevice(channel).ProcessNoteOn(note, velocity);
                    }
                    break;
                }
                case 0xA0: // Note pressure
                {
                    if (length < 2)
                        break;

                    int channel = status & 0x0F;
                    byte note = buffer[0];
                    byte velocity = buffer[1];

                    _allChannels.ProcessNotePressure(note, velocity);
                    GetChannelDevice(channel).ProcessNotePressure(note, velocity);
                    break;
                }
                case 0xB0: // Control change
                {
                    if (length < 2)
                        break;

                    int channel = status & 0x0F;
                    byte control = buffer[0];
                    byte value = buffer[1];

                    switch (control)
                    {
                        // CC 120-127 are channel mode messages
                        case 120: // All Sound Off
                        {
                            _allChannels.FullReset();
                            GetChannelDevice(channel).FullReset();
                            break;
                        }
                        case 123: // All Notes Off
                        case 124: // Omni Off
                        case 125: // Omni On
                        case 126: // Mono On
                        case 127: // Poly On
                        {
                            // All of the above messages reset all notes
                            _allChannels.ResetAllNotes();
                            GetChannelDevice(channel).ResetAllNotes();
                            break;
                        }
                        case 121: // Reset All Controllers
                        {
                            _allChannels.ResetAllCC();
                            GetChannelDevice(channel).ResetAllCC();
                            break;
                        }
                        case 122: // Local Control
                        {
                            // Not necessary to handle, ignore
                            break;
                        }
                        default:
                        {
                            _allChannels.ProcessCC(control, value);
                            GetChannelDevice(channel).ProcessCC(control, value);
                            break;
                        }
                    }
                    break;
                }
                case 0xD0: // Channel pressure
                {
                    if (length < 2)
                        break;

                    int channel = status & 0x0F;
                    byte value = buffer[0];

                    _allChannels.ProcessChannelPressure(value);
                    GetChannelDevice(channel).ProcessChannelPressure(value);
                    break;
                }
                case 0xE0: // Pitch bend
                {
                    if (length < 2)
                        break;

                    int channel = status & 0x0F;
                    byte lsb = buffer[0];
                    byte msb = buffer[1];
                    ushort value = (ushort)((msb << 7) | lsb);

                    _allChannels.ProcessPitchBend(value);
                    GetChannelDevice(channel).ProcessPitchBend(value);
                    break;
                }
                case 0xF0: // System
                {
                    switch (status)
                    {
                        // case 0xF0: // System exclusive
                        // case 0xF1: // MIDI time code quarter frame
                        // case 0xF2: // Song position pointer
                        // case 0xF3: // Song select
                        // case 0xF4:
                        // case 0xF5:
                        // case 0xF6: // Tune request
                        // case 0xF7: // End of exclusive
                        // case 0xF8: // Timing clock
                        // case 0xF9:
                        case 0xFA: // Start
                        {
                            _allChannels.ProcessPlaybackButton(MidiPlaybackButton.Start);
                            foreach (var channel in _channels)
                            {
                                channel?.ProcessPlaybackButton(MidiPlaybackButton.Start);
                            }
                            break;
                        }
                        case 0xFB: // Continue
                        {
                            _allChannels.ProcessPlaybackButton(MidiPlaybackButton.Continue);
                            foreach (var channel in _channels)
                            {
                                channel?.ProcessPlaybackButton(MidiPlaybackButton.Continue);
                            }
                            break;
                        }
                        case 0xFC: // Stop
                        {
                            _allChannels.ProcessPlaybackButton(MidiPlaybackButton.Stop);
                            foreach (var channel in _channels)
                            {
                                channel?.ProcessPlaybackButton(MidiPlaybackButton.Stop);
                            }
                            break;
                        }
                        // case 0xFD:
                        case 0xFE: // Active sensing
                        {
                            _activeSensing = true;
                            break;
                        }
                        case 0xFF: // System reset
                        {
                            FullReset();
                            break;
                        }
                    }
                    break;
                }
            }
        }

        private MidiChannel GetChannelDevice(int channel)
        {
            if (_channels[channel] == null)
                _channels[channel] = new MidiChannel(_backend, _name, channel);

            return _channels[channel];
        }

        private void FullReset()
        {
            _allChannels.FullReset();
            for (int i = 0; i < _channels.Length; i++)
            {
                DisposeChannel(_channels[i]);
                _channels[i] = null;
            }
        }
    }
}
