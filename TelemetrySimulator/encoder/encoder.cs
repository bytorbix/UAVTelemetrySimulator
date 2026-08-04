using TelemetrySimulator.Icd;

public class Encoder
{
    public byte[] BuildFrame(IcdDocument icd, Dictionary<string, double> resolvedValues, int correlatorValue)
    {

        int framesize = icd.Params.Sum(param => (param.Size + 7) / 8);
        byte[] payload = new byte[framesize];

        // build through flags + correlator param



        int bitOffset = 0;

        // walks icd.Params, inclusion test against correlatorValue, packs bytes
        foreach (IcdParam param in icd.Params)
        {
            int fieldBitOffset = bitOffset;
            bitOffset += param.Size;

            bool included = param.CorrValue == 0 || (correlatorValue & param.CorrValue) != 0;

            if (!included) continue;

            double value;
            if (param.Identifier == "correlator") value = correlatorValue;
            else if (!resolvedValues.TryGetValue(param.Identifier, out value))
            {
                continue;
            }

            value = Math.Clamp(value, (double)param.Min, (double)param.Max);

            byte[] bytes = param.Type == IcdDataType.INTEGER
                ? BitConverter.GetBytes((long)value)[..(param.Size / 8)]
                : BitConverter.GetBytes((float)value);

            if (param.Size % 8 == 0)
            {
                int byteOffset = fieldBitOffset / 8;
                Array.Copy(bytes, 0, payload, byteOffset, bytes.Length);
            }
            else
            {
                int byteOffset = fieldBitOffset / 8;
                int bitShift = fieldBitOffset % 8;
                int mask = (1 << param.Size) - 1;
                payload[byteOffset] |= (byte)(((int)value & mask) << bitShift);
            }
        }

        return payload;
    }
}
