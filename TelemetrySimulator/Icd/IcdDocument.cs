using System.Text.Json;

namespace TelemetrySimulator.Icd
{
    public class IcdDocument
    {
        public List<IcdParam> Params { get; init; }

        private Dictionary<string, IcdParam> _fieldLookup; // build up dict for O(1) future lookups

        public static IcdDocument Load(string json)
        {
            List<IcdParam>? ExtractedParams = JsonSerializer.Deserialize<List<IcdParam>>(json);

            if (ExtractedParams is null)
            {
                throw new InvalidOperationException("Failed to deserialize ICD document");
            }

            IcdDocument document = new IcdDocument { Params = ExtractedParams };

            document.BuildLookup();
            return document;
        }

        private void BuildLookup()
        {
            _fieldLookup = new Dictionary<string, IcdParam>();
            foreach (IcdParam param in Params)
            {
                if (!_fieldLookup.TryAdd(param.Identifier, param))
                {
                    throw new InvalidOperationException(
                        $"Duplicate ICD field identifier: '{param.Identifier}'.");
                }
            }
        }

        public bool TryGetField(string identifier, out IcdParam? param)
            => _fieldLookup.TryGetValue(identifier, out param);

        public IcdParam GetField(string identifier) => _fieldLookup[identifier];

    }
}
