using System.Runtime.InteropServices;
using System;

namespace Minis.Backend
{
    internal enum RtMidiApi
    {
        Unspecified,
        MacOsXCore,
        LinuxAlsa,
        UnixJack,
        WindowsMM,
        RtMidiDummy,
        WebMidiApi,
        WindowsUwp,
        Android
    }

    internal enum RtMidiErrorType
    {
        Warning,
        DebugWarning,
        Unspecified,
        NoDevicesFound,
        InvalidDevice,
        MemoryError,
        InvalidParameter,
        InvalidUse,
        DriverError,
        SystemError,
        ThreadError
    }

    internal unsafe abstract class RtMidiHandle : SafeHandle
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RtMidiWrapper
        {
            public IntPtr ptr;
            public IntPtr data;
            [MarshalAs(UnmanagedType.U1)]
            public bool ok;
            public IntPtr msg;
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        private RtMidiWrapper* Ptr => (RtMidiWrapper*)handle;

        public bool Ok => !IsInvalid && Ptr->ok;
        public string ErrorMessage => !IsInvalid ? Marshal.PtrToStringAnsi(Ptr->msg) : null;

        protected RtMidiHandle(bool ownsHandle)
            : base(IntPtr.Zero, ownsHandle)
        {
        }
    }

    internal sealed class RtMidiInHandle : RtMidiHandle
    {
        private RtMidiInHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            RtMidi.rtmidi_close_port(handle);
            RtMidi.rtmidi_in_free(handle);
            return true;
        }
    }

    internal sealed class RtMidiOutHandle : RtMidiHandle
    {
        private RtMidiOutHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle()
        {
            RtMidi.rtmidi_close_port(handle);
            RtMidi.rtmidi_out_free(handle);
            return true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal unsafe delegate void RtMidiCCallback(
        double timeStamp,
        byte* message, // const unsigned char*
        UIntPtr messageSize, // size_t
        void* userData
    );

    internal static class RtMidi
    {
#if !UNITY_EDITOR && (UNITY_IOS || UNITY_WEBGL)
        internal const string DllName = "__Internal";
#else
        internal const string DllName = "RtMidi";
#endif

        #region System

        [DllImport(DllName)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string rtmidi_get_version(); // -> const char*

        [DllImport(DllName)]
        public static unsafe extern int rtmidi_get_compiled_api(
            RtMidiApi* apis,
            uint apis_size
        );

        [DllImport(DllName)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string rtmidi_api_name( // -> const char*
            RtMidiApi api
        );

        [DllImport(DllName)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static extern string rtmidi_api_display_name( // -> const char*
            RtMidiApi api
        );

        [DllImport(DllName)]
        public static extern RtMidiApi rtmidi_compiled_api_by_name(
            [MarshalAs(UnmanagedType.LPStr)] string name // const char*
        );

        #endregion

        #region Ports

        [DllImport(DllName)]
        public static extern void rtmidi_open_port(
            RtMidiHandle device,
            uint portNumber,
            [MarshalAs(UnmanagedType.LPStr)] string portName // const char*
        );

        [DllImport(DllName)]
        public static extern void rtmidi_open_virtual_port(
            RtMidiHandle device,
            [MarshalAs(UnmanagedType.LPStr)] string portName // const char*
        );

        [DllImport(DllName)]
        public static extern void rtmidi_close_port(
            RtMidiHandle device
        );

        [DllImport(DllName)]
        public static extern void rtmidi_close_port(
            IntPtr device
        );

        [DllImport(DllName)]
        public static extern uint rtmidi_get_port_count(
            RtMidiHandle device
        );

        [DllImport(DllName)]
        [return: MarshalAs(UnmanagedType.LPStr)]
        public static unsafe extern int rtmidi_get_port_name(
            RtMidiHandle device,
            uint portNumber,
            byte* bufOut, // char*
            ref int bufLen // int*
        );

        public static unsafe (string, byte[]) rtmidi_get_port_name(RtMidiHandle device, uint portNumber)
        {
            int length = 0;
            rtmidi_get_port_name(device, portNumber, null, ref length);
            if (!device.Ok)
            {
                return (null, null);
            }

            byte[] bytes = new byte[length];
            fixed (byte* ptr = bytes)
            {
                int result = rtmidi_get_port_name(device, portNumber, ptr, ref length);
                if (!device.Ok)
                {
                    return (null, null);
                }

                string text = Marshal.PtrToStringAnsi((IntPtr)ptr);
                return (text, bytes);
            }
        }

        #endregion

        #region Input

        [DllImport(DllName)]
        public static extern RtMidiInHandle rtmidi_in_create_default();

        [DllImport(DllName)]
        public static extern RtMidiInHandle rtmidi_in_create(
            RtMidiApi api,
            [MarshalAs(UnmanagedType.LPStr)] string clientName, // const char*
            uint queueSizeLimit
        );

        [DllImport(DllName)]
        public static extern void rtmidi_in_free(
            IntPtr device
        );

        [DllImport(DllName)]
        static extern RtMidiApi rtmidi_in_get_current_api(
            RtMidiInHandle device
        );

        [DllImport(DllName)]
        public static unsafe extern void rtmidi_in_set_callback(
            RtMidiInHandle device,
            RtMidiCCallback callback,
            void* userData
        );

        [DllImport(DllName)]
        public static extern void rtmidi_in_cancel_callback(
            RtMidiInHandle device
        );

        [DllImport(DllName)]
        public static extern void rtmidi_in_ignore_types(
            RtMidiInHandle device,
            [MarshalAs(UnmanagedType.U1)] bool midiSysex,
            [MarshalAs(UnmanagedType.U1)] bool midiTime,
            [MarshalAs(UnmanagedType.U1)] bool midiSense
        );

        [DllImport(DllName)]
        public static unsafe extern double rtmidi_in_get_message(
            RtMidiInHandle device,
            byte* message, // unsigned char*
            ref UIntPtr size // size_t*
        );

        #endregion

        #region Output

        #endregion

        [DllImport(DllName)]
        public static extern RtMidiOutHandle rtmidi_out_create_default();

        [DllImport(DllName)]
        public static extern RtMidiOutHandle rtmidi_out_create(
            RtMidiApi api,
            [MarshalAs(UnmanagedType.LPStr)] string clientName // const char*
        );

        [DllImport(DllName)]
        public static extern void rtmidi_out_free(
            IntPtr device
        );

        [DllImport(DllName)]
        public static extern RtMidiApi rtmidi_out_get_current_api(
            RtMidiOutHandle device
        );

        [DllImport(DllName)]
        public static unsafe extern int rtmidi_out_send_message(
            RtMidiOutHandle device,
            byte* message, // const unsigned char*
            int length
        );
    }
}
