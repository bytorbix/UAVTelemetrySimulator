using System.Text.Json;

namespace TelemetrySimulator.Icd
{
    public class IcdDocument
    {
        public List<IcdGroup> Groups { get; init; }

        private Dictionary<string, IcdField> _fieldLookup; // build up dict for O(1) future lookups

        public static IcdDocument Load(string json)
        {
            IcdDocument? document = JsonSerializer.Deserialize<IcdDocument>(json);

            if (document is null)
            {
                throw new InvalidOperationException("Failed to deserialize ICD document");
            }

            if (document.Groups is null)
            {
                throw new InvalidOperationException("ICD document has no 'Groups'.");
            }

            document.Validate();
            document.BuildLookup();

            return document;

        }

        private void Validate()
        {
            foreach (IcdGroup group in Groups)
            {
                foreach (IcdField field in group.Fields)
                {   // Checking if 
                    if (field.Type == IcdDataType.Float && field.Size != 32)
                    {
                        throw new InvalidOperationException(
                            $"Field '{field.Identifier}' is Float but declares Size={field.Size}. Float fields must be 32 bits.");
                    }
                }
            }
        }

        private void BuildLookup()
        {
            _fieldLookup = new Dictionary<string, IcdField>();
            foreach (IcdGroup group in Groups)
            {
                foreach(IcdField field in group.Fields)
                {
                    if (!_fieldLookup.TryAdd(field.Identifier, field))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate ICD field identifier: '{field.Identifier}'.");
                    }
                }
            }
        }

        public bool TryGetField(string identifier, out IcdField? field) 
            => _fieldLookup.TryGetValue(identifier, out field);

        public IcdField GetField(string identifier) => _fieldLookup[identifier];

    }
}
