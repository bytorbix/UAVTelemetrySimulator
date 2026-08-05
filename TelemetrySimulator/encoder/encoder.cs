using System.Dynamic;
using TelemetrySimulator.Icd;

public class Encoder
{

    const int BITS_PER_BYTE = 8;
    const string CORRELATOR_PARAM_NAME = "correlator";

    public byte[] BuildFrame(IcdDocument icd, Dictionary<string, double> resolvedValues, int correlatorValue)
    {
        byte[] frame = new byte[CalculateFrameSize(icd)];

        int bitOffset = 0;

        // walks icd.Params, inclusion test against correlatorValue, packs bytes
        foreach (IcdParam param in icd.Params)
        {
            int fieldBitOffset = bitOffset;
            bitOffset += param.Size;

            if (!IsIncluded(param, correlatorValue)) continue;

            if (!TryResolveValue(param, resolvedValues, correlatorValue, out double value)) continue;

            WriteField(frame, param, value, fieldBitOffset);
        }

        return frame;
    }

    private static int CalculateFrameSize(IcdDocument icd)
        => (icd.Params.Sum(param => param.Size) + BITS_PER_BYTE - 1) / BITS_PER_BYTE;

    private static bool IsIncluded(IcdParam param, int correlatorValue)
        => param.CorrValue == 0 || (correlatorValue & param.CorrValue) != 0;

    private static bool TryResolveValue(IcdParam param, Dictionary<string, double> resolvedValues, int correlatorValue, out double value)
    {
        if (param.Identifier == CORRELATOR_PARAM_NAME)
        {
            value = correlatorValue;
            return true;
        }

        return resolvedValues.TryGetValue(param.Identifier, out value);
    }

    private static void WriteField(byte[] payload, IcdParam param, double value, int fieldBitOffset)
    {
        value = Math.Clamp(value, param.Min, param.Max);

        if (param.Size % BITS_PER_BYTE == 0)
        {
            // FLOAT is always 4 raw bytes
            byte[] bytes = param.Type == IcdDataType.INTEGER
                ? BitConverter.GetBytes((long)value)[..(param.Size / BITS_PER_BYTE)]
                : BitConverter.GetBytes((float)value);

            int byteOffset = fieldBitOffset / BITS_PER_BYTE;
            Array.Copy(bytes, 0, payload, byteOffset, bytes.Length);
        }
        else
        {
            int byteOffset = fieldBitOffset / BITS_PER_BYTE;
            int bitShift = fieldBitOffset % BITS_PER_BYTE;
            int mask = (1 << param.Size) - 1;
            // (byte) cast drops any bits past position 8, so fields crossing a byte boundary get truncated.
            payload[byteOffset] |= (byte)(((int)value & mask) << bitShift);
        }
    }
}
