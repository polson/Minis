using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace Minis
{
    [System.Obsolete("Use MidiButtonControl instead.")]
    public class MidiNoteControl : ButtonControl
    {
    }

    /// <summary>
    /// A note input on a MIDI device.
    /// </summary>
    public class MidiButtonControl : MidiNoteControl
    {
        public int minValue = 0;
        public int maxValue = 127;
        public int zeroPoint = 0;

        internal static void Initialize()
        {
            InputSystem.RegisterLayout<MidiButtonControl>("MidiButton");
        }

        public MidiButtonControl()
        {
            m_StateBlock.format = InputStateBlock.FormatBit;
            m_StateBlock.sizeInBits = 7;

            // ButtonControl parameters
            pressPoint = 1.0f / 127;
        }

        public override unsafe float ReadUnprocessedValueFromState(void* statePtr)
        {
            int value = stateBlock.ReadInt(statePtr);
            return Normalize(value, minValue, maxValue, zeroPoint);
        }

        // Borrowed from PlasticBand's IntegerAxisControl
        // Properly ensures 0-point value results in a value of 0f
        private static float Normalize(int value, int minValue, int maxValue, int zeroPoint)
        {
            if (value >= maxValue)
                return 1f;
            else if (value <= minValue)
                return minValue != zeroPoint ? -1f : 0f;

            int max;
            int min;
            float @base;
            if (value >= zeroPoint)
            {
                max = maxValue;
                min = zeroPoint;
                @base = 0f;
            }
            else
            {
                max = zeroPoint;
                min = minValue;
                @base = -1f;
            }

            float percentage;
            if (max == min) // Prevent divide-by-0
            {
                percentage = value >= max ? 1f : 0f;
            }
            else
            {
                percentage = (float) (value - min) / (max - min);
            }

            float normalized = @base + percentage;
            return normalized;
        }
    }
}
