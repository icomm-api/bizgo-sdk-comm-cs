namespace Bizgo.Internal
{
    /// <summary>Documented HTTP status and Korean description of a service-layer code.</summary>
    internal readonly struct ServiceCode
    {
        internal ServiceCode(int httpStatus, string description)
        {
            HttpStatus = httpStatus;
            Description = description;
        }

        internal int HttpStatus { get; }

        internal string Description { get; }
    }

    internal static partial class ServiceCodes
    {
        internal static bool TryGet(string code, out ServiceCode value) => Table.TryGetValue(code, out value);
    }
}
